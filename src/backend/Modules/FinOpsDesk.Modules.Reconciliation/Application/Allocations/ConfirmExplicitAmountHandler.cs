using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmExplicitAmountHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IAllocationIdempotencyLookup _idempotencyLookup;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ConfirmExplicitAmountHandler(
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

    public async Task<Result<ConfirmExplicitAmountResponse>> Handle(
        ConfirmExplicitAmountCommand command,
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
            return Result<ConfirmExplicitAmountResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        BankTransaction? bankTransaction = await _bankTransactionRepository.GetByIdAsync(
            organizationId,
            command.BankTransactionId,
            cancellationToken);

        if (bankTransaction is null)
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        Result amountValid = ExplicitAmountValidation.Validate(command.Amount);

        if (amountValid.IsFailure)
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(amountValid.Error);
        }

        Result applied = AllocationConfirmation.Apply(
            invoice,
            bankTransaction,
            command.Amount);

        if (applied.IsFailure)
        {
            return await FailOrReplayAsync(
                applied.Error,
                command,
                organizationId,
                cancellationToken);
        }

        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            command.Amount,
            _timeProvider.GetUtcNow(),
            command.IdempotencyKey,
            AllocationIdempotencyOperation.ExplicitAmount);

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

        return Result<ConfirmExplicitAmountResponse>.Success(
            ToResponse(
                allocation.Id,
                invoice.Id,
                bankTransaction.Id,
                command.Amount,
                replayed: false));
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> FailOrReplayAsync(
        Error error,
        ConfirmExplicitAmountCommand command,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyBalanceConflict.IsPossibleLostRace(error))
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(error);
        }

        ConfirmedAllocationRecord? committed = await _idempotencyLookup.FindAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (committed is null)
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(error);
        }

        return Classify(committed, command);
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> RecoverAsync(
        ConfirmExplicitAmountCommand command,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ConfirmedAllocationRecord? winner = await _idempotencyLookup.FindAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (winner is null)
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(
                ReconciliationApplicationErrors.ConcurrencyConflict);
        }

        return Classify(winner, command);
    }

    private static Result<ConfirmExplicitAmountResponse> Classify(
        ConfirmedAllocationRecord existing,
        ConfirmExplicitAmountCommand command)
    {
        if (existing.IdempotencyOperation != AllocationIdempotencyOperation.ExplicitAmount
            || existing.InvoiceId != command.InvoiceId
            || existing.BankTransactionId != command.BankTransactionId
            || existing.Amount != command.Amount)
        {
            return Result<ConfirmExplicitAmountResponse>.Failure(
                ReconciliationApplicationErrors.IdempotencyPayloadConflict);
        }

        return Result<ConfirmExplicitAmountResponse>.Success(
            ToResponse(
                existing.AllocationId,
                existing.InvoiceId,
                existing.BankTransactionId,
                existing.Amount,
                replayed: true));
    }

    private static ConfirmExplicitAmountResponse ToResponse(
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        bool replayed)
    {
        return new ConfirmExplicitAmountResponse(
            allocationId.Value,
            invoiceId.Value,
            bankTransactionId.Value,
            new MoneyDto(amount.Amount, amount.Currency.Value),
            replayed);
    }
}