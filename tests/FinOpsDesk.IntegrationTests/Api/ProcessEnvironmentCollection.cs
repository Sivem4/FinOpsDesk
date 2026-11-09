namespace FinOpsDesk.IntegrationTests.Api;

// Factories in this collection mutate process environment variables.
// The collection does not run beside other collections.
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}