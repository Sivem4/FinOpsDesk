using System.Text;

using Microsoft.Extensions.Options;

namespace FinOpsDesk.Api.Authentication;

internal sealed class JwtBearerAuthenticationSettingsValidator
    : IValidateOptions<JwtBearerAuthenticationSettings>
{
    public ValidateOptionsResult Validate(
        string? name,
        JwtBearerAuthenticationSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:JwtBearer:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(options.OrganizationClaimType))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:JwtBearer:OrganizationClaimType is required.");
        }

        bool hasAuthority = !string.IsNullOrWhiteSpace(options.Authority);
        bool hasSigningKey = !string.IsNullOrWhiteSpace(options.SigningKey);

        if (hasAuthority && hasSigningKey)
        {
            return ValidateOptionsResult.Fail(
                "Configure either Authentication:JwtBearer:Authority or Authentication:JwtBearer:SigningKey, not both.");
        }

        if (!hasAuthority && !hasSigningKey)
        {
            return ValidateOptionsResult.Fail(
                "JWT bearer authentication requires Authentication:JwtBearer:Authority, or Issuer and SigningKey.");
        }

        if (!hasSigningKey)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:JwtBearer:Issuer is required when SigningKey is configured.");
        }

        if (Encoding.UTF8.GetByteCount(options.SigningKey!) < 32)
        {
            return ValidateOptionsResult.Fail(
                "Authentication:JwtBearer:SigningKey must be at least 32 UTF-8 bytes.");
        }

        return ValidateOptionsResult.Success;
    }
}