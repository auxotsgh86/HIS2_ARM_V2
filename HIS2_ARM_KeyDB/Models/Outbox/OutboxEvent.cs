namespace HIS2_ARM_KeyDB.Models.Outbox;

/// <summary>
/// 對應 HIS2USER2.ARM_OUTBOX_EVENTS 的資料模型。
/// 💡 [設計原則] 這裡只放 Worker/Dispatcher 實際需要用到的欄位，
///    LOCKED_BY / LOCKED_TIME / CREATE_TIME 等維運追蹤欄位不在此暴露，
///    避免 Handler 誤用內部鎖定狀態做業務判斷。
/// </summary>
public sealed class OutboxEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? AggregateKey { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public int MaxRetry { get; init; }
}

