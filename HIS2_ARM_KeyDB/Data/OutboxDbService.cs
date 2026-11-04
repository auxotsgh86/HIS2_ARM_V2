using System.Data;
using Dapper;
using HIS2_ARM_KeyDB.Models.Outbox;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Oracle.ManagedDataAccess.Client;

namespace HIS2_ARM_KeyDB.Data;

/// <summary>
/// Outbox 事件的 Oracle 存取服務（Dapper）。Schema: HIS2USER2，資料表：ARM_OUTBOX_EVENTS。
///
/// [架構特徵]
/// 1. EnqueueAsync 刻意接受呼叫端傳入的 IDbConnection/IDbTransaction，
///    確保「Business Mutation」與「Outbox INSERT」落在同一個 Oracle Transaction 內，
///    Transaction Commit 失敗時 Outbox 也不會單獨存在（Atomic）。
/// 2. Dequeue/MarkProcessed/MarkFailed/RecoverStalledEvents 則比照 OracleDbService 的慣例，
///    內部自行開關連線，因為這些操作與呼叫端（OutboxWorker）沒有共用 Transaction 的需求。
/// 3. Claim SQL 刻意不使用 `FETCH FIRST n ROWS ONLY` + `FOR UPDATE SKIP LOCKED` 的組合
///    （Oracle 的 row_limiting_clause 與 for_update_clause 不能如此並用），改採 ROWNUM 寫法。
/// </summary>
public class OutboxDbService : IOutboxService
{
    private readonly string _connStr;
    private readonly ILogger<OutboxDbService> _logger;

