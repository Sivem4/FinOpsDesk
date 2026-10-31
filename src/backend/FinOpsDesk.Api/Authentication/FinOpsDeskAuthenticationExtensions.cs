using Microsoft.Extensions.Hosting;

namespace FinOpsDesk.Api.Authentication;

internal static class FinOpsDeskAuthenticationExtensions
{
    public static IServiceCollection AddFinOpsDeskAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        string? configured = configuration[AuthenticationCompositions.ConfigurationKey];
        string composition = string.IsNullOrWhiteSpace(configured)
            ? AuthenticationCompositions.ProductionCookie
            : configured.Trim();

        if (string.Equals(
            composition,
            AuthenticationCompositions.DevelopmentJwt,
            StringComparison.Ordinal))
        {
            EnsureDevelopmentJwtIsAllowed(environment);
            services.AddSingleton(new AuthenticationCompositionState
            {
                UsesProductionCookie = false,
                ProductionFinancialAccessFailClosed = false,
            });
            services.AddFinOpsDeskJwtBearerAuthentication(configuration);
            return services;
        }

        if (string.Equals(
            composition,
            AuthenticationCompositions.ProductionCookie,
            StringComparison.Ordinal))
        {
            // JWT settings may still be present on a developer machine.
            // This branch does not register the JWT bearer handler.
            services.AddOptions<JwtBearerAuthenticationSettings>()
                .Bind(configuration.GetSection(JwtBearerAuthenticationSettings.SectionName));
            services.AddSingleton(new AuthenticationCompositionState
            {
                UsesProductionCookie = true,
                ProductionFinancialAccessFailClosed = true,
            });
            services.AddFinOpsDeskCookieAuthentication();
            services.AddFinOpsDeskOpenIdConnect(configuration);
            services.AddFinOpsDeskAntiforgery();
            return services;
        }

        throw new InvalidOperationException(
            "Authentication:Composition must be DevelopmentJwt or ProductionCookie.");
    }

    private static void EnsureDevelopmentJwtIsAllowed(IHostEnvironment environment)
    {
        bool allowed = environment.IsDevelopment()
            || string.Equals(
                environment.EnvironmentName,
                AuthenticationCompositions.E2EEnvironmentName,
                StringComparison.Ordinal);

        if (allowed)
        {
            return;
        }

        throw new InvalidOperationException(
            "Authentication:Composition DevelopmentJwt is only allowed in the Development and E2E environments.");
    }
}