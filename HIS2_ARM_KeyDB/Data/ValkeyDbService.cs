using System.Text.Json;
using HIS2_ARM_KeyDB.Models;
using StackExchange.Redis;

namespace HIS2_ARM_KeyDB.Data;

/// <summary>
/// KeyDB 三層結構 (導入子系統 SYSTEM_ID 隔離與快取載入機制)
///
/// 層一  session:{jti}           → SessionInfo JSON        TTL = AccessExpire
///       refresh:{refreshJti}    → RefreshSession JSON     TTL = RefreshExpire
///       user_sessions:{userId}  → Set<jti>                TTL = AccessExpire+5min
///
/// 層二  user_roles:{userId}     → JSON string[]           TTL = 8h
///
/// 層三  [權限配置] role:perm:{systemId}:{roleId}   → Redis HASH (由 ICacheSyncService 載入)
///         field "Record.*"           value "22"  (Controller 天花板)
///         field "Record.ManageRecord" value "6"  (BITAND 有效值)
///       [功能門檻] sys:required:{systemId}:{controller}  → Redis HASH (由 ICacheSyncService 載入)
///         field "ManageRecord" value "15" (REQUIRED_BITS)
///         💡 [Key 級隔離] Key 已精準到 Controller，Controller 重載時可安全整把 KeyDeleteAsync，
///            不會誤傷同系統下其他 Controller 的快取（對齊 role:perm 的設計哲學）。
///
/// 查詢邏輯（極致簡化 Pipeline）:
///   1. HGET "sys:required:{systemId}:{controller}" 取得 API 門檻值
///   2. Pipeline 批次 HGET 多個 Role 的 Action 有效位元與 Controller 天花板
///   (註: Cache Miss 的補填已統一交由管理端的 CacheSyncService 處理，此處專注讀取效能)
/// </summary>
public class ValkeyDbService
{
    private readonly IDatabase   _db;
    private readonly ISubscriber _sub;
    private readonly ILogger<ValkeyDbService> _logger;

    // 層一與層二 (全院 SSO 共用，不區分 SystemId)
    private const string SessionKey      = "session:";
    private const string RefreshKey      = "refresh:";
    private const string UserSessionKey  = "user_sessions:";
    private const string UserRolesKey    = "user_roles:";
    
    // Pub/Sub 頻道
    private const string PermChannel     = "role_perm_update";

