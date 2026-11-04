using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Services.Interfaces;
using StackExchange.Redis;

namespace HIS2_ARM_KeyDB.Services.Cache
{
    /// <summary>
    /// 快取同步服務 (CacheSyncService)
    /// [架構特徵]：職責專一，只負責管理 KeyDB/Redis 寫入與轉譯邏輯。
    /// 資料來源統一依賴高階的 OracleDbService，實現數據與快取的關注點分離。
    /// </summary>
    public class CacheSyncService : ICacheSyncService
    {
        private readonly OracleDbService _rmdb; 
        private readonly IConnectionMultiplexer _cacheDb;
        private readonly ILogger<CacheSyncService> _logger;
        
        // 配合 ARM 專案快取配置使用 DB 1，隔離其他系統快取
        private const int TargetDb = 1; 

        public CacheSyncService(
            OracleDbService rmdb, 
            IConnectionMultiplexer cacheDb,
            ILogger<CacheSyncService> logger)
        {
            _rmdb = rmdb;
            _cacheDb = cacheDb;
            _logger = logger;
        }

        /// <summary>
        /// 將 Oracle 內的 ARM_ACTION_DEF 門檻全數同步至 KeyDB Hash 表中
        /// 快取鍵格式：sys:required:{systemId}:{controller}（依 Controller 拆成多把 Key）
        /// 內部依 Controller 分組，逐一呼叫 LoadControllerActionDefinitionsToCacheAsync，
        /// 確保每個 Controller 的重載都是彼此獨立、安全的。
        /// </summary>
        public async Task LoadAllActionDefinitionsToCacheAsync(string systemId)
        {
            try
            {
                _logger.LogInformation("開始從 Oracle 撈取子系統 {SystemId} 的 Action 門檻定義...", systemId);
                var defs = (await _rmdb.GetActionDefsForCacheAsync(systemId)).ToList();

                if (!defs.Any())
                {
                    _logger.LogWarning("系統 {SystemId} 在 Oracle 中未找到任何 API 門檻設定。", systemId);
                    return;
                }

                var controllers = defs.Select(d => d.Controller).Distinct().ToList();
                _logger.LogInformation("系統 {SystemId} 共有 {Count} 個 Controller 需要重載門檻快取。", systemId, controllers.Count);

                foreach (var controller in controllers)
                {
                    await LoadControllerActionDefinitionsToCacheAsync(systemId, controller);
                }

                _logger.LogInformation("🚀 子系統 {SystemId} 功能門檻預熱成功，共同步 {Count} 個 Controller。", systemId, controllers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "全系統功能門檻快取預熱失敗 (LoadAllActionDefinitionsToCacheAsync): {SystemId}", systemId);
                throw;
            }
        }

