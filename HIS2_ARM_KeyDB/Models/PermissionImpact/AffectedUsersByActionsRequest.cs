using HIS2_ARM_KeyDB.Models.Events;

namespace HIS2_ARM_KeyDB.Models.PermissionImpact;

public sealed class AffectedUsersByActionsRequest
{
    public ActionResourceChanged[] Actions { get; init; } = [];
}