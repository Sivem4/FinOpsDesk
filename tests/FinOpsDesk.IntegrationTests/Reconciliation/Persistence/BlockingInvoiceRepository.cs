using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal sealed class BlockingInvoiceRepository : IInvoiceRepository
{
    private readonly IInvoiceRepository _inner;
    private readonly TaskCompletionSource _reachedLoad =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    public BlockingInvoiceRepository(IInvoiceRepository inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    public Task ReachedLoad => _reachedLoad.Task;

    public void Release()
    {
        _release.TrySetResult();
    }

    public async Task<Invoice?> GetByIdAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reachedLoad.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await _inner.GetByIdAsync(
            organizationId,
            invoiceId,
            cancellationToken);
    }
}