using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Services.Interfaces;

namespace HIS2_ARM_KeyDB.Services.PermissionImpact;

public sealed class PermissionImpactService
    : IPermissionImpactService
{
    private readonly OracleDbService _oracleDb;

    public PermissionImpactService(
        OracleDbService oracleDb)
    {
        _oracleDb = oracleDb;
    }

    public Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        UserRoleChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<string>>(
            evt.UserIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public async Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        RolePermissionChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        var userIds = await _oracleDb.GetUserIdsByRolesAsync(
            evt.RoleIds,
            cancellationToken);

        return userIds.ToArray();
    }

    public async Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        ActionRequirementChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        var userIds = await _oracleDb.GetUserIdsByActionsAsync(
            evt.Actions,
            cancellationToken);

        return userIds.ToArray();
    }

    public async Task<IReadOnlyCollection<string>> GetAffectedUserIdsAsync(
        RoleStatusChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        // 💡 與 RolePermissionChangedEvent 共用同一支 Role → User Impact Query，
        //    因為兩者的「找出誰需要被通知」語意完全一致，差異只在於 Handler 端要不要 Reload role:perm。
        var userIds = await _oracleDb.GetUserIdsByRolesAsync(
            evt.RoleIds,
            cancellationToken);

        return userIds.ToArray();
    }
}