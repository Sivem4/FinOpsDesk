using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;

internal sealed class CsvImportStore : ICsvImportStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CsvImportStore> _logger;

    public CsvImportStore(
        IServiceScopeFactory scopeFactory,
        ILogger<CsvImportStore> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ImportBatch?> FindBatchByFileIdentityAsync(
        OrganizationId organizationId,
        ImportType importType,
        SourceSystem sourceSystem,
        FileHash fileHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(fileHash);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        return await context.ImportBatches
            .AsNoTracking()
            .Include(batch => batch.RowErrors)
            .SingleOrDefaultAsync(
                batch => batch.OrganizationId == organizationId
                    && batch.ImportType == importType
                    && batch.SourceSystem == sourceSystem
                    && batch.FileHash == fileHash,
                cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, InvoiceImportFacts>> FindInvoicesByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(externalIds);

        var facts = new Dictionary<string, InvoiceImportFacts>(StringComparer.Ordinal);

        if (externalIds.Count == 0)
        {
            return facts;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        foreach (ExternalId[] chunk in ToExternalIds(externalIds).Chunk(ImportLimits.LookupChunkSize))
        {
            List<Invoice> matches = await ImportSourceQueries
                .MatchingInvoices(context.Invoices.AsNoTracking(), organizationId, sourceSystem, chunk)
                .ToListAsync(cancellationToken);

            foreach (Invoice invoice in matches)
            {
                if (invoice.SourceIdentity is null)
                {
                    continue;
                }

                facts[invoice.SourceIdentity.ExternalId.Value] = new InvoiceImportFacts(
                    invoice.SourceIdentity.ExternalId.Value,
                    invoice.InvoiceNumber,
                    invoice.TotalAmount);
            }
        }

        return facts;
    }

    public async Task<IReadOnlyDictionary<string, BankTransactionImportFacts>> FindBankTransactionsByExternalIdsAsync(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(externalIds);

        var facts = new Dictionary<string, BankTransactionImportFacts>(StringComparer.Ordinal);

        if (externalIds.Count == 0)
        {
            return facts;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        foreach (ExternalId[] chunk in ToExternalIds(externalIds).Chunk(ImportLimits.LookupChunkSize))
        {
            List<BankTransaction> matches = await ImportSourceQueries
                .MatchingBankTransactions(
                    context.BankTransactions.AsNoTracking(),
                    organizationId,
                    sourceSystem,
                    chunk)
                .ToListAsync(cancellationToken);

            foreach (BankTransaction transaction in matches)
            {
                if (transaction.SourceIdentity is null)
                {
                    continue;
                }

                facts[transaction.SourceIdentity.ExternalId.Value] = new BankTransactionImportFacts(
                    transaction.SourceIdentity.ExternalId.Value,
                    transaction.Amount,
                    transaction.BookingDate,
                    transaction.Reference);
            }
        }

        return facts;
    }

    public async Task<ImportCommitResult> CommitAsync(
        ImportCommitWork work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        if (work.Invoices.Count > 0)
        {
            context.Invoices.AddRange(work.Invoices);
        }

        if (work.BankTransactions.Count > 0)
        {
            context.BankTransactions.AddRange(work.BankTransactions);
        }

        context.ImportBatches.Add(work.Batch);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new ImportCommitResult(ImportCommitStatus.Committed);
        }
        catch (DbUpdateException exception) when (TryGetSqlNumber(exception, out int number))
        {
            if (number is 2601 or 2627)
            {
                _logger.LogWarning(
                    "Import commit hit unique SQL error {SqlErrorNumber}.",
                    number);

                return new ImportCommitResult(ImportCommitStatus.UniqueConflict);
            }

            if (number == 1205)
            {
                _logger.LogWarning("Import commit hit SQL deadlock 1205.");

                return new ImportCommitResult(ImportCommitStatus.Deadlock);
            }

            throw;
        }
    }

    private static ExternalId[] ToExternalIds(IReadOnlyCollection<string> externalIds)
    {
        return externalIds
            .Distinct(StringComparer.Ordinal)
            .Select(value => new ExternalId(value))
            .ToArray();
    }

    private static bool TryGetSqlNumber(Exception exception, out int number)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                number = sqlException.Number;
                return true;
            }
        }

        number = 0;
        return false;
    }
}