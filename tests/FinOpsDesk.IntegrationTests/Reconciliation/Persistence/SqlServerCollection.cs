namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}