    /// <summary>
    /// 💡 Exponential Backoff 排程表：依 RETRY_COUNT（0-based）依序取用重試間隔，
    ///    超出陣列範圍則沿用最後一組（10 分鐘）。
    /// </summary>
    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    ];

    public OutboxDbService(IConfiguration config, ILogger<OutboxDbService> logger)
    {
        _connStr = config.GetConnectionString("Oracle")
                   ?? throw new InvalidOperationException("Oracle connection string 未設定");
        _logger = logger;
    }

    /// <summary>內部輔助方法：每次呼叫皆產生新連線物件，搭配 using 自動歸還連線池</summary>
    private OracleConnection Conn() => new(_connStr);

    // ═══════════════════════════════════════════════════════════
    // 寫入端：Enqueue（必須與呼叫端共用同一個 Transaction）
    // ═══════════════════════════════════════════════════════════

    public async Task EnqueueAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string eventType,
        string? aggregateKey,
        string payloadJson,
        string? createUser,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
                           INSERT INTO HIS2USER2.ARM_OUTBOX_EVENTS
                               (EVENT_ID, EVENT_TYPE, AGGREGATE_KEY, PAYLOAD_JSON, CREATE_USER)
                           VALUES
                               (SYS_GUID(), :EventType, :AggregateKey, :PayloadJson, :CreateUser)
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                EventType = eventType,
                AggregateKey = aggregateKey,
                PayloadJson = payloadJson,
                CreateUser = createUser
            },
            transaction: transaction,
            cancellationToken: cancellationToken));

        _logger.LogInformation(
            "Outbox event enqueued: type={EventType}, aggregateKey={AggregateKey}",
            eventType, aggregateKey);
    }

    // ═══════════════════════════════════════════════════════════
    // Worker 端：Dequeue（Claim，多 Worker/多 Instance 併發安全）
    // ═══════════════════════════════════════════════════════════

    public async Task<IReadOnlyCollection<OutboxEvent>> DequeueAsync(
        int batchSize,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = Conn();
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // 💡 不要用 FETCH FIRST n ROWS ONLY 搭配 FOR UPDATE SKIP LOCKED（Oracle 不支援此組合），
            //    改以 ROWNUM 限制筆數；此寫法不保證嚴格 FIFO，符合文件第 21 節的設計決定。
            const string claimSql = """
                                    SELECT EVENT_ID       AS EventId,
                                           EVENT_TYPE     AS EventType,
                                           AGGREGATE_KEY  AS AggregateKey,
                                           PAYLOAD_JSON   AS PayloadJson,
                                           STATUS         AS Status,
                                           RETRY_COUNT    AS RetryCount,
                                           MAX_RETRY      AS MaxRetry
                                    FROM   HIS2USER2.ARM_OUTBOX_EVENTS
                                    WHERE  STATUS = 'PENDING'
                                      AND  NEXT_RETRY_TIME <= SYSDATE
                                      AND  ROWNUM <= :BatchSize
                                    FOR UPDATE SKIP LOCKED
                                    """;

            var claimed = (await conn.QueryAsync<OutboxEvent>(new CommandDefinition(
                claimSql,
                new { BatchSize = batchSize },
                transaction: tx,
                cancellationToken: cancellationToken))).ToList();

            if (claimed.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return Array.Empty<OutboxEvent>();
            }

            const string lockSql = """
                                   UPDATE HIS2USER2.ARM_OUTBOX_EVENTS
                                   SET    STATUS = 'PROCESSING', LOCKED_BY = :WorkerId, LOCKED_TIME = SYSDATE
                                   WHERE  EVENT_ID = :EventId
                                   """;

            foreach (var evt in claimed)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    lockSql,
                    new { WorkerId = workerId, evt.EventId },
                    transaction: tx,
                    cancellationToken: cancellationToken));
            }

            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Outbox claimed {Count} events by worker {WorkerId}", claimed.Count, workerId);
            return claimed;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Outbox Dequeue 失敗，workerId={WorkerId}", workerId);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Worker 端：狀態收斂
    // ═══════════════════════════════════════════════════════════

    public async Task MarkProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var conn = Conn();
        const string sql = """
                           UPDATE HIS2USER2.ARM_OUTBOX_EVENTS
                           SET    STATUS = 'PROCESSED', PROCESSED_TIME = SYSDATE, LOCKED_BY = NULL, LOCKED_TIME = NULL
                           WHERE  EVENT_ID = :EventId
                           """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { EventId = eventId }, cancellationToken: cancellationToken));

        _logger.LogInformation("Outbox event processed: {EventId}", eventId);
    }

    public async Task MarkFailedAsync(OutboxEvent evt, Exception exception, CancellationToken cancellationToken = default)
    {
        var nextRetryCount = evt.RetryCount + 1;
        var isExhausted = nextRetryCount >= evt.MaxRetry;
        var backoff = BackoffSchedule[Math.Min(evt.RetryCount, BackoffSchedule.Length - 1)];
        var errorMessage = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;

        await using var conn = Conn();
        const string sql = """
                           UPDATE HIS2USER2.ARM_OUTBOX_EVENTS
                           SET    STATUS          = :Status,
                                  RETRY_COUNT      = :RetryCount,
                                  NEXT_RETRY_TIME  = SYSDATE + (:BackoffSeconds / 86400),
                                  LAST_ERROR       = :LastError,
                                  LOCKED_BY        = NULL,
                                  LOCKED_TIME      = NULL
                           WHERE  EVENT_ID = :EventId
                           """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Status = isExhausted ? "FAILED" : "PENDING",
                RetryCount = nextRetryCount,
                BackoffSeconds = backoff.TotalSeconds,
                LastError = errorMessage,
                evt.EventId
            },
            cancellationToken: cancellationToken));

        _logger.LogWarning(exception,
            "Outbox event failed: {EventId}, retry={RetryCount}/{MaxRetry}, status={Status}",
            evt.EventId, nextRetryCount, evt.MaxRetry, isExhausted ? "FAILED" : "PENDING");
    }

    public async Task<int> RecoverStalledEventsAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default)
    {
        await using var conn = Conn();

        // 💡 PROCESSING 且 LOCKED_TIME 早於 (現在 - staleThreshold) 視為 Worker Crash 遺留的殘留鎖，
        //    重設回 PENDING 讓下一輪輪詢可以重新 Claim。
        const string sql = """
                           UPDATE HIS2USER2.ARM_OUTBOX_EVENTS
                           SET    STATUS = 'PENDING', LOCKED_BY = NULL, LOCKED_TIME = NULL
                           WHERE  STATUS = 'PROCESSING'
                             AND  LOCKED_TIME < SYSDATE - (:StaleMinutes / 1440)
                           """;

        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { StaleMinutes = staleThreshold.TotalMinutes },
            cancellationToken: cancellationToken));

        if (affected > 0)
        {
            _logger.LogWarning("Outbox stale PROCESSING 事件已回收 {Count} 筆，重設為 PENDING", affected);
        }

        return affected;
    }
}



