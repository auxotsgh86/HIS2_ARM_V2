namespace HIS2_ARM_KeyDB.Models.Auth;

/// <summary>
/// ARM 授權引擎回傳結果模型 (Server 端定義)
/// </summary>
public class PermissionVerifyResult
{
    public bool IsAllowed { get; set; }
    public string? UserId { get; set; }
    public string? ErrorMessage { get; set; }
}