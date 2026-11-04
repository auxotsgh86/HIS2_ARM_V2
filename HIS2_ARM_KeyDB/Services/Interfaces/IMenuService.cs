using HIS2_ARM_KeyDB_Client.Models.Menu;

namespace HIS2_ARM_KeyDB.Services.Interfaces;

public interface IMenuService
{
    /// <summary>
    /// 取得使用者專屬的樹狀權限選單
    /// </summary>
    /// <param name="userId">使用者識別碼</param>
    Task<List<MenuViewModel>> GetUserMenuTreeAsync(string userId);
}