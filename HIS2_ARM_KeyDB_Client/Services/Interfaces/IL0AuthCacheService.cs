using HIS2_ARM_KeyDB_Client.Enums;

namespace HIS2_ARM_KeyDB_Client.Services.Interfaces;

/// <summary>
/// L0 授權快取服務。
///
/// 負責管理本機 Process 內的 Authorization Cache，
/// 包含 Positive / Negative Cache、TTL 以及 Cache Invalidation。
/// </summary>
public interface IL0AuthCacheService
{
    /// <summary>
    /// 嘗試從 Cache 取得指定 User 對指定 Action 的權限。
    ///
    /// 回傳 true 代表 Cache Hit。
    /// 此時 permissions 可能是 None，代表「已查詢過，但沒有任何權限」。
    ///
    /// 回傳 false 代表 Cache Miss，需要向 ARM API 查詢。
    /// </summary>
    bool TryGet(
        string userId,
        string system,
        string controller,
        string action,
        out CrudpPermissions permissions);

    /// <summary>
    /// 寫入授權結果。
    ///
    /// permissions != None → Positive Cache
    /// permissions == None → Negative Cache
    /// </summary>
    void Set(
        string userId,
        string system,
        string controller,
        string action,
        CrudpPermissions permissions);

    /// <summary>
    /// 移除指定 User 的所有 Authorization Cache。
    /// </summary>
    void Invalidate(string userId);

    /// <summary>
    /// 移除指定 User、System、Controller、Action 的 Authorization Cache。
    /// </summary>
    void Invalidate(
        string userId,
        string system,
        string controller,
        string action);

    /// <summary>
    /// 移除所有 Authorization Cache。
    /// </summary>
    void InvalidateAll();
}