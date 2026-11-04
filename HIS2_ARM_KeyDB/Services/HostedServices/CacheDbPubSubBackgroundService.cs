using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Services.Cache;

namespace HIS2_ARM_KeyDB.Services.HostedServices;

/// <summary>
/// Redis Pub/Sub 背景監聽服務
/// 負責長駐聆聽全院的權限異動廣播，並觸發本機 L1 快取銷毀
/// 💡 [優雅設計]: 實作 BackgroundService，隨應用程式啟動並在背景默默守護資安
/// </summary>
public class CacheDbPubSubBackgroundService : BackgroundService
{
    private readonly ValkeyDbService _cacheDb;
    private readonly CacheTokenManager _tokenManager;
    private readonly ILogger<CacheDbPubSubBackgroundService> _logger;

    public CacheDbPubSubBackgroundService(
        ValkeyDbService cacheDb, 
        CacheTokenManager tokenManager, 
        ILogger<CacheDbPubSubBackgroundService> logger)
    {
        _cacheDb = cacheDb;
        _tokenManager = tokenManager;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("啟動 Redis Pub/Sub L1 快取連動監聽服務...");

        // 訂閱 ValkeyDbService 中定義的權限異動頻道
        _cacheDb.SubscribeRolePermUpdate((systemId, payload) =>
        {
            // payload 可能是 "MRA_ANALYST" (角色權限異動)
            // 也可能是 "ACTION_UPDATE:Mram030.Index" (功能門檻異動)
            _logger.LogInformation("收到廣播 [{SystemId}]: {Payload}，準備引爆本機對應 L1 快取引信...", systemId, payload);
            
            if (payload.StartsWith("ACTION_UPDATE:"))
            {
                // 處理 Action 門檻異動的雙引信引爆
                // 擷取 Controller.Action，並組合成 TokenManager 認得的 ActionKey
                string actionKey = payload.Substring("ACTION_UPDATE:".Length);
                string tokenKey = $"ACT:{systemId}:{actionKey}";
                
                _tokenManager.Evict(tokenKey);
            }
            else
            {
                // 處理 Role 權限異動的引爆
                // 💡 RoleId 是跨子系統共用的識別碼（同一個角色在不同系統可能有不同權限位元），
                // 引信 key 必須加上 systemId 前綴，才不會「改了 A 系統的角色權限，卻連帶引爆 B 系統的 L1」
                _tokenManager.Evict($"{systemId}:{payload}");
            }
            
            return Task.CompletedTask;
        });

        return Task.CompletedTask;
    }
}