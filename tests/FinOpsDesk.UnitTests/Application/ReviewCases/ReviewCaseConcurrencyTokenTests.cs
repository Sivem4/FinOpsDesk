using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class ReviewCaseConcurrencyTokenTests
{
    [Fact]
    public void Encode_should_round_trip_an_eight_byte_row_version()
    {
        byte[] rowVersion = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.True(ReviewCaseConcurrencyToken.TryDecode(
            ReviewCaseConcurrencyToken.Encode(rowVersion),
            out byte[] decoded));
        Assert.Equal(rowVersion, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]
    [InlineData("YQ==")]
    public void TryDecode_should_reject_a_token_that_is_not_an_eight_byte_row_version(string? token)
    {
        Assert.False(ReviewCaseConcurrencyToken.TryDecode(token, out byte[] decoded));
        Assert.Empty(decoded);
    }
}