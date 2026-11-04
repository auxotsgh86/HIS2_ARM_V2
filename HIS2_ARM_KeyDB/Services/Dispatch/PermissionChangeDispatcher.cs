using HIS2_ARM_KeyDB.Models.Events.Interfaces;
using HIS2_ARM_KeyDB.Services.Handlers.Interfaces;

namespace HIS2_ARM_KeyDB.Services.Dispatch;

public sealed class PermissionChangeDispatcher : IPermissionChangeDispatcher
{
    private readonly IEnumerable<IPermissionEventHandler> _handlers;
    private readonly ILogger<PermissionChangeDispatcher> _logger;

    public PermissionChangeDispatcher(IEnumerable<IPermissionEventHandler> handlers, ILogger<PermissionChangeDispatcher> logger)
    {
        _handlers = handlers;
        _logger = logger;
    }

    public async Task DispatchAsync(IPermissionChangedEvent evt, CancellationToken cancellationToken = default)
    {
        // 利用 linq 挑出可以處理該種事件的 handler，是這一套設計的核心，讓事件處理可以動態擴充
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(evt));

        if (handler is null)
        {
            _logger.LogError("找不到對應的 Handler，事件型別：{EventType}", evt.GetType().Name);
            throw new NotSupportedException($"未支援的權限異動事件型別：{evt.GetType().Name}");
        }

        await handler.HandleAsync(evt, cancellationToken);
    }
}