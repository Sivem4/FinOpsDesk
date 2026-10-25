using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;

using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmAllocationHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ConfirmAllocationHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IAllocationRepository allocationRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(allocationRepository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _allocationRepository = allocationRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ConfirmAllocationResult>> Handle(
        ConfirmAllocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(
            organizationId,
            command.InvoiceId,
            cancellationToken);

        if (invoice is null)
        {
            return Result<ConfirmAllocationResult>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        BankTransaction? bankTransaction = await _bankTransactionRepository.GetByIdAsync(
            organizationId,
            command.BankTransactionId,
            cancellationToken);

        if (bankTransaction is null)
        {
            return Result<ConfirmAllocationResult>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        Result applied = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            command.Amount);

        if (applied.IsFailure)
        {
            return Result<ConfirmAllocationResult>.Failure(applied.Error);
        }

        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            command.Amount,
            _timeProvider.GetUtcNow());

        _allocationRepository.Add(allocation);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result<ConfirmAllocationResult>.Failure(
                ReconciliationApplicationErrors.ConcurrencyConflict);
        }

        return Result<ConfirmAllocationResult>.Success(
            new ConfirmAllocationResult(allocation.Id));
    }
}