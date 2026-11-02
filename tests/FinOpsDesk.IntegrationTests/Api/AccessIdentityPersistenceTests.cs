using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AccessIdentityPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public AccessIdentityPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Trailing_space_case_and_issuer_remain_distinct_identities()
    {
        string issuer = NewIssuer();
        string otherIssuer = NewIssuer();
        await using ServiceProvider provider = CreateProvider();

        ExternalIdentityProvision plain = await ProvisionAsync(provider, issuer, "operator");
        ExternalIdentityProvision trailing = await ProvisionAsync(provider, issuer, "operator ");
        ExternalIdentityProvision upper = await ProvisionAsync(provider, issuer, "Ab");
        ExternalIdentityProvision lower = await ProvisionAsync(provider, issuer, "ab");
        ExternalIdentityProvision other = await ProvisionAsync(provider, otherIssuer, "operator");
        ExternalIdentityProvision again = await ProvisionAsync(provider, issuer, "operator");

        Assert.NotEqual(plain.UserId, trailing.UserId);
        Assert.NotEqual(upper.UserId, lower.UserId);
        Assert.NotEqual(plain.UserId, other.UserId);
        Assert.Equal(ExternalIdentityProvisionStatus.Reused, again.Status);
        Assert.Equal(plain.UserId, again.UserId);

        ApplicationUser storedPlain = await RequireUserAsync(issuer, "operator");
        ApplicationUser storedTrailing = await RequireUserAsync(issuer, "operator ");
        Assert.Equal("operator", storedPlain.Subject);
        Assert.Equal("operator ", storedTrailing.Subject);
        Assert.Equal(9, storedTrailing.Subject.Length);
        Assert.True(ExternalIdentityKey.Same(storedPlain.Issuer, storedPlain.Subject, issuer, "operator"));
        Assert.False(ExternalIdentityKey.Same(storedPlain.Issuer, storedPlain.Subject, issuer, "operator "));
    }

    [Fact]
    public async Task Concurrent_first_login_creates_one_user()
    {
        string issuer = NewIssuer();
        const string subject = "concurrent-operator";
        await using ServiceProvider provider = CreateProvider();

        Task<ExternalIdentityProvision>[] attempts =
        [
            ProvisionAsync(provider, issuer, subject),
            ProvisionAsync(provider, issuer, subject),
        ];
        ExternalIdentityProvision[] results = await Task.WhenAll(attempts);

        Assert.All(results, result => Assert.True(result.Allowed));
        Assert.Equal(results[0].UserId, results[1].UserId);
        Assert.Contains(results, result => result.Status == ExternalIdentityProvisionStatus.Created);
        await RequireUserAsync(issuer, subject);
    }

    [Fact]
    public void Hash_match_with_different_raw_values_is_not_the_same_user()
    {
        ApplicationUser stored = ApplicationUser.Create(
            Guid.NewGuid(),
            "https://issuer.example",
            "Ab",
            null,
            DateTimeOffset.UtcNow);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ExternalIdentityProvisioner.ResolveExisting(stored, "https://issuer.example", "ab"));

        Assert.Equal("Identity key collision.", exception.Message);
    }

    [Fact]
    public async Task Access_migration_preserves_reconciliation_rows_without_organization_parents()
    {
        string databaseName = "FinOpsDesk_IT_" + Guid.NewGuid().ToString("N");
        SqlConnectionStringBuilder source = new(_fixture.ConnectionString);
        SqlConnectionStringBuilder master = new(source.ConnectionString)
        {
            InitialCatalog = "master",
        };
        SqlConnectionStringBuilder database = new(source.ConnectionString)
        {
            InitialCatalog = databaseName,
        };

        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{databaseName}]");
        try
        {
            OrganizationId organizationId = OrganizationId.New();
            await using (ReconciliationDbContext reconciliation = _fixture.CreateContext(database.ConnectionString))
            {
                await reconciliation.Database.MigrateAsync();
                reconciliation.Invoices.Add(PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-ACCESS"));
                await reconciliation.SaveChangesAsync();
            }

            await using (AccessDbContext access = SqlServerFixture.CreateAccessContext(database.ConnectionString))
            {
                await access.Database.MigrateAsync();
                Assert.Equal(0, await access.Organizations.CountAsync(organization => organization.Id == organizationId.Value));
            }

            await using ReconciliationDbContext after = _fixture.CreateContext(database.ConnectionString);
            Assert.Equal(1, await after.Invoices.CountAsync());

            Assert.InRange(await ScalarAsync(
                database.ConnectionString,
                "SELECT COUNT(*) FROM reconciliation.__EFMigrationsHistory"), 1, int.MaxValue);
            Assert.Equal(4, await ScalarAsync(
                database.ConnectionString,
                "SELECT COUNT(*) FROM access.__EFMigrationsHistory"));
            Assert.Equal(1, await ScalarAsync(
                database.ConnectionString,
                """
                SELECT COUNT(*)
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'access.ApplicationUsers')
                  AND name = N'SessionStamp'
                """));
            Assert.Equal(0, await ScalarAsync(
                database.ConnectionString,
                """
                SELECT COUNT(*)
                FROM sys.default_constraints AS defaultConstraint
                INNER JOIN sys.columns AS columnDefinition
                    ON defaultConstraint.parent_object_id = columnDefinition.object_id
                   AND defaultConstraint.parent_column_id = columnDefinition.column_id
                WHERE columnDefinition.object_id = OBJECT_ID(N'access.ApplicationUsers')
                  AND columnDefinition.name = N'SessionStamp'
                """));
            Assert.Equal(0, await ScalarAsync(
                database.ConnectionString,
                """
                SELECT COUNT(*)
                FROM sys.foreign_keys AS foreignKey
                INNER JOIN sys.tables AS parentTable ON foreignKey.parent_object_id = parentTable.object_id
                INNER JOIN sys.schemas AS parentSchema ON parentTable.schema_id = parentSchema.schema_id
                INNER JOIN sys.tables AS referencedTable ON foreignKey.referenced_object_id = referencedTable.object_id
                INNER JOIN sys.schemas AS referencedSchema ON referencedTable.schema_id = referencedSchema.schema_id
                WHERE parentSchema.name = N'reconciliation'
                  AND referencedSchema.name = N'access'
                """));
        }
        finally
        {
            await ExecuteAsync(
                master.ConnectionString,
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
        }
    }

    [Fact]
    public async Task Membership_foreign_keys_do_not_cascade()
    {
        const string sql =
            """
            SELECT delete_referential_action
            FROM sys.foreign_keys
            WHERE name IN (
                N'FK_OrganizationMemberships_ApplicationUsers_UserId',
                N'FK_OrganizationMemberships_Organizations_OrganizationId')
            ORDER BY name
            """;

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        List<int> actions = [];
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetByte(0));
        }

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.Equal(0, action));
    }

    private ServiceProvider CreateProvider()
    {
        ServiceCollection services = new();
        services.AddAccessModule(_fixture.ConnectionString);
        return services.BuildServiceProvider();
    }

    private static async Task<ExternalIdentityProvision> ProvisionAsync(
        IServiceProvider provider,
        string issuer,
        string subject)
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            IExternalIdentityProvisioner provisioner = scope.ServiceProvider
                .GetRequiredService<IExternalIdentityProvisioner>();
            return await provisioner.ProvisionAsync(issuer, subject, null, CancellationToken.None);
        }
    }

    private async Task<ApplicationUser> RequireUserAsync(string issuer, string subject)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        List<ApplicationUser> rows = await context.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync();

        ApplicationUser match = Assert.Single(
            rows,
            user => ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject));
        return match;
    }

    private static string NewIssuer()
    {
        return "https://issuer.example/" + Guid.NewGuid().ToString("N");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}