    public ValkeyDbService(IConnectionMultiplexer redis, ILogger<ValkeyDbService> logger)
    {
        _db  = redis.GetDatabase(1); // 依專案架構預設指向 DB=1
        _sub = redis.GetSubscriber();
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════
    // 層一：session + refresh
    // ═══════════════════════════════════════════════════════════

    public async Task SetSessionAsync(SessionInfo s, TimeSpan ttl)
    {
        await _db.StringSetAsync(SessionKey + s.Jti, Serialize(s), ttl);
        var uk = UserSessionKey + s.UserId;
        await _db.SetAddAsync(uk, s.Jti);
        await _db.KeyExpireAsync(uk, ttl.Add(TimeSpan.FromMinutes(5)));
    }

    public async Task<SessionInfo?> GetSessionAsync(string jti)
    {
        var val = await _db.StringGetAsync(SessionKey + jti);
        if (!val.HasValue) return null;
        var s = Deserialize<SessionInfo>(val!);
        return s?.IsRevoked == true ? null : s;
    }

    public async Task RevokeSessionAsync(string jti)
    {
        var key = SessionKey + jti;
        var val = await _db.StringGetAsync(key);
        if (!val.HasValue) return;
        var s = Deserialize<SessionInfo>(val!);
        if (s is null) return;
        s.IsRevoked = true;
        var ttl = await _db.KeyTimeToLiveAsync(key);
        await _db.StringSetAsync(key, Serialize(s), ttl ?? TimeSpan.FromMinutes(10));
        _logger.LogInformation("Session revoked: jti={Jti}", jti);
    }

    public async Task SetRefreshSessionAsync(RefreshSession rs, TimeSpan ttl)
        => await _db.StringSetAsync(RefreshKey + rs.RefreshJti, Serialize(rs), ttl);

    public async Task<RefreshSession?> GetRefreshSessionAsync(string refreshJti)
    {
        var val = await _db.StringGetAsync(RefreshKey + refreshJti);
        if (!val.HasValue) return null;
        var rs = Deserialize<RefreshSession>(val!);
        return rs?.IsRevoked == true ? null : rs;
    }

    public async Task RevokeRefreshSessionAsync(string refreshJti)
    {
        var key = RefreshKey + refreshJti;
        var val = await _db.StringGetAsync(key);
        if (!val.HasValue) return;
        var rs = Deserialize<RefreshSession>(val!);
        if (rs is null) return;
        rs.IsRevoked = true;
        var ttl = await _db.KeyTimeToLiveAsync(key);
        await _db.StringSetAsync(key, Serialize(rs), ttl ?? TimeSpan.FromMinutes(10));
    }

    public async Task RevokeAllSessionsByUserAsync(string userId)
    {
        var jtis = await _db.SetMembersAsync(UserSessionKey + userId);
        foreach (var j in jtis)
        {
            var val = await _db.StringGetAsync(SessionKey + j);
            if (val.HasValue)
            {
                var s = Deserialize<SessionInfo>(val!);
                if (!string.IsNullOrEmpty(s?.RefreshJti))
                    await RevokeRefreshSessionAsync(s.RefreshJti);
            }
            await RevokeSessionAsync(j.ToString());
        }
        await _db.KeyDeleteAsync(UserSessionKey + userId);
        await DeleteUserRolesAsync(userId);
        _logger.LogInformation("All sessions revoked: userId={UserId}", userId);
    }

    public async Task DeleteOneSessionAsync(string jti)
    {
        var val = await _db.StringGetAsync(SessionKey + jti);
        if (val.HasValue)
        {
            var s = Deserialize<SessionInfo>(val!);
            if (s is not null)
            {
                await _db.SetRemoveAsync(UserSessionKey + s.UserId, jti);
                if (!string.IsNullOrEmpty(s.RefreshJti))
                    await _db.KeyDeleteAsync(RefreshKey + s.RefreshJti);
            }
        }
        await _db.KeyDeleteAsync(SessionKey + jti);
        _logger.LogInformation("Session deleted: jti={Jti}", jti);
    }

    public async Task<List<SessionInfo>> GetSessionsByUserAsync(string userId)
    {
        var jtis = await _db.SetMembersAsync(UserSessionKey + userId);
        var list = new List<SessionInfo>();
        foreach (var j in jtis)
        {
            var s = await GetSessionAsync(j.ToString());
            if (s is not null) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// 列出所有目前在線的使用者，並附上各自的 Session 清單。
    /// 💡 以 SCAN（IServer.KeysAsync 內部走 SCAN cursor）取代 KEYS，避免在使用者量大時
    ///    阻塞單執行緒的 Redis/KeyDB。若未來使用者規模持續成長，建議改維護一個
    ///    全域 Set（如 "online_users"）於 SetSessionAsync/RevokeSessionAsync 時
    ///    同步 SADD/SREM，將本查詢複雜度降為 O(1) SMEMBERS。
    /// </summary>
    public async Task<Dictionary<string, List<SessionInfo>>> GetAllOnlineUsersWithSessionsAsync()
    {
        var endpoints = _db.Multiplexer.GetEndPoints();
        var result = new Dictionary<string, List<SessionInfo>>();

        foreach (var endpoint in endpoints)
        {
            var server = _db.Multiplexer.GetServer(endpoint);
            if (server.IsReplica) continue;

            await foreach (var key in server.KeysAsync(database: _db.Database, pattern: UserSessionKey + "*"))
            {
                var userId = key.ToString().Substring(UserSessionKey.Length);
                if (result.ContainsKey(userId)) continue;

                var sessions = await GetSessionsByUserAsync(userId);
                if (sessions.Count > 0)
                    result[userId] = sessions;
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 層二：user_roles:{userId}
    // ═══════════════════════════════════════════════════════════

    public async Task<List<string>?> GetUserRolesAsync(string userId)
    {
        var val = await _db.StringGetAsync(UserRolesKey + userId);
        return val.HasValue ? Deserialize<List<string>>(val!) : null;
    }

    public async Task SetUserRolesAsync(string userId, List<string> roleIds, TimeSpan? ttl = null)
        => await _db.StringSetAsync(UserRolesKey + userId,
            Serialize(roleIds), ttl ?? TimeSpan.FromHours(8));

    public async Task DeleteUserRolesAsync(string userId)
    {
        await _db.KeyDeleteAsync(UserRolesKey + userId);
        _logger.LogDebug("user_roles evicted: userId={UserId}", userId);
    }

    // ═══════════════════════════════════════════════════════════
    // 層三：role:perm:{systemId}:{roleId} 與 sys:required:{systemId}
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 取得指定 Action 節點所需要的門檻權限 (REQUIRED_BITS)
    /// 💡 [Key 級隔離] Key 已精準到 Controller，field 只需 ActionName
    /// </summary>
    public async Task<int?> GetRequiredBitsAsync(string systemId, string controller, string actionName)
    {
        var val = await _db.HashGetAsync($"sys:required:{systemId}:{controller}", actionName);
        return ParseBits(val);
    }

    /// <summary>確認角色權限快取是否存在</summary>
    public async Task<bool> RolePermsCachedAsync(string systemId, string roleId)
        => await _db.KeyExistsAsync($"role:perm:{systemId}:{roleId}");

    /// <summary>
    /// Pipeline 批次查詢：針對該使用者的多個角色，同時送出 HGET
    /// </summary>
    public async Task<Dictionary<string, (int? ActionBits, int? ControllerBits)>>
        GetEffectiveBitsAsync(string systemId, IEnumerable<string> roleIds,
            string controller, string actionName)
    {
        var roles        = roleIds.ToList();
        var actionField  = $"{controller}.{actionName}";
        var ctlField     = $"{controller}.*";
        var batch        = _db.CreateBatch();

        var actionTasks = roles.ToDictionary(
            r => r,
            r => batch.HashGetAsync($"role:perm:{systemId}:{r}", actionField));

        var ctlTasks = roles.ToDictionary(
            r => r,
            r => batch.HashGetAsync($"role:perm:{systemId}:{r}", ctlField));

        batch.Execute();
        await Task.WhenAll(
            actionTasks.Values.Cast<Task>()
            .Concat(ctlTasks.Values.Cast<Task>()));

        return roles.ToDictionary(
            r => r,
            r => (
                ParseBits(actionTasks[r].Result),
                ParseBits(ctlTasks[r].Result)
            ));
    }

    /// <summary>
    /// 💡 [架構優化] 將「API 門檻查詢」與「多角色權限查詢」打包成同一次 Redis 網路往返 (Pipelining)，
    /// 比照 PermissionDecisionService.VerifyAccessAsync 的作法，減少一次 round-trip。
    /// </summary>
    public async Task<(int? RequiredBits, Dictionary<string, (int? ActionBits, int? ControllerBits)> RolePerms)>
        GetPermissionEvaluationDataAsync(string systemId, IEnumerable<string> roleIds,
            string controller, string actionName)
    {
        var roles       = roleIds.ToList();
        var actionField = $"{controller}.{actionName}";
        var ctlField    = $"{controller}.*";
        var batch       = _db.CreateBatch();

        // 💡 sys:required 已改為 Key 級隔離 (sys:required:{systemId}:{controller})，field 只留 ActionName
        var reqTask = batch.HashGetAsync($"sys:required:{systemId}:{controller}", actionName);

        var actionTasks = roles.ToDictionary(
            r => r,
            r => batch.HashGetAsync($"role:perm:{systemId}:{r}", actionField));

        var ctlTasks = roles.ToDictionary(
            r => r,
            r => batch.HashGetAsync($"role:perm:{systemId}:{r}", ctlField));

        batch.Execute();

        await Task.WhenAll(
            new Task[] { reqTask }
            .Concat(actionTasks.Values.Cast<Task>())
            .Concat(ctlTasks.Values.Cast<Task>()));

        var rolePerms = roles.ToDictionary(
            r => r,
            r => (
                ParseBits(actionTasks[r].Result),
                ParseBits(ctlTasks[r].Result)
            ));

        return (ParseBits(reqTask.Result), rolePerms);
    }

    /// <summary>清除特定子系統下特定角色的權限快取</summary>
    public async Task DeleteRolePermsAsync(string systemId, string roleId)
    {
        await _db.KeyDeleteAsync($"role:perm:{systemId}:{roleId}");
        _logger.LogInformation("role_perms evicted: systemId={SystemId}, roleId={RoleId}", systemId, roleId);
    }

    /// <summary>
    /// 清除指定系統下、指定 Controller 的所有 Action 門檻快取。
    /// 💡 [Key 級隔離] 因 Key 已精準到 Controller，可安全整把 KeyDeleteAsync，不會誤傷同系統下其他 Controller。
    /// 對應 ActionRequirementChangedEvent 的 Controller 級重載場景。
    /// </summary>
    public async Task DeleteControllerRequiredBitsAsync(string systemId, string controller)
    {
        await _db.KeyDeleteAsync($"sys:required:{systemId}:{controller}");
        _logger.LogInformation("L2 RequiredBits(Controller) evicted: sys={Sys}, controller={Controller}", systemId, controller);
    }

    /// <summary>移除單一 Action 的門檻快取欄位（細粒度場景，例如單一 Action 被停用）</summary>
    public async Task DeleteRequiredBitsAsync(string systemId, string controller, string actionName)
    {
        await _db.HashDeleteAsync($"sys:required:{systemId}:{controller}", actionName);
        _logger.LogInformation("L2 RequiredBits deleted: sys={Sys}, controller={Controller}, action={Action}", systemId, controller, actionName);
    }
    
    // ═══════════════════════════════════════════════════════════
    // Pub/Sub — 角色/功能權限異動推播
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 💡 [修正]：推播訊息改為 "SystemId|Payload" 格式，避免 ActionName 中的 '.' 或 ':' 影響切割
    /// payload 可以是 "MRA_ANALYST" 或是 "ACTION_UPDATE:Mram030.Index"
    /// </summary>
    public async Task PublishRolePermUpdateAsync(string systemId, string payload)
    {
        string message = $"{systemId}|{payload}";
        var n = await _sub.PublishAsync(RedisChannel.Literal(PermChannel), message);
        _logger.LogInformation("Pub/Sub published: {Message}, nodes={N}", message, n);
    }

    /// <summary>
    /// 訂閱端收到推播後，解析並交由 handler 處理
    /// </summary>
    public void SubscribeRolePermUpdate(Func<string, string, Task> handler)
    {
        _sub.Subscribe(RedisChannel.Literal(PermChannel), async (_, msg) =>
        {
            var msgStr = msg.ToString();
            // 💡 [修正]：使用 | 切割，確保 ActionName 等後方內容的完整性
            var parts = msgStr.Split('|'); 
            if (parts.Length == 2)
            {
                _logger.LogInformation("Pub/Sub received: sys={Sys}, payload={Payload}", parts[0], parts[1]);
                await handler(parts[0], parts[1]);
            }
        });
        _logger.LogInformation("Subscribed to: {Channel}", PermChannel);
    }

    // ═══════════════════════════════════════════════════════════
    // Health
    // ═══════════════════════════════════════════════════════════

    public async Task<bool> PingAsync()
        => (await _db.PingAsync()) > TimeSpan.Zero;

    // ─── Helpers ────────────────────────────────────────────────
    private static string  Serialize<T>(T obj)      => JsonSerializer.Serialize(obj);
    private static T?      Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s);
    private static int?    ParseBits(RedisValue v)   =>
        v.HasValue && int.TryParse(v.ToString(), out var i) ? i : null;
}