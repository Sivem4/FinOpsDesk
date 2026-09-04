using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.SharedKernel.Results;

public sealed class ResultOfTTests
{
    [Fact]
    public void Success_should_contain_the_provided_value()
    {
        Result<string> result = Result<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void Failure_should_contain_the_provided_error()
    {
        var error = new Error("code", "A message.");

        Result<string> result = Result<string>.Failure(error);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Failure_should_reject_null_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => Result<string>.Failure(null!));
    }

    [Fact]
    public void Success_should_reject_null_value()
    {
        Assert.Throws<ArgumentNullException>(
            () => Result<string>.Success(null!));
    }
}