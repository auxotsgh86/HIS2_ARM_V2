using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Models.Events.Interfaces;
using HIS2_ARM_KeyDB.Services.Handlers.Interfaces;
using HIS2_ARM_KeyDB.Services.Interfaces;

/// <summary>
/// 處理人員角色異動：直接轉發，無需查詢 Impact
/// 💡 對應快取層二 user_roles:{userId}，與 ICacheSyncService（層三 role:perm / sys:required）無關。
/// </summary>
public sealed class UserRoleChangedEventHandler : IPermissionEventHandler
{
    private readonly ValkeyDbService _cacheDb;
    private readonly IArmNotificationService _notificationService;
    private readonly ILogger<UserRoleChangedEventHandler> _logger;

    public UserRoleChangedEventHandler(
        ValkeyDbService cacheDb,
        IArmNotificationService notificationService,
        ILogger<UserRoleChangedEventHandler> logger)
    {
        _cacheDb = cacheDb;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(IPermissionChangedEvent evt) => evt is UserRoleChangedEvent;

    public async Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt is not UserRoleChangedEvent typedEvt)
            throw new ArgumentException($"預期 {nameof(UserRoleChangedEvent)}，實際收到 {evt.GetType().Name}", nameof(evt));

        if (typedEvt.UserIds == null || typedEvt.UserIds.Length == 0) return;

        // 💡 層二快取（user_roles:{userId}）失效，交由下次請求 Lazy Loading 回填最新角色清單
        foreach (var userId in typedEvt.UserIds)
        {
            await _cacheDb.DeleteUserRolesAsync(userId);
        }

        _logger.LogInformation("處理 UserRole 異動，已清除 {Count} 位使用者的 user_roles 快取，並廣播快取失效。", typedEvt.UserIds.Length);
        await _notificationService.NotifyPermissionCacheInvalidatedAsync(typedEvt.UserIds, cancellationToken);
    }
}

/// <summary>
/// 處理角色權限異動：需透過 ImpactService 轉換 Role -> Users，
/// 並透過 ICacheSyncService 跨系統重載該角色的 role:perm 快取。
/// </summary>
public sealed class RolePermissionChangedEventHandler : IPermissionEventHandler
{
    private readonly ICacheSyncService _cacheSync;
    private readonly IPermissionImpactService _impactService;
    private readonly IArmNotificationService _notificationService;
    private readonly ILogger<RolePermissionChangedEventHandler> _logger;

    public RolePermissionChangedEventHandler(
        ICacheSyncService cacheSync,
        IPermissionImpactService impactService,
        IArmNotificationService notificationService,
        ILogger<RolePermissionChangedEventHandler> logger)
    {
        _cacheSync = cacheSync;
        _impactService = impactService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(IPermissionChangedEvent evt) => evt is RolePermissionChangedEvent;

    public async Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt is not RolePermissionChangedEvent typedEvt)
            throw new ArgumentException($"預期 {nameof(RolePermissionChangedEvent)}，實際收到 {evt.GetType().Name}", nameof(evt));

        _logger.LogInformation("處理 RolePermission 異動，開始跨系統重載 KeyDB 快取...");

        // 💡 Role 為全域實體，可能同時服務多個子系統，逐一角色跨系統重載 role:perm 快取
        foreach (var roleId in typedEvt.RoleIds)
        {
            await _cacheSync.LoadRolePermissionsAcrossAllSystemsAsync(roleId);
        }

        _logger.LogInformation("開始計算受影響使用者...");
        var affectedUsers = await _impactService.GetAffectedUserIdsAsync(typedEvt, cancellationToken);

        if (affectedUsers.Any())
        {
            await _notificationService.NotifyPermissionCacheInvalidatedAsync(affectedUsers, cancellationToken);
        }
    }
}

/// <summary>
/// 處理角色狀態異動（ARM_ROLES.IS_ACTIVE）：需透過 ImpactService 轉換 Role -> Users，
/// 但【不】重載 role:perm 快取——Role Permission 與 Role Active Status 是不同語意的資料，
/// role:perm 讓它自然依 TTL 到期即可，真正需要立即失效的是該 Role 所屬 Users 的授權快取。
/// </summary>
public sealed class RoleStatusChangedEventHandler : IPermissionEventHandler
{
    private readonly IPermissionImpactService _impactService;
    private readonly IArmNotificationService _notificationService;
    private readonly ILogger<RoleStatusChangedEventHandler> _logger;

    public RoleStatusChangedEventHandler(
        IPermissionImpactService impactService,
        IArmNotificationService notificationService,
        ILogger<RoleStatusChangedEventHandler> logger)
    {
        _impactService = impactService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(IPermissionChangedEvent evt) => evt is RoleStatusChangedEvent;

    public async Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt is not RoleStatusChangedEvent typedEvt)
            throw new ArgumentException($"預期 {nameof(RoleStatusChangedEvent)}，實際收到 {evt.GetType().Name}", nameof(evt));

