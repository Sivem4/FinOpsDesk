using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.SharedKernel.Results;

public sealed class ErrorTests
{
    [Fact]
    public void Constructor_should_reject_null_code()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Error(null!, "A message."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_should_reject_empty_or_whitespace_code(
        string code)
    {
        Assert.Throws<ArgumentException>(
            () => new Error(code, "A message."));
    }

    [Fact]
    public void Constructor_should_reject_null_message()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Error("code", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_should_reject_empty_or_whitespace_message(
        string message)
    {
        Assert.Throws<ArgumentException>(
            () => new Error("code", message));
    }
}