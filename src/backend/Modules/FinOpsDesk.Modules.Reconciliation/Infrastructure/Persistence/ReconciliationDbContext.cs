using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

public sealed class ReconciliationDbContext : DbContext, IUnitOfWork
{
    private readonly IFinancialCommitGuard _commitGuard;

    public ReconciliationDbContext(DbContextOptions<ReconciliationDbContext> options)
        : this(options, NoFinancialCommitGuard.Instance)
    {
    }

    public ReconciliationDbContext(
        DbContextOptions<ReconciliationDbContext> options,
        IFinancialCommitGuard commitGuard)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(commitGuard);

        _commitGuard = commitGuard;
    }

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

    public DbSet<Allocation> Allocations => Set<Allocation>();

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    public DbSet<ReviewCase> ReviewCases => Set<ReviewCase>();

    public DbSet<ReviewCaseAuditEntry> ReviewCaseAuditEntries => Set<ReviewCaseAuditEntry>();

    public DbSet<ReviewCaseOpenAttempt> ReviewCaseOpenAttempts => Set<ReviewCaseOpenAttempt>();

    public DbSet<ReviewCaseComment> ReviewCaseComments => Set<ReviewCaseComment>();

    public DbSet<BatchExecution> BatchExecutions => Set<BatchExecution>();

    public DbSet<FinancialAuditEntry> FinancialAuditEntries => Set<FinancialAuditEntry>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_commitGuard.RequiresCommitAuthorization)
        {
            return await base.SaveChangesAsync(cancellationToken);
        }

        if (Database.CurrentTransaction is not null)
        {
            try
            {
                await _commitGuard.ProtectAsync(Database.CurrentTransaction.GetDbTransaction(), cancellationToken);
                return await base.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await RollbackQuietlyAsync(Database.CurrentTransaction, cancellationToken);
                throw;
            }
        }

        await using IDbContextTransaction transaction = await Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _commitGuard.ProtectAsync(transaction.GetDbTransaction(), cancellationToken);
            int count = await base.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw;
        }
    }

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(
                "The persisted data was modified by another operation.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new UniqueConstraintConflictException(
                "A unique constraint rejected the commit.",
                exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reconciliation");

        modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
        modelBuilder.ApplyConfiguration(new BankTransactionConfiguration());
        modelBuilder.ApplyConfiguration(new AllocationConfiguration());
        modelBuilder.ApplyConfiguration(new ImportBatchConfiguration());
        modelBuilder.ApplyConfiguration(new ReviewCaseConfiguration());
        modelBuilder.ApplyConfiguration(new ReviewCaseAuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ReviewCaseOpenAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new ReviewCaseCommentConfiguration());
        modelBuilder.ApplyConfiguration(new BatchExecutionConfiguration());
        modelBuilder.ApplyConfiguration(new FinancialAuditEntryConfiguration());
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }
        }

        return false;
    }

    private static async Task RollbackQuietlyAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
    }
}