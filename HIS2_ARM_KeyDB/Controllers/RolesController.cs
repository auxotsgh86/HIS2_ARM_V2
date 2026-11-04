using Microsoft.AspNetCore.Mvc;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;

namespace HIS2_ARM_KeyDB.Controllers;

/// <summary>
/// 角色主檔（ARM_ROLES）查詢與狀態管理控制器。
///
/// 💡 [補齊缺口]：前端「角色權限」頁左側需要一個「搜尋角色」欄位 + 分頁清單，
///    用來讓使用者先選定 RoleId，再接續呼叫 UserMgrController.GetUserData(roleId=...)
///    查詢「目前成員」、或 Admin/UserMgr 相關端點調整該角色的權限/成員。
///    角色的新增/建檔屬於另一個管理流程，暫不在此次需求範圍內；
///    但「啟用/停用」已補上（見 UpdateRoleStatus），因為對應的 Outbox 事件
///    （RoleStatusChangedEvent）原本是消費端全通、卻從未被任何端點產生的死代碼。
/// </summary>
[ApiController]
[Route("api/admin/[controller]")] // 路由對應為 /api/admin/roles
public class RolesController : ControllerBase
{
    private readonly OracleDbService _oracleDb;
    private readonly ILogger<RolesController> _logger;

    public RolesController(OracleDbService oracleDb, ILogger<RolesController> logger)
    {
        _oracleDb = oracleDb;
        _logger = logger;
    }

    /// <summary>
    /// 查詢角色清單（分頁），支援 Keyword（比對 RoleId 或 RoleName）/ IncludeInactive 篩選。
    /// 對應前端「角色權限」頁左側的「搜尋角色」欄位。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetRoles([FromQuery] RoleQueryRequest query)
    {
        var (items, total) = await _oracleDb.GetRolesAsync(
            query.Keyword, query.IncludeInactive, query.Page, query.PageSize);

        return Ok(new PagedResult<Role>(items.ToList(), total, query.Page, query.PageSize));
    }

    /// <summary>
    /// 查詢單一角色主檔資料（供角色詳情頁 / 表單預填使用）。
    /// </summary>
    [HttpGet("{roleId}")]
    public async Task<IActionResult> GetRole(string roleId)
    {
        var role = await _oracleDb.GetRoleByIdAsync(roleId);

        if (role is null)
        {
            _logger.LogWarning("查詢失敗，找不到角色: {RoleId}", roleId);
            return NotFound(new { Message = $"找不到角色 {roleId}" });
        }

        return Ok(role);
    }

    /// <summary>
    /// 【補齊缺口】查詢指定角色目前的完整權限矩陣（System/Controller/Action × CRUDP）。
    /// 對應前端「角色權限」頁需求：使用者先透過 GetRoles 選定 RoleId 後，
    /// 必須能先看到「這個 Role 現在到底有什麼權限」，才能進行編輯，
    /// 否則 AdminController.UpdateRolePermission 的寫入端點會變成只能盲寫。
    ///
    /// 💡 ARM_ROLES.ROLE_ID 為全域唯一（未綁定 SystemId），同一角色可能同時服務多個子系統，
    ///    因此：
    ///    - 帶 systemId → 只回傳該系統底下的權限矩陣（對應現有 PermMatrix 頁行為）
    ///    - 不帶 systemId → 自動查出該角色目前有效權限分佈的所有 SystemId，彙整回傳完整矩陣
    ///      （對應使用者範例中跨系統顯示的「System / Controller / Action / C R U D P」表格）
    /// </summary>
    [HttpGet("{roleId}/permissions")]
    public async Task<IActionResult> GetRolePermissions(string roleId, [FromQuery] string? systemId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return BadRequest(new { Message = "roleId 為必填" });

        var result = new List<RolePermDetail>();

        if (!string.IsNullOrWhiteSpace(systemId))
        {
            result.AddRange(await _oracleDb.GetPermissionsFullAsync(systemId, roleId));
        }
        else
        {
            var systemIds = await _oracleDb.GetDistinctSystemIdsByRoleAsync(roleId);
            foreach (var sid in systemIds)
                result.AddRange(await _oracleDb.GetPermissionsFullAsync(sid, roleId));
        }

        return Ok(result
            .OrderBy(p => p.SystemId)
            .ThenBy(p => p.Controller)
            .ThenBy(p => p.ActionName)
            .ToList());
    }

    /// <summary>
    /// 【補齊缺口】啟用/停用角色（ARM_ROLES.IS_ACTIVE）。
    /// 💡 [稽核發現] RoleStatusChangedEvent / RoleStatusChangedEventHandler /
    ///    PermissionImpactService.GetAffectedUserIdsAsync(RoleStatusChangedEvent) 原本已
    ///    完整實作，卻沒有任何端點會產生這個事件——這支方法正是補上的生產端。
    /// 停用角色後，該角色所屬使用者的 Authorization Cache 會被 Handler 標記失效並透過
    /// SignalR 廣播（不強制重載 role:perm，交由 TTL 自然到期，詳見 RoleStatusChangedEvent 註解）。
    /// </summary>
    [HttpPut("{roleId}/status")]
    public async Task<IActionResult> UpdateRoleStatus(string roleId, [FromBody] UpdateRoleStatusRequest request)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return BadRequest(new { Message = "roleId 為必填" });

        var ok = await _oracleDb.UpdateRoleStatusAsync(roleId, request.IsActive, operatorUserId: User.Identity?.Name);

        if (!ok)
        {
            _logger.LogWarning("更新角色狀態失敗，找不到角色: {RoleId}", roleId);
            return NotFound(new { Message = $"找不到角色 {roleId}" });
        }

        var statusText = request.IsActive ? "啟用" : "停用";
        _logger.LogInformation("[Roles] UpdateRoleStatus 已排入 Outbox: RoleId={RoleId}, IsActive={IsActive}",
            roleId, request.IsActive);

        return Ok(new
        {
            Message = $"角色 {roleId} 已{statusText}，已排入 Outbox 佇列，稍後將由背景 Worker 同步受影響使用者的授權快取。"
        });
    }
}

