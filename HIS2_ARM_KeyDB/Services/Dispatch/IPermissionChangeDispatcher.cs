using HIS2_ARM_KeyDB.Models.Events.Interfaces;

namespace HIS2_ARM_KeyDB.Services.Dispatch;

public interface IPermissionChangeDispatcher
{
    Task DispatchAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default);
}