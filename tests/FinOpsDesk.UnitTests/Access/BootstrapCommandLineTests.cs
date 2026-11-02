using FinOpsDesk.AccessBootstrap;
using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.UnitTests.Access;

public sealed class BootstrapCommandLineTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Missing_operator_is_rejected_before_a_command_is_accepted()
    {
        bool parsed = BootstrapCommandLine.TryParse(RoleArguments(includeOperator: false), out BootstrapInvocation? invocation, out string? failure);

        Assert.False(parsed);
        Assert.Null(invocation);
        Assert.Equal("invalid-argument", failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" operator")]
    public void Empty_or_padded_operator_is_rejected(string operatorName)
    {
        string[] args = RoleArguments(includeOperator: true);
        args[^1] = operatorName;

        bool parsed = BootstrapCommandLine.TryParse(args, out BootstrapInvocation? invocation, out string? failure);

        Assert.False(parsed);
        Assert.Null(invocation);
        Assert.Equal("invalid-operator", failure);
    }

    [Fact]
    public void Operator_longer_than_128_characters_is_rejected()
    {
        string[] args = RoleArguments(includeOperator: true);
        args[^1] = new string('o', 129);

        bool parsed = BootstrapCommandLine.TryParse(args, out _, out string? failure);

        Assert.False(parsed);
        Assert.Equal("invalid-operator", failure);
    }

    [Fact]
    public void Duplicate_operator_is_rejected()
    {
        string[] args =
        [
            ..RoleArguments(includeOperator: true),
            "--operator",
            "second-operator",
        ];

        bool parsed = BootstrapCommandLine.TryParse(args, out BootstrapInvocation? invocation, out string? failure);

        Assert.False(parsed);
        Assert.Null(invocation);
        Assert.Equal("invalid-argument", failure);
    }

    [Fact]
    public void Unknown_option_is_rejected()
    {
        string[] args =
        [
            ..RoleArguments(includeOperator: true),
            "--source",
            "environment",
        ];

        bool parsed = BootstrapCommandLine.TryParse(args, out _, out string? failure);

        Assert.False(parsed);
        Assert.Equal("invalid-argument", failure);
    }

    [Fact]
    public void Valid_grant_role_keeps_the_explicit_operator()
    {
        bool parsed = BootstrapCommandLine.TryParse(
            RoleArguments(includeOperator: true, command: "grant-role"),
            out BootstrapInvocation? invocation,
            out string? failure);

        Assert.True(parsed);
        Assert.Null(failure);
        Assert.NotNull(invocation);
        Assert.Equal("grant-role", invocation.Command);
        Assert.Equal(OrganizationId, invocation.OrganizationId);
        Assert.Equal(OrganizationRole.Operator, invocation.Role);
        Assert.Equal("phase-7d2-operator", invocation.OperatorName);
    }

    [Fact]
    public void Valid_revoke_role_keeps_the_explicit_operator()
    {
        string[] args = RoleArguments(includeOperator: true, command: "revoke-role");
        args[^1] = new string('r', 128);

        bool parsed = BootstrapCommandLine.TryParse(args, out BootstrapInvocation? invocation, out string? failure);

        Assert.True(parsed);
        Assert.Null(failure);
        Assert.NotNull(invocation);
        Assert.Equal("revoke-role", invocation.Command);
        Assert.Equal(OrganizationRole.ReversalApprover, invocation.Role);
        Assert.Equal(128, invocation.OperatorName.Length);
    }

    private static string[] RoleArguments(bool includeOperator, string command = "grant-role")
    {
        List<string> args =
        [
            command,
            "--issuer",
            "https://issuer.example",
            "--subject",
            "subject-1",
            "--organization-id",
            OrganizationId.ToString("D"),
            "--role",
            command == "revoke-role" ? "ReversalApprover" : "Operator",
        ];
        if (includeOperator)
        {
            args.Add("--operator");
            args.Add("phase-7d2-operator");
        }

        return args.ToArray();
    }
}