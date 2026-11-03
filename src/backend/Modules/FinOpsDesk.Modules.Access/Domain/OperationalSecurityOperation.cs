namespace FinOpsDesk.Modules.Access.Domain;

public enum OperationalSecurityOperation : byte
{
    DisableUser = 1,
    RevokeMembership = 2,
    DisableOrganization = 3,
}