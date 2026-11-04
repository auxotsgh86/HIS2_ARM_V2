using HIS2_ARM_KeyDB_Client.Models.Menu;

namespace HIS2_ARM_KeyDB_Client.Services.Interfaces;

public interface IMenuCacheService
{
    Task<List<MenuViewModel>> GetMenuAsync(
        string userId,
        string cookieHeader,
        CancellationToken cancellationToken = default);

    void Invalidate(string userId);
}
