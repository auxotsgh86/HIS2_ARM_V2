using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Models.Events;

/// <summary>
/// 角色狀態異動事件（例如 ARM_ROLES.IS_ACTIVE 被停用/啟用）
/// 需經 PermissionImpactService 將 RoleIds 轉換為受影響的 UserIds。
///
/// 💡 [設計原則] 與 RolePermissionChangedEvent 的差異：
///    - RolePermissionChanged 代表「Role 擁有哪些 Permission」改變，需要 Reload role:perm 快取。
///    - RoleStatusChanged 代表「Role 本身是否可參與有效授權」改變，不強制 Reload role:perm，
///      讓它自然依 TTL 到期即可；真正需要立即失效的是該 Role 所屬 Users 的 Authorization Cache。
/// </summary>
public sealed class RoleStatusChangedEvent : IPermissionChangedEvent
{
    public required string[] RoleIds { get; init; }
}

