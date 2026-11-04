namespace HIS2_ARM_KeyDB.Models;

public class SessionInfo
{
    public string   Jti        { get; set; } = string.Empty;
    public string   UserId     { get; set; } = string.Empty;
    public string   ClientIp   { get; set; } = string.Empty;
    public string   UserAgent  { get; set; } = string.Empty;
    public DateTime IssuedAt   { get; set; }
    public DateTime ExpiresAt  { get; set; }
    public bool     IsRevoked  { get; set; } = false;
    public string   RefreshJti { get; set; } = string.Empty;
}

public class RefreshSession
{
    public string   RefreshJti { get; set; } = string.Empty;
    public string   AccessJti  { get; set; } = string.Empty;
    public string   UserId     { get; set; } = string.Empty;
    public DateTime IssuedAt   { get; set; }
    public DateTime ExpiresAt  { get; set; }
    public bool     IsRevoked  { get; set; } = false;
}

// ─── Request / Response DTOs ─────────────────────────────────────────────────
public class LoginRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class RefreshRequest
{
    // 💡 明確定義 RefreshToken 屬性，供 AuthController 讀取
    public string RefreshToken { get; set; } = string.Empty;
}

public class LogoutRequest
{
    public string AccessToken { get; set; } = string.Empty;
}

/// <summary>
/// 登入與憑證換發成功後的回傳模型
/// </summary>
public class AuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessExpiresAt { get; set; }
    public DateTime RefreshExpiresAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<string> RoleIds { get; set; } = new List<string>();

    public string LegacyToken { get; set; } =  string.Empty;
}



public record JwtValidationResponse(
    bool    IsValid,
    string? UserId,
    string? Message);

/// <summary>前端查詢「我對此 Action 能做哪些操作」</summary>
public record ActionPermResponse(
    string       Controller,
    string       ActionName,
    int          EffectiveBits,
    List<string> AllowedOps);

// ─── UserMgr DTOs ────────────────────────────────────────────────────────────

public record AddUserRequest(
    string       UserId,
    string       Username,
    string       Password,
    List<string> RoleIds,
    string       Department);

public record BatchAddUsersRequest(List<AddUserRequest> Users);

public record UpdUserRequest(
    string       Username,
    List<string> RoleIds,
    string       Department,
    bool         IsActive);

public record DeleteOneSessionRequest(string Jti);

/// <summary>
/// GetUserData 查詢條件：支援「Role → 目前成員」與一般使用者清單搜尋，並附分頁。
/// 💡 ExcludeRoleId：用於「新增成員」候選名單，排除已經是該角色成員的使用者。
/// </summary>
public record UserQueryRequest(
    string? UserId = null,
    string? RoleId = null,
    string? ExcludeRoleId = null,
    string? Department = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>通用分頁回應包裝</summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// 批次將「既有」使用者加入指定角色。
/// 💡 與 BatchAddUsersRequest 不同：操作對象是已存在的使用者，不需要密碼，
///    只會異動 ARM_USER_ROLES，不會重新 INSERT INTO ARM_USERS。
/// </summary>
public record AssignUsersToRoleRequest(string RoleId, List<string> UserIds);

// ─── Role DTOs ───────────────────────────────────────────────────────────────

/// <summary>
/// GetRoles 查詢條件：支援「角色權限」頁左側的角色搜尋 + 分頁。
/// 💡 IncludeInactive：維運盤查用，預設 false 只顯示有效角色。
/// </summary>
public record RoleQueryRequest(
    string? Keyword = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 20);

