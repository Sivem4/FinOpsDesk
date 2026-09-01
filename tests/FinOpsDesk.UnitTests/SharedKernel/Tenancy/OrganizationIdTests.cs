using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.SharedKernel.Tenancy;

public sealed class OrganizationIdTests
{
    [Fact]
    public void Constructor_should_reject_empty_guid()
    {
        Assert.Throws<ArgumentException>(
            () => new OrganizationId(Guid.Empty));
    }
}