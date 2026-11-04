using HIS2_ARM_KeyDB.Models;

namespace HIS2_ARM_KeyDB.Services.Interfaces;

public interface IUserRepository
{
    /// <summary>
    /// 驗證帳密，若成功則回傳包含真實姓名 (Username) 的 User 物件
    /// </summary>
    Task<User?> ValidateUserAsync(string userId, string password);

    /// <summary>
    /// 取得該使用者的角色清單 (例如 NURSE, DOCTOR)
    /// </summary>
    Task<List<string>> GetUserRolesAsync(string userId);
}