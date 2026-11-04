using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HIS2_ARM_KeyDB.Services;

/// <summary>
/// 動態子系統解析中間件 (Middleware)
/// 從 Request path 第一段解析 HIS2 子系統代號 (例如：/HIS2_INP/Login/Login => HIS2_INP)。
/// 若沒有系統前綴，則回傳預設 ARM 系統代號，並將結果存入 HttpContext.Items 供後續授權攔截器使用。
/// </summary>
public class SystemIdResolver
{
    private readonly RequestDelegate _next; // Middleware 必備參數
    private static readonly Regex SystemIdPattern = new("^[A-Z0-9_]{2,20}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly string _defaultSystemId;
    private readonly HashSet<string> _nonSystemPrefixes;

    // 將 IConfiguration 與 RequestDelegate 一起由 DI 注入
    public SystemIdResolver(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _defaultSystemId = Normalize(config["Arm:DefaultSystemId"] ?? "HIS2_ARM_V2");
        _nonSystemPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "User", "UserMgr", "PermMgr", "Bootstrap", "health", "swagger", "admin-ui", "Admin", "lib"
        };

        var extraPrefixes = config.GetSection("Arm:NonSystemPathPrefixes").Get<List<string>>();
        if (extraPrefixes != null)
        {
            foreach (var prefix in extraPrefixes)
            {
                _nonSystemPrefixes.Add(prefix);
            }
        }
    }

    /// <summary>
    /// Middleware 核心執行方法
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // 執行 URL 解析邏輯
        string resolvedSystemId = ResolveFromPath(context);

        // ★ 將解析結果掛載到生命週期中，供 AuthorizeActionFilter 取用
        context.Items["SystemId"] = resolvedSystemId;

        // 呼叫下一個 Middleware
        await _next(context);
    }

    /// <summary>
    /// 核心的 URL 解析邏輯 (抽離為獨立方法以利維護)
    /// </summary>
    private string ResolveFromPath(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var firstSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        
        if (string.IsNullOrWhiteSpace(firstSegment)) return _defaultSystemId;
        if (_nonSystemPrefixes.Contains(firstSegment)) return _defaultSystemId;
        if (!SystemIdPattern.IsMatch(firstSegment)) return _defaultSystemId;

        return Normalize(firstSegment);
    }

    public static string Normalize(string systemId) => systemId.Trim().ToUpperInvariant();
}