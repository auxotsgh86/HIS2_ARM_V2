using System.Data;
using HIS2_ARM_KeyDB.Models.Outbox;

namespace HIS2_ARM_KeyDB.Services.Interfaces;

/// <summary>
/// Outbox 事件的寫入 / 取出 / 狀態管理介面。
/// 💡 [核心原則] EnqueueAsync 必須使用呼叫端已經開好的 connection/transaction，
///    絕不能自己另外開連線或自行 Commit，否則無法保證「Business Mutation 與 Outbox INSERT 同一 Transaction」。
/// </summary>
public interface IOutboxService
{
    /// <summary>
    /// 將事件寫入 Outbox，必須與呼叫端的 Business Mutation 共用同一個 connection/transaction。
    /// </summary>
    Task EnqueueAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string eventType,
        string? aggregateKey,
        string payloadJson,
        string? createUser,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Worker 端輪詢並鎖定一批待處理事件（STATUS: PENDING → PROCESSING）。
    /// 💡 內部採用 Oracle `FOR UPDATE SKIP LOCKED`，多 Instance/多 Worker 併發安全。
    /// </summary>
    Task<IReadOnlyCollection<OutboxEvent>> DequeueAsync(
        int batchSize,
        string workerId,
        CancellationToken cancellationToken = default);

    /// <summary>標記事件已成功處理完畢（STATUS → PROCESSED）。</summary>
    Task MarkProcessedAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 標記事件處理失敗，依 Exponential Backoff 排定下次重試時間；
    /// 若已達 MAX_RETRY 上限則轉為 FAILED，交由人工排查。
    /// </summary>
    Task MarkFailedAsync(
        OutboxEvent evt,
        Exception exception,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stale PROCESSING Recovery：找出鎖定時間超過 staleThreshold 卻仍卡在 PROCESSING 的事件，
    /// 重新設回 PENDING，讓下一輪 Worker 可以重新 Claim（因應 Worker Crash 後鎖未釋放的情境）。
    /// </summary>
    Task<int> RecoverStalledEventsAsync(
        TimeSpan staleThreshold,
        CancellationToken cancellationToken = default);
}

