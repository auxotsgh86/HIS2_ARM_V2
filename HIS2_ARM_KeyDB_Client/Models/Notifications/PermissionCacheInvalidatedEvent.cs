namespace HIS2_ARM_KeyDB_Client.Models.Notifications;

/// <summary>
/// ARM 權限異動後，通知子系統使指定 User 的 Auth Cache 失效。
///
/// 此事件只包含已完成 Impact Calculation 的 UserId。
/// Client 不需要知道造成失效的原始權限異動原因。
/// </summary>
public sealed class PermissionCacheInvalidatedEvent
{
    public string[] UserIds { get; init; } = [];
}