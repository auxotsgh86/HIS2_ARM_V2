namespace HIS2_ARM_KeyDB_Client.Models.Auth;

internal class PermissionVerifyResult
{
    public bool IsAllowed { get; set; }
    public string? UserId { get; set; }
    public string? ErrorMessage { get; set; }
}