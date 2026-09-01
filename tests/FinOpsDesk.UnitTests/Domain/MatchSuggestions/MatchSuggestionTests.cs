using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.MatchSuggestions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.MatchSuggestions;

public sealed class MatchSuggestionTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_suggestion_should_be_pending()
    {
        var id = MatchSuggestionId.New();
        var invoiceId = InvoiceId.New();
        var bankTransactionId = BankTransactionId.New();
        var suggestedAmount = new Money(300m, Pln);

        var suggestion = new MatchSuggestion(
            id,
            TestOrganizationId,
            invoiceId,
            bankTransactionId,
            suggestedAmount,
            GeneratedAt);

        Assert.Equal(id, suggestion.Id);
        Assert.Equal(TestOrganizationId, suggestion.OrganizationId);
        Assert.Equal(invoiceId, suggestion.InvoiceId);
        Assert.Equal(bankTransactionId, suggestion.BankTransactionId);
        Assert.Equal(suggestedAmount, suggestion.SuggestedAmount);
        Assert.Equal(GeneratedAt, suggestion.GeneratedAt);
        Assert.Equal(MatchSuggestionStatus.Pending, suggestion.Status);
        Assert.Null(suggestion.ResolvedAt);
        Assert.Null(suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Constructor_should_reject_non_positive_suggested_amount(
        decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MatchSuggestion(
                MatchSuggestionId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(amount, Pln),
                GeneratedAt));
    }

    [Fact]
    public void Constructor_should_reject_default_generated_at()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MatchSuggestion(
                MatchSuggestionId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                default));
    }

    [Fact]
    public void Accept_should_mark_suggestion_as_accepted()
    {
        var suggestion = CreateSuggestion();
        var allocationId = AllocationId.New();
        var resolvedAt = GeneratedAt.AddHours(1);

        Result result = suggestion.Accept(allocationId, resolvedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(MatchSuggestionStatus.Accepted, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal(allocationId, suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
        Assert.Equal(
            new Money(300m, Pln),
            suggestion.SuggestedAmount);
    }

    [Fact]
    public void Accept_should_allow_same_time_as_generation()
    {
        var suggestion = CreateSuggestion();
        var allocationId = AllocationId.New();

        suggestion.Accept(allocationId, GeneratedAt);

        Assert.Equal(MatchSuggestionStatus.Accepted, suggestion.Status);
        Assert.Equal(GeneratedAt, suggestion.ResolvedAt);
        Assert.Equal(allocationId, suggestion.AllocationId);
    }

    [Fact]
    public void Accept_should_reject_time_before_generation()
    {
        var suggestion = CreateSuggestion();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => suggestion.Accept(
                AllocationId.New(),
                GeneratedAt.AddMinutes(-1)));

        AssertPendingUnchanged(suggestion);
    }

    [Fact]
    public void Accept_should_reject_null_allocation_id()
    {
        var suggestion = CreateSuggestion();

        Assert.Throws<ArgumentNullException>(
            () => suggestion.Accept(
                null!,
                GeneratedAt.AddHours(1)));

        AssertPendingUnchanged(suggestion);
    }

    [Fact]
    public void Accept_should_reject_null_allocation_id_when_already_accepted()
    {
        var suggestion = CreateSuggestion();
        var allocationId = AllocationId.New();
        var resolvedAt = GeneratedAt.AddHours(1);

        suggestion.Accept(allocationId, resolvedAt);

        Assert.Throws<ArgumentNullException>(
            () => suggestion.Accept(
                null!,
                resolvedAt.AddHours(1)));

        Assert.Equal(MatchSuggestionStatus.Accepted, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal(allocationId, suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
    }

    [Fact]
    public void Accept_should_reject_when_already_accepted()
    {
        var suggestion = CreateSuggestion();
        var allocationId = AllocationId.New();
        var resolvedAt = GeneratedAt.AddHours(1);

        suggestion.Accept(allocationId, resolvedAt);

        Result result = suggestion.Accept(
            AllocationId.New(),
            resolvedAt.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.MatchSuggestionAlreadyResolved.Code,
            result.Error?.Code);

        Assert.Equal(MatchSuggestionStatus.Accepted, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal(allocationId, suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
    }

    [Fact]
    public void Accept_should_reject_when_already_rejected()
    {
        var suggestion = CreateSuggestion();
        var resolvedAt = GeneratedAt.AddHours(1);

        suggestion.Reject("Incorrect reference", resolvedAt);

        Result result = suggestion.Accept(
            AllocationId.New(),
            resolvedAt.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.MatchSuggestionAlreadyResolved.Code,
            result.Error?.Code);

        Assert.Equal(MatchSuggestionStatus.Rejected, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Null(suggestion.AllocationId);
        Assert.Equal("Incorrect reference", suggestion.RejectionReason);
    }

    [Fact]
    public void Reject_should_mark_suggestion_as_rejected()
    {
        var suggestion = CreateSuggestion();
        var resolvedAt = GeneratedAt.AddHours(1);

        Result result = suggestion.Reject("Incorrect reference", resolvedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(MatchSuggestionStatus.Rejected, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal("Incorrect reference", suggestion.RejectionReason);
        Assert.Null(suggestion.AllocationId);
        Assert.Equal(
            new Money(300m, Pln),
            suggestion.SuggestedAmount);
    }

    [Fact]
    public void Reject_should_trim_reason()
    {
        var suggestion = CreateSuggestion();

        suggestion.Reject(
            "  Incorrect reference  ",
            GeneratedAt.AddHours(1));

        Assert.Equal(
            "Incorrect reference",
            suggestion.RejectionReason);
    }

    [Fact]
    public void Reject_should_allow_same_time_as_generation()
    {
        var suggestion = CreateSuggestion();

        suggestion.Reject("Incorrect reference", GeneratedAt);

        Assert.Equal(MatchSuggestionStatus.Rejected, suggestion.Status);
        Assert.Equal(GeneratedAt, suggestion.ResolvedAt);
    }

    [Fact]
    public void Reject_should_reject_null_reason()
    {
        var suggestion = CreateSuggestion();

        Assert.Throws<ArgumentNullException>(
            () => suggestion.Reject(
                null!,
                GeneratedAt.AddHours(1)));

        AssertPendingUnchanged(suggestion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_should_reject_empty_or_whitespace_reason(
        string reason)
    {
        var suggestion = CreateSuggestion();

        Assert.Throws<ArgumentException>(
            () => suggestion.Reject(
                reason,
                GeneratedAt.AddHours(1)));

        AssertPendingUnchanged(suggestion);
    }

    [Fact]
    public void Reject_should_reject_time_before_generation()
    {
        var suggestion = CreateSuggestion();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => suggestion.Reject(
                "Incorrect reference",
                GeneratedAt.AddMinutes(-1)));

        AssertPendingUnchanged(suggestion);
    }

    [Fact]
    public void Reject_should_reject_when_already_rejected()
    {
        var suggestion = CreateSuggestion();
        var resolvedAt = GeneratedAt.AddHours(1);

        suggestion.Reject("Incorrect reference", resolvedAt);

        Result result = suggestion.Reject(
            "Duplicate payment",
            resolvedAt.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.MatchSuggestionAlreadyResolved.Code,
            result.Error?.Code);

        Assert.Equal(MatchSuggestionStatus.Rejected, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal("Incorrect reference", suggestion.RejectionReason);
        Assert.Null(suggestion.AllocationId);
    }

    [Fact]
    public void Reject_should_reject_when_already_accepted()
    {
        var suggestion = CreateSuggestion();
        var allocationId = AllocationId.New();
        var resolvedAt = GeneratedAt.AddHours(1);

        suggestion.Accept(allocationId, resolvedAt);

        Result result = suggestion.Reject(
            "Incorrect reference",
            resolvedAt.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.MatchSuggestionAlreadyResolved.Code,
            result.Error?.Code);

        Assert.Equal(MatchSuggestionStatus.Accepted, suggestion.Status);
        Assert.Equal(resolvedAt, suggestion.ResolvedAt);
        Assert.Equal(allocationId, suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
    }

    private static MatchSuggestion CreateSuggestion()
    {
        return new MatchSuggestion(
            MatchSuggestionId.New(),
            TestOrganizationId,
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(300m, Pln),
            GeneratedAt);
    }

    private static void AssertPendingUnchanged(MatchSuggestion suggestion)
    {
        Assert.Equal(MatchSuggestionStatus.Pending, suggestion.Status);
        Assert.Null(suggestion.ResolvedAt);
        Assert.Null(suggestion.AllocationId);
        Assert.Null(suggestion.RejectionReason);
    }
}