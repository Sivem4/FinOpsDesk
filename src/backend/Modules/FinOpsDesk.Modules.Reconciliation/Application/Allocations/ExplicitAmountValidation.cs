using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

/// <summary>
/// Checks that a caller-supplied amount can be stored as <c>decimal(19,4)</c>
/// without rounding.
/// </summary>
/// <remarks>
/// Trailing zeros belong to the decimal scale, not to extra precision.
/// <c>400.00000</c> is the same value as <c>400.0000</c> and is accepted.
/// A non-zero digit past the fourth decimal place, such as <c>0.00001</c>,
/// is rejected because SQL Server would have to round it.
/// The absolute value must be below 10^15 so fifteen digits before the
/// decimal point fit in precision 19 with scale 4.
/// A three-letter currency is already required by <see cref="CurrencyCode"/>
/// when <see cref="Money"/> is constructed.
/// </remarks>
public static class ExplicitAmountValidation
{
    private const int Scale = 4;

    private static readonly decimal MaximumExclusive = 1_000_000_000_000_000m;

    public static Result Validate(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0m || amount.Amount >= MaximumExclusive)
        {
            return Result.Failure(ReconciliationApplicationErrors.ExplicitAmountInvalid);
        }

        decimal stored = decimal.Round(
            amount.Amount,
            Scale,
            MidpointRounding.AwayFromZero);

        if (amount.Amount != stored)
        {
            return Result.Failure(ReconciliationApplicationErrors.ExplicitAmountInvalid);
        }

        return Result.Success();
    }
}