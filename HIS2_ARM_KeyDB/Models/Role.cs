namespace HIS2_ARM_KeyDB.Models;

public class Role
{
    public string RoleId      { get; set; } = string.Empty;
    public string RoleName    { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool   IsActive    { get; set; } = true;
}
