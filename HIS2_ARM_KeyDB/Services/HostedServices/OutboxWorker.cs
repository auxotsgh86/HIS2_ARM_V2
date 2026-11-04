using System.Text.Json;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Models.Events.Interfaces;
using HIS2_ARM_KeyDB.Models.Outbox;
using HIS2_ARM_KeyDB.Services.Dispatch;
using HIS2_ARM_KeyDB.Services.Interfaces;

namespace HIS2_ARM_KeyDB.Services.HostedServices;

/// <summary>
/// Outbox 事件背景消化服務。
///
/// 生命週期：
///   PENDING → (Claim) PROCESSING → Dispatcher/Handler → PROCESSED
///                                        └─ 失敗 → Backoff 排程 → PENDING（重試）
///                                                             └─ 逾 MAX_RETRY → FAILED
///
/// 💡 [架構特徵]
/// 1. IOutboxService / IPermissionChangeDispatcher 皆為 Scoped，BackgroundService 本身是 Singleton，
///    因此每一輪輪詢都必須透過 IServiceScopeFactory 建立新的 DI Scope，不能建構子直接注入。
/// 2. 單一輪詢例外（例如 Oracle 暫時斷線）只記錄 Log，不讓整個 BackgroundService 掛掉；
///    單一事件處理例外則交給 IOutboxService.MarkFailedAsync 走 Retry/Backoff。
/// 3. Handler 內部（Cache Reload / Delete / Publish / SignalR）都必須是冪等操作，
///    因為 At-Least-Once Delivery 下，同一事件可能被處理超過一次。
/// </summary>
public sealed class OutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxWorker> _logger;
    private readonly string _workerId;

    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    private readonly TimeSpan _staleThreshold;
    private readonly TimeSpan _staleCheckInterval;

    private DateTime _lastStaleCheckUtc = DateTime.MinValue;

    public OutboxWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<OutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // 💡 workerId 用於 LOCKED_BY 欄位識別，格式：MachineName-ProcessId，
        //    方便維運從殘留的 PROCESSING 事件追蹤是哪台 Instance 的 Worker 卡住。
        _workerId = $"{Environment.MachineName}-{Environment.ProcessId}";

        _pollInterval = TimeSpan.FromSeconds(config.GetValue("Outbox:PollIntervalSeconds", 5));
        _batchSize = config.GetValue("Outbox:BatchSize", 20);
        _staleThreshold = TimeSpan.FromMinutes(config.GetValue("Outbox:StaleThresholdMinutes", 5));
        _staleCheckInterval = TimeSpan.FromSeconds(config.GetValue("Outbox:StaleCheckIntervalSeconds", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxWorker 啟動，workerId={WorkerId}, pollInterval={Interval}s, batchSize={BatchSize}",
            _workerId, _pollInterval.TotalSeconds, _batchSize);

        using var timer = new PeriodicTimer(_pollInterval);

        // 啟動時先跑一輪，不用等第一個 tick
        await RunOnceAsync(stoppingToken);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("OutboxWorker 收到停止信號，準備關閉。");
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxService>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IPermissionChangeDispatcher>();

            // 💡 Stale PROCESSING Recovery：不需要每一輪都跑，依 _staleCheckInterval 節流，
            //    避免每 5 秒就多打一次 UPDATE。
            if (DateTime.UtcNow - _lastStaleCheckUtc >= _staleCheckInterval)
            {
                var recovered = await outbox.RecoverStalledEventsAsync(_staleThreshold, stoppingToken);
                if (recovered > 0)
                {
                    _logger.LogWarning("OutboxWorker 回收 {Count} 筆卡在 PROCESSING 的殘留事件", recovered);
                }
                _lastStaleCheckUtc = DateTime.UtcNow;
            }

            var events = await outbox.DequeueAsync(_batchSize, _workerId, stoppingToken);
            if (events.Count == 0)
            {
                return;
            }

            _logger.LogInformation("OutboxWorker 取得 {Count} 筆待處理事件", events.Count);

            foreach (var evt in events)
            {
                await ProcessEventAsync(outbox, dispatcher, evt, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關閉流程，不記錄為錯誤
        }
        catch (Exception ex)
        {
            // 💡 這裡是「整輪輪詢」層級的例外（例如 Oracle 暫時斷線），
            //    不應讓 BackgroundService 整個掛掉，記錄後留待下一輪重試。
            _logger.LogError(ex, "OutboxWorker 輪詢過程發生未預期例外，將於下一輪重試");
        }
    }

    private async Task ProcessEventAsync(
        IOutboxService outbox,
        IPermissionChangeDispatcher dispatcher,
        OutboxEvent evt,
        CancellationToken cancellationToken)
    {
        try
        {
            var domainEvent = DeserializePayload(evt);
            await dispatcher.DispatchAsync(domainEvent, cancellationToken);
            await outbox.MarkProcessedAsync(evt.EventId, cancellationToken);

            _logger.LogInformation(
                "Outbox event 處理成功: {EventId} ({EventType})", evt.EventId, evt.EventType);
        }
        catch (Exception ex)
        {
            // 💡 At-Least-Once：這裡失敗只代表這一輪沒處理成功，事件會依 Backoff 排程回到 PENDING 等待重試，
            //    因此 Handler 內部的每一步都必須允許重複執行而不產生副作用錯誤。
            _logger.LogError(ex,
                "Outbox event 處理失敗: {EventId} ({EventType})，retry={RetryCount}/{MaxRetry}",
                evt.EventId, evt.EventType, evt.RetryCount, evt.MaxRetry);
            await outbox.MarkFailedAsync(evt, ex, cancellationToken);
        }
    }

    /// <summary>
    /// 依 EVENT_TYPE 將 PAYLOAD_JSON 反序列化成對應的 IPermissionChangedEvent 實作型別。
    /// </summary>
    private static IPermissionChangedEvent DeserializePayload(OutboxEvent evt)
    {
        IPermissionChangedEvent? result = evt.EventType switch
        {
            OutboxEventTypes.UserRoleChanged =>
                JsonSerializer.Deserialize<UserRoleChangedEvent>(evt.PayloadJson),

            OutboxEventTypes.RolePermissionChanged =>
                JsonSerializer.Deserialize<RolePermissionChangedEvent>(evt.PayloadJson),

            OutboxEventTypes.ActionRequirementChanged =>
                JsonSerializer.Deserialize<ActionRequirementChangedEvent>(evt.PayloadJson),

            OutboxEventTypes.RoleStatusChanged =>
                JsonSerializer.Deserialize<RoleStatusChangedEvent>(evt.PayloadJson),

            OutboxEventTypes.UserStatusChanged =>
                JsonSerializer.Deserialize<UserStatusChangedEvent>(evt.PayloadJson),

            _ => throw new NotSupportedException($"尚未支援的 Outbox EventType：{evt.EventType}")
        };

        return result ?? throw new InvalidOperationException(
            $"Outbox payload 反序列化結果為 null：eventId={evt.EventId}, eventType={evt.EventType}");
    }
}

