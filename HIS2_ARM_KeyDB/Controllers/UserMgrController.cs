using Microsoft.AspNetCore.Mvc;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;

namespace HIS2_ARM_KeyDB.Controllers;

/// <summary>
/// 使用者角色指派管理控制器（重建自已停用的 UserMgrController_.cs_）。
///
/// 💡 [重建紀錄]：舊版引用的 KeyDbService / PermissionService / AuthorizeActionAttribute /
///    ApiResult 等類別在目前架構下皆已不存在（分別被 ValkeyDbService / AuthService /
///    SelfArmAuthorizationMiddleware 取代，且該中介軟體已經是全域套用，不需要再額外掛
///    Action 層級的授權特性），因此改為對齊目前 AdminController / UsersController 的實作風格重寫。
///
/// 💡 [Outbox 整合]：角色重新指派會同時影響「角色集合」與「啟用狀態」兩種語意，
///    因此 UpdateUserAsync 內部會一次送出 UserRoleChanged 與 UserStatusChanged 兩個事件，
///    交由 OutboxWorker 背景處理 Cache Invalidation / Session 撤銷 / SignalR 通知。
/// </summary>
[ApiController]
[Route("api/admin/[controller]")] // 路由對應為 /api/admin/usermgr
public class UserMgrController : ControllerBase
{
    private readonly OracleDbService _oracleDb;
    private readonly ValkeyDbService _valkeyDb;
    private readonly ILogger<UserMgrController> _logger;

    public UserMgrController(
        OracleDbService oracleDb,
        ValkeyDbService valkeyDb,
        ILogger<UserMgrController> logger)
    {
        _oracleDb = oracleDb;
        _valkeyDb = valkeyDb;
        _logger = logger;
    }

    /// <summary>
    /// 新增單一使用者（含初始角色指派）。
    /// 💡 全新使用者沒有任何既存 Cache 可以失效，因此不需要透過 Outbox 通知。
    /// </summary>
    [HttpPost("AddOneUser")]
    public async Task<IActionResult> AddOneUser([FromBody] AddUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { Message = "UserId 與 Password 為必填" });

