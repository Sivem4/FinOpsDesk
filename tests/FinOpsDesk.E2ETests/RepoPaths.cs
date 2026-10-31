namespace FinOpsDesk.E2ETests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string ApiProject { get; } = Path.Combine(
        Root,
        "src",
        "backend",
        "FinOpsDesk.Api",
        "FinOpsDesk.Api.csproj");

    public static string FrontendDirectory { get; } = Path.Combine(Root, "src", "frontend");

    public static string Configuration
    {
        get
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FinOpsDesk.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "E2E could not find the repository root from the test output directory.");
    }
}