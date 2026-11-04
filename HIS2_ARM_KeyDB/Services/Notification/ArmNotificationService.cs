using HIS2_ARM_KeyDB_Client.Models.Notifications;
using HIS2_ARM_KeyDB_Client.Options;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace HIS2_ARM_KeyDB.Services.Notification;

/// <summary>
/// ARM 權限異動通知服務。
///
/// 連線至 HubCenter 的 ArmHub（/armhub），
/// 將 PermissionImpactService 計算完成的 UserId 清單
/// 透過 SignalR 廣播給各子系統。
///
/// 本服務不負責：
/// - 權限異動判斷
/// - Impact 範圍計算
/// - Cache Invalidation
/// - Role → User / Action → Role → User 查詢
/// </summary>
public sealed class ArmNotificationService
    : BackgroundService, IArmNotificationService
{
    private readonly SignalROptions _options;
    private readonly ILogger<ArmNotificationService> _logger;

    private HubConnection? _hubConnection;

    public ArmNotificationService(
        IOptions<SignalROptions> options,
        ILogger<ArmNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 建立與 ArmHub 的 SignalR 連線。
    /// </summary>
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ArmHubUrl))
        {
            _logger.LogWarning(
                "SignalR ArmHubUrl 未設定，ARM Notification Service 不啟動");

            return;
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(_options.ArmHubUrl)
            .WithAutomaticReconnect()
            .Build();

        RegisterConnectionHandlers();

        try
        {
            await _hubConnection.StartAsync(stoppingToken);

            _logger.LogInformation(
                "ARM Notification Service 已連線至 ArmHub: {Url}",
                _options.ArmHubUrl);

            // BackgroundService 持續存活，
            // 實際 SignalR 工作由 IArmNotificationService 方法執行。
            await Task.Delay(
                Timeout.Infinite,
                stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "ARM Notification Service 正常停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ARM Notification Service 發生錯誤");
        }
    }

    /// <summary>
    /// 註冊 SignalR Connection 的生命週期事件。
    /// </summary>
    private void RegisterConnectionHandlers()
    {
        if (_hubConnection is null)
        {
            return;
        }

        _hubConnection.Reconnecting += error =>
        {
            _logger.LogWarning(
                error,
                "ARM Notification Service SignalR 正在重新連線");

            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            _logger.LogInformation(
                "ARM Notification Service SignalR 已重新連線。ConnectionId={ConnectionId}",
                connectionId);

            return Task.CompletedTask;
        };

        _hubConnection.Closed += error =>
        {
            if (error is not null)
            {
                _logger.LogWarning(
                    error,
                    "ARM Notification Service SignalR 連線已關閉");
            }
            else
            {
                _logger.LogInformation(
                    "ARM Notification Service SignalR 連線已關閉");
            }

            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// 通知所有子系統指定 User 的 Auth Cache 已失效。
    /// 對應 Hub 方法：SendToAll(eventName, payload)
    /// </summary>
    public async Task NotifyPermissionCacheInvalidatedAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        var evt = new PermissionCacheInvalidatedEvent
        {
            UserIds = ids
        };

        await SendAsync(
            ArmNotificationEvents.AuthCacheInvalidated,
            evt,
            cancellationToken);

        _logger.LogInformation(
            "已透過 SignalR 發送 {EventName}。UserCount={UserCount}",
            ArmNotificationEvents.AuthCacheInvalidated,
            ids.Length);
    }

    /// <summary>
    /// 實際執行 SignalR Hub Method：SendToAll
    /// </summary>
    private async Task SendAsync<T>(
        string eventName,
        T payload,
        CancellationToken cancellationToken)
    {
        var connection = _hubConnection;

        if (connection is null)
        {
            throw new InvalidOperationException(
                "ARM Notification Service 尚未建立 SignalR Connection。");
        }

        if (connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException(
                $"ARM Notification Service 尚未連線至 ArmHub。CurrentState={connection.State}");
        }

        // 對應 ArmHubLib.SendToAll(string eventName, object payload)
        await connection.InvokeAsync(
            "SendToAll",
            eventName,
            payload,
            cancellationToken);
    }

    /// <summary>
    /// 停止 SignalR Connection。
    /// </summary>
    public override async Task StopAsync(
        CancellationToken cancellationToken)
    {
        var connection = _hubConnection;

        if (connection is not null)
        {
            try
            {
                if (connection.State != HubConnectionState.Disconnected)
                {
                    await connection.StopAsync(
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "停止 ARM Notification Service SignalR Connection 時發生錯誤");
            }

            await connection.DisposeAsync();

            _hubConnection = null;
        }

        await base.StopAsync(cancellationToken);
    }
}