        var user = new User
        {
            UserId = req.UserId.Trim().ToUpper(),
            Username = req.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password, 12),
            RoleIds = req.RoleIds,
            Department = req.Department
        };

        var ok = await _oracleDb.CreateUserAsync(user);
        if (!ok)
            return Conflict(new { Message = "新增失敗，帳號可能已存在" });

        _logger.LogInformation("[UserMgr] AddOneUser: {UserId}", user.UserId);
        return Ok(new { user.UserId, Message = "使用者已建立" });
    }

    /// <summary>
    /// 批次新增使用者。
    /// </summary>
    [HttpPost("BatchAddUsers")]
    public async Task<IActionResult> BatchAddUsers([FromBody] BatchAddUsersRequest req)
    {
        if (req.Users is null || req.Users.Count == 0)
            return BadRequest(new { Message = "使用者清單不可為空" });

        var users = req.Users.Select(r => new User
        {
            UserId = r.UserId.Trim().ToUpper(),
            Username = r.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password, 12),
            RoleIds = r.RoleIds,
            Department = r.Department
        }).ToList();

        var ok = await _oracleDb.BatchCreateUsersAsync(users);
        if (!ok)
            return BadRequest(new { Message = "批次新增失敗" });

        _logger.LogInformation("[UserMgr] BatchAddUsers: {Count}", users.Count);
        return Ok(new { Count = users.Count, Message = $"已建立 {users.Count} 位使用者" });
    }

    /// <summary>
    /// 查詢使用者清單（分頁），支援 UserId / RoleId / ExcludeRoleId / Department / Keyword 篩選。
    /// 💡 帶 RoleId 時 → 「Role 目前成員」清單；帶 ExcludeRoleId 時 → 「新增成員」候選名單（排除已是成員者）。
    /// </summary>
    [HttpGet("GetUserData")]
    public async Task<IActionResult> GetUserData([FromQuery] UserQueryRequest query)
    {
        var (items, total) = await _oracleDb.GetUsersAsync(
            query.UserId, query.RoleId, query.ExcludeRoleId, query.Department, query.Keyword,
            query.Page, query.PageSize);

        var result = items.Select(u => new
        {
            u.UserId, u.Username, u.RoleIds, u.Department,
            u.IsActive, u.CreatedAt, u.LastLoginAt, u.LastLoginIp
        }).ToList();

        return Ok(new PagedResult<object>(result, total, query.Page, query.PageSize));
    }

    /// <summary>
    /// 批次將「既有」使用者加入指定角色（對應「選取多人 → 一次送出 → 加入 Role」情境）。
    /// 💡 與 BatchAddUsers 不同：操作對象是已存在的使用者，只異動 ARM_USER_ROLES，
    ///    不會也不應該重新 INSERT INTO ARM_USERS。
    /// </summary>
    [HttpPost("AssignUsersToRole")]
    public async Task<IActionResult> AssignUsersToRole([FromBody] AssignUsersToRoleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RoleId) || req.UserIds is null || req.UserIds.Count == 0)
            return BadRequest(new { Message = "RoleId 與 UserIds 為必填" });

        var affected = await _oracleDb.AssignUsersToRoleAsync(
            req.RoleId, req.UserIds, operatorUserId: User.Identity?.Name);

        _logger.LogInformation("[UserMgr] AssignUsersToRole 已排入 Outbox: RoleId={RoleId}, Count={Count}",
            req.RoleId, affected);

        return Ok(new
        {
            Message = $"已將 {affected} 位使用者加入角色 {req.RoleId}，已排入 Outbox 佇列，稍後將由背景 Worker 同步快取。",
            AffectedCount = affected
        });
    }

    /// <summary>
    /// 更新使用者（角色重新指派 + 啟用狀態）。
    /// 💡 [Outbox] OracleDbService.UpdateUserAsync 內部已將「UPDATE ARM_USERS」+
    ///    「重建 ARM_USER_ROLES」+「Outbox Insert (UserRoleChanged / UserStatusChanged)」
    ///    納入同一個 Transaction，這裡不再需要（也不應該）自己直接呼叫 ValkeyDbService。
    /// </summary>
    [HttpPost("UpdOneUser")]
    public async Task<IActionResult> UpdOneUser([FromQuery] string userId, [FromBody] UpdUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { Message = "userId 為必填" });

        var user = new User
        {
            UserId = userId,
            Username = req.Username,
            RoleIds = req.RoleIds,
            Department = req.Department,
            IsActive = req.IsActive
        };

        var ok = await _oracleDb.UpdateUserAsync(user, operatorUserId: User.Identity?.Name);
        if (!ok)
            return BadRequest(new { Message = "更新失敗" });

        _logger.LogInformation("[UserMgr] UpdOneUser 已排入 Outbox: {UserId}", userId);
        return Ok(new { Message = "使用者已更新，已排入 Outbox 佇列，稍後將由背景 Worker 同步快取與 Session。" });
    }

    /// <summary>
    /// 強制刪除單一 Session（維運工具，非屬 Outbox 涵蓋的 5 種權限異動事件，
    /// 這是管理員針對「特定一條連線」的即時操作，維持同步直接呼叫。）
    /// </summary>
    [HttpPost("DeleteOneSession")]
    public async Task<IActionResult> DeleteOneSession([FromBody] DeleteOneSessionRequest req)
    {
        await _valkeyDb.DeleteOneSessionAsync(req.Jti);
        _logger.LogInformation("[UserMgr] DeleteOneSession: {Jti}", req.Jti);
        return Ok(new { Message = $"Session {req.Jti} 已強制刪除" });
    }

    /// <summary>
    /// 查詢指定使用者目前所有在線 Session。
    /// </summary>
    [HttpGet("GetUserSessions")]
    public async Task<IActionResult> GetUserSessions([FromQuery] string userId)
        => Ok(await _valkeyDb.GetSessionsByUserAsync(userId));

    /// <summary>
    /// 查詢目前所有在線使用者，及各自的 Session 清單。
    /// 💡 直接從 KeyDB 的 user_sessions:* Key 掃描彙整，不觸及 Oracle，符合「在線」即時性語意。
    /// </summary>
    [HttpGet("GetOnlineUsers")]
    public async Task<IActionResult> GetOnlineUsers()
    {
        var data = await _valkeyDb.GetAllOnlineUsersWithSessionsAsync();
        var result = data.Select(kv => new
        {
            UserId = kv.Key,
            SessionCount = kv.Value.Count,
            Sessions = kv.Value
        });

        return Ok(result);
    }
}

