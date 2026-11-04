namespace HIS2_ARM_KeyDB.Models.Events.Interfaces;

/// <summary>
/// 權限異動事件的標記介面 (Marker Interface)
/// 所有會經過 PermissionChangeDispatcher 分派的事件都必須實作此介面
/// </summary>
public interface IPermissionChangedEvent
{
}