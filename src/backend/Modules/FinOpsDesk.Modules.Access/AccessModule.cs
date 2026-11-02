using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.Modules.Access;

public static class AccessModule
{
    public static IServiceCollection AddAccessModule(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AccessDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "access")));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IExternalIdentityProvisioner, ExternalIdentityProvisioner>();
        services.AddScoped<IOrganizationMembershipReader, OrganizationMembershipReader>();
        services.AddScoped<IBrowserSessionStore, BrowserSessionStore>();
        return services;
    }
}