        /// <summary>
        /// 💡 [Controller 級重載] 只重新載入指定 Controller 底下的 Action 門檻。
        /// 快取鍵格式：sys:required:{systemId}:{controller}
        /// 因 Key 已精準到 Controller，可安全整把 KeyDeleteAsync 後全量灌回，不會誤傷同系統下其他 Controller。
        /// </summary>
        public async Task LoadControllerActionDefinitionsToCacheAsync(string systemId, string controller)
        {
            try
            {
                var db = _cacheDb.GetDatabase(TargetDb);
                string cacheKey = $"sys:required:{systemId}:{controller}";

                var defs = (await _rmdb.GetActionDefsForCacheAsync(systemId, controller)).ToList();

                // Key 已精準到 Controller，可安全整把清除，不影響其他 Controller
                await db.KeyDeleteAsync(cacheKey);

                if (defs.Any())
                {
                    var hashEntries = defs.Select(d =>
                        new HashEntry(d.ActionName, d.RequiredBits)
                    ).ToArray();

                    await db.HashSetAsync(cacheKey, hashEntries);
                    _logger.LogInformation("Controller {Controller}@{SystemId} 門檻重載完成，共 {Count} 筆至快取 [{Key}]",
                        controller, systemId, defs.Count, cacheKey);
                }
                else
                {
                    _logger.LogWarning("Controller {Controller}@{SystemId} 查無任何有效 Action 門檻設定，已清空對應快取。",
                        controller, systemId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller 級快取重載失敗 (LoadControllerActionDefinitionsToCacheAsync): {SystemId}/{Controller}",
                    systemId, controller);
                throw;
            }
        }

        /// <summary>
        /// 將 Oracle 內的 ARM_PERMISSIONS 角色權限同步至 KeyDB Hash 表中
        /// 快取鍵格式：role:perm:{systemId}:{roleId}
        /// </summary>
        public async Task LoadRolePermissionsToCacheAsync(string systemId, string roleId)
        {
            try
            {
                var db = _cacheDb.GetDatabase(TargetDb);
                string cacheKey = $"role:perm:{systemId}:{roleId}";

                var perms = await _rmdb.GetRolePermsForCacheAsync(systemId, roleId);

                // 先清空舊快取
                await db.KeyDeleteAsync(cacheKey);

                if (perms != null && perms.Any())
                {
                    // 若 ActionName 為空，轉換為天花板權限代表符號 "*"
                    var hashEntries = perms.Select(p => 
                        new HashEntry(
                            $"{p.Controller}.{(string.IsNullOrEmpty(p.ActionName) ? "*" : p.ActionName)}", 
                            p.PermsBits)
                    ).ToArray();
                    
                    await db.HashSetAsync(cacheKey, hashEntries);
                    _logger.LogInformation("已同步角色權限至快取: {Key}, 共 {Count} 筆", cacheKey, perms.Count());
                }
                else
                {
                    // [防禦性設計] 防止快取穿透 (Cache Penetration)
                    // 當角色真的沒有任何權限時，塞入一個虛擬標記，防止前端請求穿透至 Oracle
                    await db.HashSetAsync(cacheKey, "_exists", 1);
                    await db.KeyExpireAsync(cacheKey, TimeSpan.FromMinutes(10)); 
                    _logger.LogWarning("角色 {RoleId} 在系統 {SystemId} 中無權限設定，已寫入 L2 防穿透標記。", roleId, systemId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "快取同步失敗 (LoadRolePermissionsToCacheAsync): {SystemId} / {RoleId}", systemId, roleId);
                throw;
            }
        }

        /// <summary>
        /// 💡 [跨系統重載] Role 為全域實體（ARM_ROLES.ROLE_ID 未綁定 SystemId），可能同時服務多個子系統。
        /// 當角色權限異動、但不確定影響範圍時，查出該角色目前有效權限所涉及的所有 SystemId，
        /// 逐一呼叫 LoadRolePermissionsToCacheAsync 進行重載，避免漏更新其他受影響系統的快取。
        /// </summary>
        public async Task LoadRolePermissionsAcrossAllSystemsAsync(string roleId)
        {
            try
            {
                var systemIds = (await _rmdb.GetDistinctSystemIdsByRoleAsync(roleId)).ToList();

                if (!systemIds.Any())
                {
                    _logger.LogWarning("角色 {RoleId} 目前查無任何有效系統權限設定，略過快取重載。", roleId);
                    return;
                }

                foreach (var systemId in systemIds)
                {
                    await LoadRolePermissionsToCacheAsync(systemId, roleId);
                }

                _logger.LogInformation("角色 {RoleId} 已跨 {Count} 個子系統完成快取重載：{Systems}",
                    roleId, systemIds.Count, string.Join(",", systemIds));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "跨系統角色快取重載失敗 (LoadRolePermissionsAcrossAllSystemsAsync): {RoleId}", roleId);
                throw;
            }
        }

        /// <summary>
        /// 自動預熱該系統所有的歷史角色權限
        /// 透過取得所有 RoleId 並迴圈呼叫 LoadRolePermissionsToCacheAsync 達成自我修復與預熱。
        /// </summary>
        public async Task LoadAllRolePermissionsToCacheAsync(string systemId)
        {
            try
            {
                _logger.LogInformation("開始獲取子系統 {SystemId} 擁有設定的所有歷史角色清單...", systemId);
                var roleIds = await _rmdb.GetDistinctRolesBySystemAsync(systemId);

                if (roleIds == null || !roleIds.Any())
                {
                    _logger.LogInformation("系統 {SystemId} 目前沒有任何歷史角色權限需要預熱。", systemId);
                    return;
                }

                _logger.LogInformation("開始對子系統 {SystemId} 的 {Count} 個角色進行迴圈同步...", systemId, roleIds.Count());
                
                // 迴圈呼叫單一角色同步方法
                foreach (var roleId in roleIds)
                {
                    await LoadRolePermissionsToCacheAsync(systemId, roleId);
                }

                _logger.LogInformation("✨ 系統 {SystemId} 的所有歷史角色權限已全數預熱完畢，共處理 {Count} 個角色。", systemId, roleIds.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "全系統角色快取預熱失敗 (LoadAllRolePermissionsToCacheAsync): {SystemId}", systemId);
                throw;
            }
        }
    }
}