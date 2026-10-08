using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FinOpsDesk.Api.Authentication;

internal static class JwtBearerAuthenticationExtensions
{
    public static IServiceCollection AddFinOpsDeskJwtBearerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<JwtBearerAuthenticationSettings>,
            JwtBearerAuthenticationSettingsValidator>();

        services.AddOptions<JwtBearerAuthenticationSettings>()
            .Bind(configuration.GetSection(JwtBearerAuthenticationSettings.SectionName))
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtBearerAuthenticationSettings>>(ConfigureJwtBearer);

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwtOptions,
        IOptions<JwtBearerAuthenticationSettings> settingsAccessor)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(settingsAccessor);

        JwtBearerAuthenticationSettings settings = settingsAccessor.Value;
        string audience = settings.Audience
            ?? throw new InvalidOperationException(
                "Authentication:JwtBearer:Audience is required.");

        jwtOptions.MapInboundClaims = false;
        jwtOptions.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        jwtOptions.Audience = audience;

        jwtOptions.TokenValidationParameters ??= new TokenValidationParameters();
        TokenValidationParameters validation = jwtOptions.TokenValidationParameters;
        validation.ValidateIssuer = true;
        validation.ValidateAudience = true;
        validation.ValidateLifetime = true;
        validation.RequireExpirationTime = true;
        validation.ValidateIssuerSigningKey = true;
        validation.ValidAudience = audience;
        validation.ClockSkew = TimeSpan.FromMinutes(5);

        if (!string.IsNullOrWhiteSpace(settings.Authority))
        {
            jwtOptions.Authority = settings.Authority;

            if (!string.IsNullOrWhiteSpace(settings.Issuer))
            {
                validation.ValidIssuer = settings.Issuer;
            }

            return;
        }

        string issuer = settings.Issuer
            ?? throw new InvalidOperationException(
                "Authentication:JwtBearer:Issuer is required when SigningKey is configured.");
        string signingKey = settings.SigningKey
            ?? throw new InvalidOperationException(
                "Authentication:JwtBearer:SigningKey is required when Authority is not configured.");

        validation.ValidIssuer = issuer;
        validation.IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(signingKey));
    }
}