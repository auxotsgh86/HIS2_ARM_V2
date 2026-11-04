using System.Security.Claims;
using HIS2_ARM_KeyDB.Attributes;
using HIS2_ARM_KeyDB.Services;
using HIS2_ARM_KeyDB.Services.Auth;
using Microsoft.AspNetCore.Authorization;

namespace HIS2_ARM_KeyDB.Middlewares;

/// <summary>
/// 【ARM 中心自我防護中介軟體】
/// 💡 與下游子系統的 ArmAuthorizationMiddleware 邏輯同源，但採「進程內直呼」：
/// 直接呼叫 AuthService.CheckPermissionAsync，不繞 HTTP loopback。
/// 同時支援 MVC Controller 與 Razor Pages 兩種路由。
/// </summary>
public class SelfArmAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SelfArmAuthorizationMiddleware> _logger;
    private readonly string _selfSystemId;

    private const string ACCESS_COOKIE_NAME = "ARM_ACCESS_TOKEN";

    public SelfArmAuthorizationMiddleware(
        RequestDelegate next,
        IConfiguration config,
        ILogger<SelfArmAuthorizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        // 💡 沿用你剛整理好的設定鍵：Arm:DefaultSystemId = HIS2_ARM_V2
        _selfSystemId = SystemIdResolver.Normalize(config["Arm:DefaultSystemId"] ?? "HIS2_ARM_V2");
    }

    // 💡 AuthService 是 Scoped，必須從 InvokeAsync 參數注入（不可放建構子）
    public async Task InvokeAsync(HttpContext context, AuthService authService)
    {
        var endpoint = context.GetEndpoint();

        // 尚未命中任何端點 (如 Swagger UI 中介軟體、404)：放行交給後續管線
        if (endpoint == null)
        {
            await _next(context);
            return;
        }

        // 免死金牌：[BypassArmAuth] 或 [AllowAnonymous] 一律放行
        bool isBypass = endpoint.Metadata.GetMetadata<BypassArmAuthAttribute>() != null ||
                        endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null;
        if (isBypass)
        {
            await _next(context);
            return;
        }

        // ── 解析目標：同時支援 Controller 與 Razor Pages ──
        // MVC:    controller=Admin, action=UpdateRolePermission
        // Pages:  page=/Admin/Users → controller="Pages.Admin.Users", action="View"
        string? controller = context.Request.RouteValues["controller"]?.ToString();
        string? action = context.Request.RouteValues["action"]?.ToString();

        if (controller == null)
        {
            var page = context.Request.RouteValues["page"]?.ToString();
            if (page != null)
            {
                controller = "Pages" + page.Replace('/', '.'); // e.g. "Pages.Admin.Users"
                action = "View";
            }
        }

        if (string.IsNullOrEmpty(controller) || string.IsNullOrEmpty(action))
        {
            await HandleRejectAsync(context, StatusCodes.Status403Forbidden, "無效的路由目標");
            return;
        }

        // ── 取憑證：HttpOnly Cookie 優先，Bearer Header 備援 ──
        string? token = context.Request.Cookies[ACCESS_COOKIE_NAME];
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            await HandleRejectAsync(context, StatusCodes.Status401Unauthorized, "未登入或憑證已失效");
            return;
        }

        // ── 🚀 核心差異：進程內直呼權限引擎，不打 HTTP ──
        var result = await authService.CheckPermissionAsync(token, _selfSystemId, controller, action);

        if (!result.IsAllowed)
        {
            _logger.LogWarning("【自我防護】拒絕存取: System={Sys}, Target={Ctrl}/{Act}, User={UserId}, 原因={Reason}",
                _selfSystemId, controller, action, result.UserId ?? "(未識別)", result.ErrorMessage);

            // Token 無效 → 401；有身分但位元不足 → 403
            int status = string.IsNullOrEmpty(result.UserId)
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;

            await HandleRejectAsync(context, status, result.ErrorMessage ?? "權限不足，拒絕操作");
            return;
        }

        // 驗證通過：掛載標準 ClaimsPrincipal，讓 [Authorize] / User.Identity 原生運作
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, result.UserId ?? ""),
            new Claim(ClaimTypes.Name, result.UserId ?? "")
        }, "ArmSelfAuth");
        context.User = new ClaimsPrincipal(identity);
        context.Items["UserId"] = result.UserId;

        await _next(context);
    }

    /// <summary>智慧回應分流：瀏覽器換頁給 302 導登入頁；AJAX/API 給 JSON</summary>
    private static async Task HandleRejectAsync(HttpContext context, int statusCode, string message)
    {
        var request = context.Request;

        bool isAjax = request.Headers.XRequestedWith == "XMLHttpRequest" ||
                      request.Headers["Sec-Fetch-Mode"] == "cors";
        bool acceptsHtml = request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);

        // PathBase 已由 UsePathBase("/ArmV2") 掛載，這裡組相對自身的登入路徑
        var returnUrl = Uri.EscapeDataString(request.PathBase + request.Path + request.QueryString);
        string loginUrl = $"{request.PathBase}/Account/Login?returnUrl={returnUrl}";

        bool isForbidden = statusCode == StatusCodes.Status403Forbidden;

        if (!isAjax && acceptsHtml)
        {
            if (isForbidden)
            {
                // 有身分但無權限：不導登入頁（避免無意義的重新登入迴圈），顯示簡單提示
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    $"""
                     <html><body style="font-family:sans-serif;text-align:center;padding-top:10vh">
                     <h3>🚫 權限不足</h3><p>{System.Net.WebUtility.HtmlEncode(message)}</p>
                     <a href="{request.PathBase}/">回首頁</a>
                     </body></html>
                     """);
            }
            else
            {
                context.Response.Redirect(loginUrl);
            }
        }
        else
        {
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(new
            {
                Status = statusCode,
                Detail = message,
                RedirectUrl = isForbidden ? null : loginUrl
            });
        }
    }
}