        _logger.LogInformation("處理 RoleStatus 異動，不重載 role:perm，僅計算受影響使用者...");

        var affectedUsers = await _impactService.GetAffectedUserIdsAsync(typedEvt, cancellationToken);

        if (affectedUsers.Any())
        {
            await _notificationService.NotifyPermissionCacheInvalidatedAsync(affectedUsers, cancellationToken);
        }
    }
}

/// <summary>
/// 處理人員狀態異動（ARM_USERS.IS_ACTIVE）：已知受影響的 UserId，無需查詢 Impact。
/// 除了讓授權快取失效外，User 被停用時也必須撤銷其 Session（強制登出），
/// 因此除了 user_roles 之外，也一併呼叫 RevokeAllSessionsByUserAsync 撤銷 session/refresh。
/// </summary>
public sealed class UserStatusChangedEventHandler : IPermissionEventHandler
{
    private readonly ValkeyDbService _cacheDb;
    private readonly IArmNotificationService _notificationService;
    private readonly ILogger<UserStatusChangedEventHandler> _logger;

    public UserStatusChangedEventHandler(
        ValkeyDbService cacheDb,
        IArmNotificationService notificationService,
        ILogger<UserStatusChangedEventHandler> logger)
    {
        _cacheDb = cacheDb;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(IPermissionChangedEvent evt) => evt is UserStatusChangedEvent;

    public async Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt is not UserStatusChangedEvent typedEvt)
            throw new ArgumentException($"預期 {nameof(UserStatusChangedEvent)}，實際收到 {evt.GetType().Name}", nameof(evt));

        if (typedEvt.UserIds == null || typedEvt.UserIds.Length == 0) return;

        // 💡 RevokeAllSessionsByUserAsync 內部已包含：撤銷所有 session/refresh session + 刪除 user_roles，
        //    對應文件第 9 節「除了 Cache Invalidation，也應該撤銷該 User 的 Session」的要求。
        foreach (var userId in typedEvt.UserIds)
        {
            await _cacheDb.RevokeAllSessionsByUserAsync(userId);
        }

        _logger.LogInformation("處理 UserStatus 異動，已撤銷 {Count} 位使用者的 Session 並清除快取，並廣播快取失效。",
            typedEvt.UserIds.Length);
        await _notificationService.NotifyPermissionCacheInvalidatedAsync(typedEvt.UserIds, cancellationToken);
    }
}

/// <summary>
/// 處理 API 門檻異動：需透過 ImpactService 轉換 Action -> Roles -> Users，
/// 並透過 ICacheSyncService 依 (SystemId, Controller) 分組重載 sys:required 快取。
/// </summary>
public sealed class ActionRequirementChangedEventHandler : IPermissionEventHandler
{
    private readonly ICacheSyncService _cacheSync;
    private readonly IPermissionImpactService _impactService;
    private readonly IArmNotificationService _notificationService;
    private readonly ILogger<ActionRequirementChangedEventHandler> _logger;

    public ActionRequirementChangedEventHandler(
        ICacheSyncService cacheSync,
        IPermissionImpactService impactService,
        IArmNotificationService notificationService,
        ILogger<ActionRequirementChangedEventHandler> logger)
    {
        _cacheSync = cacheSync;
        _impactService = impactService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(IPermissionChangedEvent evt) => evt is ActionRequirementChangedEvent;

    public async Task HandleAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt is not ActionRequirementChangedEvent typedEvt)
            throw new ArgumentException($"預期 {nameof(ActionRequirementChangedEvent)}，實際收到 {evt.GetType().Name}", nameof(evt));

        _logger.LogInformation("處理 ActionRequirement 異動，開始依 (System, Controller) 分組重載 KeyDB 快取...");

        // 💡 sys:required:{systemId}:{controller} 為 Key 級隔離結構，依 (System, Controller) 去重分組後逐一重載，
        // 不會誤傷同系統下其他無關 Controller 的快取。
        var groups = typedEvt.Actions
            .Select(a => (a.System, a.Controller))
            .Distinct();

        foreach (var (systemId, controller) in groups)
        {
            await _cacheSync.LoadControllerActionDefinitionsToCacheAsync(systemId, controller);
        }

        _logger.LogInformation("開始計算受影響使用者...");
        var affectedUsers = await _impactService.GetAffectedUserIdsAsync(typedEvt, cancellationToken);

        if (affectedUsers.Any())
        {
            await _notificationService.NotifyPermissionCacheInvalidatedAsync(affectedUsers, cancellationToken);
        }
    }
}