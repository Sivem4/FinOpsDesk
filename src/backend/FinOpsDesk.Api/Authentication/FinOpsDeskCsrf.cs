namespace FinOpsDesk.Api.Authentication;

internal static class FinOpsDeskCsrf
{
    public const string HeaderName = "X-CSRF-Token";

    public const string ProblemCode = "auth.csrf.invalid";
}