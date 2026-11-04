using Microsoft.AspNetCore.Mvc;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Dto;
using HIS2_ARM_KeyDB.Models;

namespace HIS2_ARM_KeyDB.Controllers;

/// <summary>
/// 全院使用者帳號生命週期管理控制器
/// 負責處理人員帳號的新增、查詢、修改與停權 (軟刪除)。
///
/// 💡 [架構修正紀錄]：本檔案原本有兩個獨立 Bug 導致所有端點在執行期必然失敗：
///    1. 連線字串誤用不存在的設定 Key "OracleDb"（appsettings 實際只有 "Oracle"）。
///    2. 自行拼寫的 SQL 欄位名稱（USER_NAME/DEPT_ID/CREATED_AT）與 ARM_USERS 正式 Schema
///       （USERNAME/DEPARTMENT/CREATE_TIME）不符，執行必噴 ORA-00904。
///    現已改為統一呼叫已驗證過欄位正確的 OracleDbService，並將 Cache/Session 副作用
///    交給 Outbox + OutboxWorker 非同步處理，不再由 Controller 直接呼叫 ValkeyDbService。
/// </summary>
[ApiController]
[Route("api/admin/[controller]")] // 路由會對應為 /api/admin/users
public class UsersController : ControllerBase
{
    private readonly OracleDbService _oracleDb;
    private readonly ILogger<UsersController> _logger;

    public UsersController(OracleDbService oracleDb, ILogger<UsersController> logger)
    {
        _oracleDb = oracleDb;
        _logger = logger;
    }

    /// <summary>
    /// 建立新使用者帳號
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDto request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.UserName))
            return BadRequest("使用者代碼與名稱為必填欄位");

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("初始密碼為必填欄位");

        var user = new User
        {
            UserId = request.UserId,
            Username = request.UserName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 12),
            Department = request.DeptId,
            IsActive = true
        };

        try
        {
            var ok = await _oracleDb.CreateUserAsync(user);
            if (!ok)
            {
                _logger.LogWarning("建立使用者失敗，代碼可能已存在: {UserId}", request.UserId);
                return Conflict($"使用者代碼 {request.UserId} 已存在，或建立失敗");
            }

            _logger.LogInformation("✅ 使用者帳號建立成功: {UserId} - {UserName}", request.UserId, request.UserName);
            return Ok(new { Message = "使用者建立成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立使用者發生異常: {UserId}", request.UserId);
            return StatusCode(500, "系統發生內部異常，使用者建立失敗");
        }
    }

    /// <summary>
    /// 查詢單一使用者資料
    /// </summary>
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetUser(string userId)
    {
        var user = await _oracleDb.GetUserByIdFromRmdbAsync(userId);

        if (user is null)
        {
            _logger.LogWarning("查詢失敗，找不到使用者: {UserId}", userId);
            return NotFound($"找不到使用者 {userId}");
        }

        return Ok(new UserResponseDto
        {
            UserId = user.UserId,
            UserName = user.Username,
            DeptId = user.Department,
            IsActive = user.IsActive ? "Y" : "N",
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// 更新使用者基本資料 (包含停權設定)
    /// </summary>
    [HttpPut("{userId}")]
    public async Task<IActionResult> UpdateUser(string userId, [FromBody] UpdateUserDto request)
    {
        try
        {
            // 💡 [Outbox] UpdateUserProfileAsync 內部已將「UPDATE ARM_USERS」與
            //    「Outbox Insert (UserStatusChanged)」納入同一個 Transaction，
            //    Session 撤銷 / Cache Invalidation / SignalR 一律交給 OutboxWorker 背景處理。
            var ok = await _oracleDb.UpdateUserProfileAsync(
                userId, request.UserName, request.DeptId, request.IsActive,
                operatorUserId: User.Identity?.Name);

            if (!ok)
                return NotFound($"找不到使用者 {userId}");

            _logger.LogInformation("✅ 使用者資料更新成功並排入 Outbox: {UserId}, IsActive={IsActive}",
                userId, request.IsActive);

            var message = request.IsActive
                ? "使用者資料更新成功"
                : "使用者已更新為停權狀態，已排入 Outbox 佇列，稍後將由背景 Worker 強制作廢其全院在線 Session。";

            return Ok(new { Message = message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新使用者發生異常: {UserId}", userId);
            return StatusCode(500, "系統發生內部異常，使用者更新失敗");
        }
    }

    /// <summary>
    /// 刪除使用者 (軟刪除 Soft Delete)
    /// </summary>
    [HttpDelete("{userId}")]
    public async Task<IActionResult> DisableUser(string userId)
    {
        try
        {
            // 💡 [Outbox] DeactivateUserAsync 內部已將「UPDATE IS_ACTIVE='N'」與
            //    「Outbox Insert (UserStatusChanged)」納入同一個 Transaction。
            var ok = await _oracleDb.DeactivateUserAsync(userId, operatorUserId: User.Identity?.Name);

            if (!ok)
                return NotFound($"找不到使用者 {userId}");

            _logger.LogInformation("✅ 使用者已刪除 (軟刪除) 並排入 Outbox: {UserId}", userId);

            return Ok(new
            {
                Message = $"使用者 {userId} 已成功刪除，已排入 Outbox 佇列，稍後將由背景 Worker 強制作廢其全院連線與角色快取。"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除使用者發生異常: {UserId}", userId);
            return StatusCode(500, "系統發生內部異常，使用者刪除失敗");
        }
    }
}

