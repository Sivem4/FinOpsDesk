using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

namespace FinOpsDesk.UnitTests.Access;

public sealed class SqlApplicationLockTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Nonnegative_codes_are_acquired(int code)
    {
        Assert.True(SqlApplicationLock.IsAcquired(code));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public void Negative_codes_are_not_acquired(int code)
    {
        Assert.False(SqlApplicationLock.IsAcquired(code));
    }

    [Fact]
    public void Missing_or_invalid_procedure_result_is_not_a_return_code()
    {
        Assert.False(SqlApplicationLock.TryReadReturnCode(null, out _));
        Assert.False(SqlApplicationLock.TryReadReturnCode(DBNull.Value, out _));
        Assert.False(SqlApplicationLock.TryReadReturnCode("0", out _));
        Assert.True(SqlApplicationLock.TryReadReturnCode(0, out int acquired));
        Assert.Equal(0, acquired);
        Assert.True(SqlApplicationLock.TryReadReturnCode(-1, out int timeout));
        Assert.Equal(-1, timeout);
    }
}