using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class FinOpsDeskApiFactoryEnvironmentTests
{
    private const string ConnectionStringVariable = "ConnectionStrings__FinOpsDesk";

    [Fact]
    public void Nested_factory_disposal_restores_the_previous_connection_string()
    {
        string? original = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        try
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, "Server=before-tests;");

            using (FinOpsDeskApiFactory outer = new("Server=outer;"))
            {
                Assert.Equal(
                    "Server=outer;",
                    Environment.GetEnvironmentVariable(ConnectionStringVariable));

                using (FinOpsDeskApiFactory inner = new("Server=inner;"))
                {
                    Assert.Equal(
                        "Server=inner;",
                        Environment.GetEnvironmentVariable(ConnectionStringVariable));
                }

                Assert.Equal(
                    "Server=outer;",
                    Environment.GetEnvironmentVariable(ConnectionStringVariable));
            }

            Assert.Equal(
                "Server=before-tests;",
                Environment.GetEnvironmentVariable(ConnectionStringVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, original);
        }
    }
}