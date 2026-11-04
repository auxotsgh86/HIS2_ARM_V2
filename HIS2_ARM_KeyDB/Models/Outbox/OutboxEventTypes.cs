namespace HIS2_ARM_KeyDB.Models.Outbox;

/// <summary>
/// ARM_OUTBOX_EVENTS.EVENT_TYPE 的合法值集合。
/// 💡 Worker 依此字串反序列化 PAYLOAD_JSON 成對應的 IPermissionChangedEvent 實作型別，
///    寫入端 (Mutation Controller / Service) 也必須使用這裡的常數，避免 Magic String 打錯字。
/// </summary>
public static class OutboxEventTypes
{
    public const string UserRoleChanged = "UserRoleChanged";
    public const string RolePermissionChanged = "RolePermissionChanged";
    public const string ActionRequirementChanged = "ActionRequirementChanged";
    public const string RoleStatusChanged = "RoleStatusChanged";
    public const string UserStatusChanged = "UserStatusChanged";
}

