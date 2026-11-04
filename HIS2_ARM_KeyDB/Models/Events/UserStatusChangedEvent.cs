using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Models.Events;

/// <summary>
/// 人員狀態異動事件（例如 ARM_USERS.IS_ACTIVE 被停用/啟用）
/// 已知受影響的 UserId，不需要經過 PermissionImpactService 查詢。
///
/// 💡 [設計原則] 與 UserRoleChangedEvent 的差異：
///    - UserRoleChanged 代表「User 的角色集合」改變。
///    - UserStatusChanged 代表「User 本身的 Active 狀態」改變，
///      除了 Cache Invalidation 外，也應該撤銷該 User 的 Session（強制登出）。
/// </summary>
public sealed class UserStatusChangedEvent : IPermissionChangedEvent
{
    public required string[] UserIds { get; init; }
}

