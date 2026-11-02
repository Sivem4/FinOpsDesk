using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.AccessBootstrap;

internal sealed record BootstrapInvocation(
    string Command,
    Guid OrganizationId,
    string? Name,
    string? Issuer,
    string? Subject,
    OrganizationRole Role,
    string OperatorName);

internal static class BootstrapCommandLine
{
    public static bool TryParse(string[] args, out BootstrapInvocation? invocation, out string? failure)
    {
        invocation = null;
        failure = "invalid-command";
        if (args.Length == 0)
        {
            return false;
        }

        string command = args[0];
        if (command is not ("create-organization" or "grant-membership" or "grant-role" or "revoke-role"))
        {
            return false;
        }

        Dictionary<string, string> options = [];
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                failure = "invalid-argument";
                return false;
            }

            if (!options.TryAdd(args[index], args[index + 1]))
            {
                failure = "invalid-argument";
                return false;
            }
        }

        if (command == "create-organization")
        {
            if (!HasOnly(options, "--organization-id", "--name")
                || !Guid.TryParseExact(options["--organization-id"], "D", out Guid organizationId))
            {
                failure = "invalid-argument";
                return false;
            }

            invocation = new BootstrapInvocation(command, organizationId, options["--name"], null, null, default, string.Empty);
            failure = null;
            return true;
        }

        if (command == "grant-membership")
        {
            if (!HasOnly(options, "--issuer", "--subject", "--organization-id")
                || !Guid.TryParseExact(options["--organization-id"], "D", out Guid organizationId))
            {
                failure = "invalid-argument";
                return false;
            }

            invocation = new BootstrapInvocation(
                command,
                organizationId,
                null,
                options["--issuer"],
                options["--subject"],
                default,
                string.Empty);
            failure = null;
            return true;
        }

        if (!HasOnly(options, "--issuer", "--subject", "--organization-id", "--role", "--operator")
            || !Guid.TryParseExact(options["--organization-id"], "D", out Guid roleOrganizationId)
            || !TryParseRole(options["--role"], out OrganizationRole role))
        {
            failure = "invalid-argument";
            return false;
        }

        if (!TryReadOperator(options["--operator"], out string operatorName))
        {
            failure = "invalid-operator";
            return false;
        }

        invocation = new BootstrapInvocation(
            command,
            roleOrganizationId,
            null,
            options["--issuer"],
            options["--subject"],
            role,
            operatorName);
        failure = null;
        return true;
    }

    private static bool TryReadOperator(string operatorName, out string accepted)
    {
        try
        {
            accepted = RoleActor.DeploymentBootstrap(operatorName).OperatorName
                ?? throw new ArgumentException("Deployment operator name is missing.");
            return true;
        }
        catch (ArgumentException)
        {
            accepted = string.Empty;
            return false;
        }
    }

    private static bool HasOnly(Dictionary<string, string> options, params string[] names)
    {
        if (options.Count != names.Length)
        {
            return false;
        }

        foreach (string name in names)
        {
            if (!options.ContainsKey(name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseRole(string text, out OrganizationRole role)
    {
        switch (text)
        {
            case "Viewer":
                role = OrganizationRole.Viewer;
                return true;
            case "Operator":
                role = OrganizationRole.Operator;
                return true;
            case "ReversalApprover":
                role = OrganizationRole.ReversalApprover;
                return true;
            case "OrganizationAdmin":
                role = OrganizationRole.OrganizationAdmin;
                return true;
            default:
                role = default;
                return false;
        }
    }
}