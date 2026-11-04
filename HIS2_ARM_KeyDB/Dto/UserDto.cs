using System.Text.Json.Serialization;

namespace HIS2_ARM_KeyDB.Dto;

/// <summary>
/// 建立使用者請求 DTO (Command / Write Model)
/// 💡 僅開放建立帳號時允許設定的基礎欄位
/// </summary>
public class CreateUserDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("deptId")]
    public string DeptId { get; set; } = string.Empty;

    /// <summary>
    /// 💡 [補漏] 原本此 DTO 沒有密碼欄位，導致 ARM_USERS.PASSWORD_HASH (NOT NULL) 永遠無值可寫。
    /// 由管理端設定初始密碼，Controller 端負責 BCrypt Hash 後再寫入。
    /// </summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 更新使用者請求 DTO (Command / Write Model)
/// 💡 [安全防禦]：刻意不包含 UserId (主鍵不可改) 與密碼等敏感欄位，防止前端越權竄改 (Mass Assignment 防禦)
/// </summary>
public class UpdateUserDto
{
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("deptId")]
    public string DeptId { get; set; } = string.Empty;

    /// <summary>
    /// 前端傳入的停權狀態 (true = 啟用, false = 停權)
    /// 💡 Controller 會將此布林值轉換為資料庫的 'Y' 或 'N'，並決定是否觸發 Session 註銷
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// 使用者查詢回應 DTO (Query / Read Model)
/// 💡 [優雅設計]：隱藏密碼雜湊、登入 IP 等後端機密，僅暴露前端畫面真正需要的欄位
/// </summary>
public class UserResponseDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("deptId")]
    public string DeptId { get; set; } = string.Empty;

    /// <summary>
    /// 對應資料庫的 'Y' 或 'N'
    /// </summary>
    [JsonPropertyName("isActive")]
    public string IsActive { get; set; } = "Y";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}