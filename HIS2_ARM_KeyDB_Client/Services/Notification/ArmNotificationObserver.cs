using HIS2_ARM_KeyDB_Client.Models.Notifications;
using HIS2_ARM_KeyDB_Client.Options;
using HIS2_ARM_KeyDB_Client.Services.Interfaces;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HIS2_ARM_KeyDB_Client.Services.Notification;

/// <summary>
/// 子系統端：連線至 HubCenter ArmHub，監聽權限快取失效事件，
/// 並清除本機 L0 Auth Cache / Menu Cache。
///
/// 事件名稱必須與 ARM 端 ArmNotificationEvents.AuthCacheInvalidated 一致：
///   "PermissionCacheInvalidated"
/// </summary>
public sealed class ArmNotificationObserver : BackgroundService
{
    /// <summary>
    /// 與 ARM ArmNotificationEvents.AuthCacheInvalidated 必須一致。
    /// </summary>
    private const string EventName = "PermissionCacheInvalidated";

    private readonly SignalROptions _signalROptions;
    private readonly ArmClientOptions _armOptions;
    private readonly IL0AuthCacheService _authCacheService;
    private readonly IMenuCacheService _menuCacheService;
    private readonly ILogger<ArmNotificationObserver> _logger;

    private HubConnection? _connection;

    public ArmNotificationObserver(
        IOptions<SignalROptions> signalROptions,
        IOptions<ArmClientOptions> armOptions,
        IL0AuthCacheService authCacheService,
        IMenuCacheService menuCacheService,
        ILogger<ArmNotificationObserver> logger)
    {
        _signalROptions = signalROptions.Value;
        _armOptions = armOptions.Value;
        _authCacheService = authCacheService;
        _menuCacheService = menuCacheService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_signalROptions.ArmHubUrl))
        {
            _logger.LogWarning(
                "SignalR ArmHubUrl 未設定，ArmNotificationObserver 不啟動");
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(_signalROptions.ArmHubUrl)
            .WithAutomaticReconnect()
            .Build();

        // 監聽 ARM 廣播的權限失效事件
        _connection.On<PermissionCacheInvalidatedEvent>(
            EventName,
            HandlePermissionCacheInvalidated);

        // 斷線重連後要重新加入 Group
        _connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation(
                "ArmNotificationObserver 已重新連線。ConnectionId={ConnectionId}",
                connectionId);

            await TryJoinSystemGroupAsync(stoppingToken);
        };

        try
        {
            await _connection.StartAsync(stoppingToken);

            _logger.LogInformation(
                "ArmNotificationObserver 已連線至 ArmHub: {Url}",
                _signalROptions.ArmHubUrl);

            // 連線成功後加入本系統的 Group，才能接收 SendToGroup 定向廣播
            await TryJoinSystemGroupAsync(stoppingToken);

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("ArmNotificationObserver 正在停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArmNotificationObserver 發生錯誤");
        }
    }

    /// <summary>
    /// 呼叫 Hub 的 JoinSystemGroup，加入 sys:{SystemId}。
    /// SystemId 未設定時略過（仍可收到 SendToAll 全廣播）。
    /// </summary>
    private async Task TryJoinSystemGroupAsync(CancellationToken cancellationToken)
    {
        if (_connection is null
            || _connection.State != HubConnectionState.Connected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_armOptions.SystemId))
        {
            _logger.LogWarning(
                "ArmClientOptions.SystemId 未設定，略過 JoinSystemGroup（僅能收 SendToAll）");
            return;
        }

        try
        {
            await _connection.InvokeAsync(
                "JoinSystemGroup",
                _armOptions.SystemId,
                cancellationToken);

            _logger.LogInformation(
                "ArmNotificationObserver 已加入 Group sys:{SystemId}",
                _armOptions.SystemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "JoinSystemGroup 失敗。SystemId={SystemId}",
                _armOptions.SystemId);
        }
    }

    private void HandlePermissionCacheInvalidated(
        PermissionCacheInvalidatedEvent evt)
    {
        if (evt.UserIds is null || evt.UserIds.Length == 0)
        {
            return;
        }

        foreach (var userId in evt.UserIds)
        {
            _authCacheService.Invalidate(userId);
            _menuCacheService.Invalidate(userId);
        }

        _logger.LogInformation(
            "收到 {EventName}，已失效 UserId 數量：{Count}",
            EventName,
            evt.UserIds.Length);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            try
            {
                if (_connection.State != HubConnectionState.Disconnected)
                {
                    await _connection.StopAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止 ArmNotificationObserver SignalR 連線時發生錯誤");
            }

            await _connection.DisposeAsync();
            _connection = null;
        }

        await base.StopAsync(cancellationToken);
    }
}