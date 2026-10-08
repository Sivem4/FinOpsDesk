using Microsoft.AspNetCore.Authorization;

namespace FinOpsDesk.Api.Authorization;

internal sealed class TenantMemberRequirement : IAuthorizationRequirement
{
}