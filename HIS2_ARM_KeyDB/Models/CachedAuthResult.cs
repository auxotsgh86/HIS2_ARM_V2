namespace HIS2_ARM_KeyDB.Models;

/// <summary>
/// 權限驗證結果 (回傳給各個子系統)
/// </summary>
public class CachedAuthResult
{
    /// <summary>
    /// 是否允許放行
    /// </summary>
    public bool IsAllowed { get; set; }

    /// <summary>
    /// 拒絕原因 (若 IsAllowed 為 false 時提供)
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// 使用者代號 (放行時回傳，供子系統寫 Log 或資料綁定)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// 使用者名稱
    /// </summary>
    public string? UserName { get; set; }
}