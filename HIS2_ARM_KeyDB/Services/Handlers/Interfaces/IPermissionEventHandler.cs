using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Services.Handlers.Interfaces;

/// <summary>
/// 權限異動事件的處理器介面
/// 透過 CanHandle 判斷是否能處理傳入的事件，交由 PermissionChangeDispatcher 動態尋找對應實例
/// </summary>
public interface IPermissionEventHandler
{
    /// <summary>此 Handler 是否能處理傳入的事件</summary>
    bool CanHandle(IPermissionChangedEvent evt);

    Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default);
}