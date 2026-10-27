using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.Modules.Reconciliation;

public static class ReconciliationModule
{
    public static IServiceCollection AddReconciliationModule(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<ReconciliationDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "reconciliation")));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<IInvoiceQueries, InvoiceQueries>();
        services.AddScoped<ListInvoicesHandler>();
        services.AddScoped<IExactMatchCandidateQueries, ExactMatchCandidateQueries>();
        services.AddScoped<GetExactMatchesHandler>();
        services.AddScoped<GetSmartMatchesHandler>();
        services.AddScoped<IBankTransactionRepository, BankTransactionRepository>();
        services.AddScoped<IAllocationRepository, AllocationRepository>();
        services.AddScoped<IAllocationIdempotencyLookup, AllocationIdempotencyLookup>();
        services.AddScoped<IBatchExecutionStore, BatchExecutionStore>();
        services.AddScoped<IBatchExecutionOwner, BatchExecutionOwner>();
        services.AddScoped<IBatchExactMatchItemExecutor, BatchExactMatchItemExecutor>();
        services.AddScoped<ICompetitorProbeQueries, CompetitorProbeQueries>();
        services.AddScoped<IOpenReviewBlockQueries, OpenReviewBlockQueries>();
        services.TryAddSingleton<IBatchExactMatchItemSynchronization, ImmediateBatchItemSynchronization>();
        services.AddScoped<PreviewBatchExactMatchesHandler>();
        services.AddScoped<ExecuteBatchExactMatchesHandler>();
        services.AddScoped<ConfirmExactMatchHandler>();
        services.AddScoped<ConfirmExplicitAmountHandler>();
        services.AddScoped<IReviewCaseRepository, ReviewCaseRepository>();
        services.AddScoped<IReviewCaseSnapshotLookup, ReviewCaseSnapshotLookup>();
        services.AddScoped<IReviewCaseOpenConflict, ReviewCaseOpenConflict>();
        services.AddScoped<IAllocationLinkLookup, AllocationLinkLookup>();
        services.AddScoped<IReviewCaseQueries, ReviewCaseQueries>();
        services.AddScoped<OpenReviewCaseHandler>();
        services.AddScoped<ResolveReviewCaseHandler>();
        services.AddScoped<DismissReviewCaseHandler>();
        services.AddScoped<ListReviewCasesHandler>();
        services.AddScoped<GetReviewCaseHandler>();
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<ReconciliationDbContext>());
        services.AddSingleton<IImportFileReader, CsvHelperImportFileReader>();
        services.AddScoped<ICsvImportStore, CsvImportStore>();
        services.AddScoped<ImportCsvHandler>();

        return services;
    }
}