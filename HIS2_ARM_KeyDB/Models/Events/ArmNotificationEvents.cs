// namespace HIS2_ARM_KeyDB.Models.Events;
//
// public static class ArmNotificationEvents
// {
//     public const string PermissionCacheInvalidated =
//         "PermissionCacheInvalidated";
// }

namespace HIS2_ARM_KeyDB.Models.Events;

/// <summary>
/// ARM → Hub → 子系統 的 SignalR 事件名稱常數。
/// 必須與 Client 端 ArmNotificationObserver 的監聽名稱一致。
/// </summary>
public static class ArmNotificationEvents
{
    /// <summary>
    /// 權限快取失效通知。
    /// Client 端：connection.On&lt;PermissionCacheInvalidatedEvent&gt;("PermissionCacheInvalidated", ...)
    /// </summary>
    public const string AuthCacheInvalidated = "PermissionCacheInvalidated";
}