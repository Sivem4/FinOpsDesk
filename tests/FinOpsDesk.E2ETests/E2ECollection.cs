namespace FinOpsDesk.E2ETests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ECollection
{
    public const string Name = "FinOpsDesk.BrowserE2E";
}