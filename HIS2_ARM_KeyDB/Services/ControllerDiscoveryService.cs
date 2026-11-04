using HIS2_ARM_KeyDB.Models;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace HIS2_ARM_KeyDB.Services;

/// <summary>
/// Controller / Action 自動探索服務
///
/// 使用 ASP.NET Core 內建的 IActionDescriptorCollectionProvider，
/// 比原始 reflection 更可靠：
///   - 自動包含外部 Assembly 的 Controller（只要有掛到同一個 WebApplication）
///   - 只回傳真正可路由的 Action，不包含 non-public / inherited / helper method
///   - 排除 ARM 自身的管理 Controller（可透過設定過濾）
///
/// 使用情境：
///   1. 管理介面初始載入 → GET /PermMgr/GetControllerActions
///   2. 新系統上線後自動同步可用的 Controller/Action 清單
/// </summary>
public class ControllerDiscoveryService
{
    private readonly IActionDescriptorCollectionProvider _provider;
    private readonly IConfiguration                      _config;
    private readonly ILogger<ControllerDiscoveryService> _logger;

    /// <summary>掃描時排除這些 Controller（ARM 本身的管理端點）</summary>
    private static readonly HashSet<string> ExcludeControllers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "User", "UserMgr", "PermMgr"
        };

    public ControllerDiscoveryService(
        IActionDescriptorCollectionProvider provider,
        IConfiguration config,
        ILogger<ControllerDiscoveryService> logger)
    {
        _provider = provider;
        _config   = config;
        _logger   = logger;
    }

    /// <summary>
    /// 掃描所有已掛載的 Controller 及其 Action，回傳結構化清單
    ///
    /// 可透過 appsettings.json DiscoveryOptions:ExcludeControllers 新增排除名單
    /// </summary>
    public DiscoveryResponse Discover()
    {
        // 從設定檔讀取額外排除名單
        var configExcludes = _config
            .GetSection("DiscoveryOptions:ExcludeControllers")
            .Get<List<string>>() ?? [];

        var excludeSet = new HashSet<string>(
            ExcludeControllers.Concat(configExcludes),
            StringComparer.OrdinalIgnoreCase);

        // IActionDescriptorCollectionProvider 提供所有已註冊路由的描述
        var descriptors = _provider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(d => !excludeSet.Contains(d.ControllerName))
            .ToList();

        // 依 Controller 分組
        var grouped = descriptors
            .GroupBy(d => new
            {
                d.ControllerName,
                FullTypeName  = d.ControllerTypeInfo.FullName ?? d.ControllerName,
                AssemblyName  = d.ControllerTypeInfo.Assembly.GetName().Name ?? ""
            })
            .Select(g => new ControllerActionInfo
            {
                ControllerName = g.Key.ControllerName,
                FullTypeName   = g.Key.FullTypeName,
                AssemblyName   = g.Key.AssemblyName,
                // ActionName 去重（同一 Action 可能有多個 HttpMethod 路由）
                Actions = g.Select(d => d.ActionName)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(a => a)
                           .ToList()
            })
            .OrderBy(c => c.ControllerName)
            .ToList();

        var totalActions = grouped.Sum(c => c.Actions.Count);

        _logger.LogInformation(
            "Controller discovery: {Controllers} controllers, {Actions} actions",
            grouped.Count, totalActions);

        return new DiscoveryResponse(grouped.Count, totalActions, grouped);
    }

    /// <summary>
    /// 取得指定 Controller 的所有 Action 名稱
    /// （供 PermMgr 儲存前驗證用）
    /// </summary>
    public List<string> GetActions(string controllerName)
    {
        return _provider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(d => d.ControllerName.Equals(controllerName, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ActionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();
    }

    /// <summary>Controller 是否存在於已掛載路由中</summary>
    public bool ControllerExists(string controllerName) =>
        _provider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Any(d => d.ControllerName.Equals(controllerName, StringComparison.OrdinalIgnoreCase));
}
