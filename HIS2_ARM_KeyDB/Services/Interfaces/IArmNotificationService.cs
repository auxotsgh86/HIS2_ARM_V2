namespace HIS2_ARM_KeyDB.Services.Interfaces;

/// <summary>
/// ARM 權限異動通知服務。
/// 負責將已計算完成的 Cache Invalidation 範圍透過 SignalR 發送出去。
/// </summary>
public interface IArmNotificationService
{
    Task NotifyPermissionCacheInvalidatedAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken = default);
}