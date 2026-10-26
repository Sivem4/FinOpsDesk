using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmExactMatchHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IAllocationIdempotencyLookup _idempotencyLookup;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ConfirmExactMatchHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IAllocationRepository allocationRepository,
        IAllocationIdempotencyLookup idempotencyLookup,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(allocationRepository);
        ArgumentNullException.ThrowIfNull(idempotencyLookup);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _allocationRepository = allocationRepository;
        _idempotencyLookup = idempotencyLookup;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ConfirmExactMatchResponse>> Handle(
        ConfirmExactMatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ConfirmedAllocationRecord? existing = await _idempotencyLookup.FindAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            return Classify(existing, command);
        }

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(
            organizationId,
            command.InvoiceId,
            cancellationToken);

        if (invoice is null)
        {
            return Result<ConfirmExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        BankTransaction? bankTransaction = await _bankTransactionRepository.GetByIdAsync(
            organizationId,
            command.BankTransactionId,
            cancellationToken);

        if (bankTransaction is null)
        {
            return Result<ConfirmExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        Result<Money> amount = ExactMatchConfirmation.TryDeriveAmount(
            invoice.InvoiceNumber,
            bankTransaction.Reference,
            invoice.OutstandingAmount,
            bankTransaction.UnappliedAmount);

        if (amount.IsFailure)
        {
            return Result<ConfirmExactMatchResponse>.Failure(amount.Error);
        }

        Result applied = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            amount.Value);

        if (applied.IsFailure)
        {
            return Result<ConfirmExactMatchResponse>.Failure(applied.Error);
        }

        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            amount.Value,
            _timeProvider.GetUtcNow(),
            command.IdempotencyKey);

        _allocationRepository.Add(allocation);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is ConcurrencyConflictException
            or UniqueConstraintConflictException)
        {
            return await RecoverAsync(command, organizationId, cancellationToken);
        }

        return Result<ConfirmExactMatchResponse>.Success(
            ToResponse(
                allocation.Id,
                invoice.Id,
                bankTransaction.Id,
                amount.Value,
                replayed: false));
    }

    private async Task<Result<ConfirmExactMatchResponse>> RecoverAsync(
        ConfirmExactMatchCommand command,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ConfirmedAllocationRecord? winner = await _idempotencyLookup.FindAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (winner is null)
        {
            return Result<ConfirmExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.ConcurrencyConflict);
        }

        return Classify(winner, command);
    }

    private static Result<ConfirmExactMatchResponse> Classify(
        ConfirmedAllocationRecord existing,
        ConfirmExactMatchCommand command)
    {
        if (existing.InvoiceId != command.InvoiceId
            || existing.BankTransactionId != command.BankTransactionId)
        {
            return Result<ConfirmExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.IdempotencyPayloadConflict);
        }

        return Result<ConfirmExactMatchResponse>.Success(
            ToResponse(
                existing.AllocationId,
                existing.InvoiceId,
                existing.BankTransactionId,
                existing.Amount,
                replayed: true));
    }

    private static ConfirmExactMatchResponse ToResponse(
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        bool replayed)
    {
        return new ConfirmExactMatchResponse(
            allocationId.Value,
            invoiceId.Value,
            bankTransactionId.Value,
            new MoneyDto(amount.Amount, amount.Currency.Value),
            replayed);
    }
}