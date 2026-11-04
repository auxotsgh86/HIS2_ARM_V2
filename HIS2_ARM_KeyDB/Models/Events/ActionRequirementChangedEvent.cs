using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Models.Events;

public sealed class ActionResourceChanged
{
    public required string System { get; init; }
    public required string Controller { get; init; }
    public required string ActionName { get; init; }
}

/// <summary>
/// API 門檻異動事件（例如某個 Action 所需的權限被修改）
/// 需經 PermissionImpactService 將 Action -> Role -> UserIds 轉換
/// </summary>
public sealed class ActionRequirementChangedEvent : IPermissionChangedEvent
{
    public required ActionResourceChanged[] Actions { get; init; }
}