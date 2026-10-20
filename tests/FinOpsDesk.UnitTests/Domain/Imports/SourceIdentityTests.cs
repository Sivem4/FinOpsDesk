using FinOpsDesk.Modules.Reconciliation.Domain.Imports;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Imports;

public sealed class SourceIdentityTests
{
    [Fact]
    public void SourceSystem_should_trim_and_uppercase()
    {
        var sourceSystem = new SourceSystem("  mBank  ");

        Assert.Equal("MBANK", sourceSystem.Value);
    }

    [Fact]
    public void SourceSystem_should_preserve_internal_spaces()
    {
        var sourceSystem = new SourceSystem(" bank account ");

        Assert.Equal("BANK ACCOUNT", sourceSystem.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SourceSystem_should_reject_blank_values(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new SourceSystem(value));
    }

    [Fact]
    public void SourceSystem_should_reject_values_longer_than_64_characters()
    {
        Assert.Throws<ArgumentException>(
            () => new SourceSystem(new string('a', SourceSystem.MaxLength + 1)));
    }

    [Fact]
    public void SourceSystem_should_accept_the_maximum_length()
    {
        string value = new string('a', SourceSystem.MaxLength);

        var sourceSystem = new SourceSystem(value);

        Assert.Equal(SourceSystem.MaxLength, sourceSystem.Value.Length);
    }

    [Fact]
    public void SourceSystem_equality_should_ignore_case()
    {
        var left = new SourceSystem("mBank");
        var right = new SourceSystem("MBANK");

        Assert.Equal(left, right);
    }

    [Fact]
    public void ExternalId_should_trim_and_preserve_case()
    {
        var externalId = new ExternalId("  Inv-001  ");

        Assert.Equal("Inv-001", externalId.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExternalId_should_reject_blank_values(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalId(value));
    }

    [Fact]
    public void ExternalId_should_reject_values_longer_than_128_characters()
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalId(new string('a', ExternalId.MaxLength + 1)));
    }

    [Fact]
    public void ExternalId_should_accept_the_maximum_length()
    {
        string value = new string('a', ExternalId.MaxLength);

        var externalId = new ExternalId(value);

        Assert.Equal(value, externalId.Value);
    }

    [Fact]
    public void ExternalId_equality_should_be_case_sensitive()
    {
        var left = new ExternalId("abc");
        var right = new ExternalId("ABC");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void SourceIdentity_should_use_value_equality()
    {
        var left = new SourceIdentity(
            new SourceSystem(" erp "),
            new ExternalId("abc"));
        var right = new SourceIdentity(
            new SourceSystem("ERP"),
            new ExternalId("abc"));

        Assert.Equal(left, right);
        Assert.Equal("ERP", left.SourceSystem.Value);
        Assert.Equal("abc", left.ExternalId.Value);
    }

    [Fact]
    public void SourceIdentity_should_distinguish_external_id_case()
    {
        var left = new SourceIdentity(
            new SourceSystem("ERP"),
            new ExternalId("abc"));
        var right = new SourceIdentity(
            new SourceSystem("ERP"),
            new ExternalId("ABC"));

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void SourceIdentity_should_reject_null_components()
    {
        var sourceSystem = new SourceSystem("ERP");
        var externalId = new ExternalId("abc");

        Assert.Throws<ArgumentNullException>(
            () => new SourceIdentity(null!, externalId));
        Assert.Throws<ArgumentNullException>(
            () => new SourceIdentity(sourceSystem, null!));
    }
}