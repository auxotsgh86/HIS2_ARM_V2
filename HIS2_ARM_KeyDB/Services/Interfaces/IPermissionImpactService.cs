using HIS2_ARM_KeyDB.Models.Events;

namespace HIS2_ARM_KeyDB.Services.Interfaces;

/// <summary>
/// 計算權限異動所影響的 User 範圍。
/// </summary>
public interface IPermissionImpactService
{
    /// <summary>
    /// 取得 UserRole 異動所影響的 User。
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        UserRoleChangedEvent evt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得 Role 權限異動所影響的 User。
    /// Role → User
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        RolePermissionChangedEvent evt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得 Action 需求權限異動所影響的 User。
    /// Action → Role → User
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        ActionRequirementChangedEvent evt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得 Role 狀態異動（IS_ACTIVE 停用/啟用）所影響的 User。
    /// Role → User
    /// 💡 Impact Query 與 Effective Permission Query 目的不同：即使 Role 剛被停用，
    ///    此查詢仍需找出「原本」屬於該 Role 的 User，才能讓他們的授權快取正確失效。
    /// </summary>
    Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        RoleStatusChangedEvent evt,
        CancellationToken cancellationToken = default);
}