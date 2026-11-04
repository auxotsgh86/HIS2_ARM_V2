using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Models.Events;

/// <summary>
/// 角色權限異動事件（例如角色的權限位元被修改）
/// 需經 PermissionImpactService 將 RoleIds 轉換為受影響的 UserIds
/// </summary>
public sealed class RolePermissionChangedEvent : IPermissionChangedEvent
{
    public required string[] RoleIds { get; init; }
}