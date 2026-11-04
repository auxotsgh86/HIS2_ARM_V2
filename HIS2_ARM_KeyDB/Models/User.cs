namespace HIS2_ARM_KeyDB.Models;

public class User
{
    public string    UserId       { get; set; } = string.Empty;
    public string    Username     { get; set; } = string.Empty;
    public string    PasswordHash { get; set; } = string.Empty;
    public string    Department   { get; set; } = string.Empty;
    public bool      IsActive     { get; set; } = true;
    public DateTime  CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt  { get; set; }
    public string?   LastLoginIp  { get; set; }

    /// <summary>Navigation — ARM_USER_ROLES 多對多，Service 層填入</summary>
    public List<string> RoleIds { get; set; } = [];
}
