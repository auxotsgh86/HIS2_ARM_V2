namespace HIS2_ARM_KeyDB.Models.PermissionImpact;

public sealed class AffectedUsersByRolesRequest
{
    public string[] RoleIds { get; init; } = [];
}