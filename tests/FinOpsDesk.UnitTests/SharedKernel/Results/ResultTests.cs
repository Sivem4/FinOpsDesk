using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.SharedKernel.Results;

public sealed class ResultTests
{
    [Fact]
    public void Success_should_not_contain_an_error()
    {
        Result result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_should_contain_the_provided_error()
    {
        var error = new Error("code", "A message.");

        Result result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Failure_should_reject_null_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => Result.Failure(null!));
    }
}