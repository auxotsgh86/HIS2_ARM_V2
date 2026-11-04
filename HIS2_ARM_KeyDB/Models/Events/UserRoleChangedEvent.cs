using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Models.Events;

/// <summary>
/// 人員角色異動事件（例如指派/移除角色）
/// 已知受影響的 UserId，不需要經過 PermissionImpactService 查詢
/// </summary>
public sealed class UserRoleChangedEvent : IPermissionChangedEvent
{
    public required string[] UserIds { get; init; }
}