namespace HIS2_ARM_KeyDB.Services.Interfaces
{
    /// <summary>
    /// 快取同步服務介面 (CacheSyncService)
    /// 負責將 Oracle 內的權限核心資料同步至 KeyDB/Redis 結構中。
    /// </summary>
    public interface ICacheSyncService
    {
        /// <summary>
        /// 💡 [名稱對齊] 將指定子系統（如 HIS2_MRA）的「所有」功能需求門檻位元（REQUIRED_BITS）全數預載入至 KeyDB。
        /// 快取結構：Redis HASH，依 Controller 分拆為多把 Key (sys:required:{systemId}:{controller})
        /// 內部會依 Controller 分組，逐一呼叫 LoadControllerActionDefinitionsToCacheAsync。
        /// </summary>
        Task LoadAllActionDefinitionsToCacheAsync(string systemId);

        /// <summary>
        /// 💡 [Controller 級重載] 只重新載入指定 Controller 底下的 Action 門檻，
        /// 因 Key 已精準到 Controller (sys:required:{systemId}:{controller})，可安全整把 KeyDeleteAsync 後全量灌回，
        /// 不會誤傷同系統下其他無關 Controller 的快取（縮小 Blast Radius）。
        /// 觸發時機：Outbox 消費 ActionRequirementChangedEvent 時，依 (SystemId, Controller) 分組處理。
        /// </summary>
        Task LoadControllerActionDefinitionsToCacheAsync(string systemId, string controller);

        /// <summary>
        /// 將指定子系統下、「特定角色」的實際被賦予權限位元（PERMS_BITS）載入至 KeyDB。
        /// 觸發時機：後台 Admin 修改單一角色權限時，或 Cache Miss 的 Lazy Loading。
        /// 快取結構：Redis HASH (role:perm:{systemId}:{roleId})
        /// </summary>
        Task LoadRolePermissionsToCacheAsync(string systemId, string roleId);

        /// <summary>
        /// 💡 [跨系統重載] Role 為全域實體（ARM_ROLES.ROLE_ID 未綁定 SystemId），可能同時服務多個子系統。
        /// 當角色權限異動、但不確定影響範圍時，重載該角色在「所有」有設定過權限的系統下的快取。
        /// 內部會查出該角色涉及的所有 SystemId，逐一呼叫 LoadRolePermissionsToCacheAsync。
        /// </summary>
        Task LoadRolePermissionsAcrossAllSystemsAsync(string roleId);

        /// <summary>
        /// 批次將指定子系統下「所有擁有權限的歷史角色」全數預載入至 KeyDB。
        /// 觸發時機：AP 伺服器啟動或維運人員手動觸發全域預熱 (Warm-up)。
        /// </summary>
        Task LoadAllRolePermissionsToCacheAsync(string systemId);
    }
    
    /// <summary>
    /// 【跨系統造橋防腐服務介面】
    /// 負責與 Oracle 資料庫互動，獲取或生成舊版的 32 碼 Token。
    /// 將此邏輯抽象為介面，確保核心認證中心 (AuthService) 與資料庫操作完全解耦。
    /// </summary>
    public interface ILegacyTokenService
    {
        /// <summary>
        /// 獲取或產生一組寫入 Oracle 的有效舊版 32 碼簽證
        /// </summary>
        /// <param name="userId">登入使用者的帳號</param>
        /// <param name="clientIp">使用者的前端真實 IP</param>
        /// <returns>回傳 32 碼隨機 Token 字串。若發生異常則回傳空字串。</returns>
        Task<string> GetOrGenerateTokenToRmdbAsync(string userId, string clientIp);

        Task RevokeRmdbTokenAsync(string userId);
    }
}