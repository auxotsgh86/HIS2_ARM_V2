using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using HIS2_ARM_KeyDB.Attributes;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace HIS2_ARM_KeyDB.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    public class BootstrapController : ControllerBase
    {
        private readonly OracleDbService _oracleDb;
        private readonly ICacheSyncService _cacheSync;
        private readonly ILogger<BootstrapController> _logger;

        public BootstrapController(OracleDbService oracleDb, ICacheSyncService cacheSync, ILogger<BootstrapController> logger)
        {
            _oracleDb = oracleDb;
            _cacheSync = cacheSync;
            _logger = logger;
        }
        
        /// <summary>
        /// 接收子系統 (如 HIS2_MRA) 自動註冊的 API 拓撲，並自動同步與預熱相關快取
        /// </summary>
        [HttpPost("register-endpoints/{systemId}")]
        [AllowAnonymous] // 🛡️ 鑿洞 1：告訴底層身分驗證警衛，這支 API 不用查 JWT (解決 401 錯誤)
        [BypassArmAuth]  // 🛡️ 鑿洞 2：告訴 ARM 授權中介軟體，這支 API 不用查 BITAND 權限
        public async Task<IActionResult> RegisterEndpoints(string systemId, [FromBody] List<ControllerActionInfo> endpoints)
        {
            // 💡 加上基礎的防呆驗證
            if (string.IsNullOrWhiteSpace(systemId) || endpoints == null || !endpoints.Any())
            {
                return BadRequest(new { Error = "必須指定有效的 SystemId 與至少一個 API 端點" });
            }

            _logger.LogInformation("開始處理子系統 {SystemId} 的 API 拓撲自動註冊，共收到 {Count} 個端點...", systemId, endpoints.Count);

            try
            {
                // 1. 落地 API 拓撲到 Oracle (有則忽略，無則新增)
                int newAddedCount = await _oracleDb.RegisterEndpointsAsync(systemId, endpoints);
        
                // 2. 預熱「功能門檻 (sys:required)」
                // 💡 [修正]：呼叫更名後的對稱方法，一次性刷入該系統的所有 Action 門檻
                await _cacheSync.LoadAllActionDefinitionsToCacheAsync(systemId);

                // 3. 自動預熱該系統所有的歷史角色權限 (role:perm)
                // 💡 確保子系統一上線，Redis 裡就備妥所有舊有使用者的權限，達成自我修復 (Self-Healing)
                await _cacheSync.LoadAllRolePermissionsToCacheAsync(systemId);

                _logger.LogInformation("✅ 系統 {SystemId} 註冊完成。共新增 {Count} 筆新端點，拓撲與角色權限已全數載入 L2 快取。", systemId, newAddedCount);

                return Ok(new 
                { 
                    Message = "Registration successful, topology and role permissions cache synced.", 
                    NewEndpointsAdded = newAddedCount 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "子系統 {SystemId} API 拓撲註冊與預熱過程發生異常", systemId);
                return StatusCode(500, new { Error = "系統自動註冊與快取預熱失敗" });
            }
        }
        
        /// <summary>
        /// 供維運人員手動觸發的快取預熱端點 (保留做為 Ops 工具)
        /// 💡 [優雅設計]：完美的對稱呼叫，一次完成「角色權限」與「API門檻」的全面洗牌與預熱
        /// </summary>
        [HttpPost("warmup-legacy-permissions/{systemId}")]
        public async Task<IActionResult> WarmupLegacyPermissions(string systemId)
        {
            if (string.IsNullOrWhiteSpace(systemId))
            {
                return BadRequest(new { Error = "必須指定有效的 SystemId" });
            }

            _logger.LogInformation("🚨 [Ops 維運手動觸發] 開始為系統 {SystemId} 進行歷史權限與 API 門檻快取預熱...", systemId);

            try
            {
                // 🧩 拼圖 1：預熱角色的權限 (ROLE_BITS) -> 內部會自動處理防穿透標記
                await _cacheSync.LoadAllRolePermissionsToCacheAsync(systemId);

                // 🧩 拼圖 2：預熱 API 的門檻要求 (REQUIRED_BITS) -> 採用覆蓋式 Hash 寫入
                await _cacheSync.LoadAllActionDefinitionsToCacheAsync(systemId);

                _logger.LogInformation("✨ 系統 {SystemId} 的雙重快取手動預熱順利完成。", systemId);

                return Ok(new 
                { 
                    Message = $"Successfully warmed up role permissions AND action definitions for system: {systemId}",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "🚨 維運端點預熱期間發生嚴重異常！系統：{SystemId}", systemId);
                return StatusCode(500, new { Error = "快取預熱期間發生內部嚴重錯誤", Details = ex.Message });
            }
        }
    }
}