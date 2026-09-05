using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

public sealed class ReconciliationDbContext : DbContext, IUnitOfWork
{
    public ReconciliationDbContext(DbContextOptions<ReconciliationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

    public DbSet<Allocation> Allocations => Set<Allocation>();

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
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reconciliation");

        modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
        modelBuilder.ApplyConfiguration(new BankTransactionConfiguration());
        modelBuilder.ApplyConfiguration(new AllocationConfiguration());
    }
}