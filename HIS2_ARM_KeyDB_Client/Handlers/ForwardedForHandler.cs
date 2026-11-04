using Microsoft.AspNetCore.Http;

namespace HIS2_ARM_KeyDB_Client.Handlers;

/// <summary>
/// 💡 統一攔截所有透過 "ArmClient" 發出的請求，
/// 自動把「目前這次使用者請求」解析出來的真實瀏覽器 IP 轉發給 ArmV2，
/// 解決 MRA → ArmV2 這種 Server-to-Server 呼叫時，ArmV2 只看到 MRA 伺服器 IP 的問題。
/// </summary>
public class ForwardedForHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ForwardedForHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 因為 Program.cs 已經呼叫 app.UseForwardedHeaders()，
        // 這裡的 RemoteIpAddress 已經是被 Caddy/F5 鏈路解析過的「真實瀏覽器 IP」
        var clientIp = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        if (!string.IsNullOrWhiteSpace(clientIp) &&
            !request.Headers.Contains("X-Forwarded-For"))
        {
            request.Headers.Add("X-Forwarded-For", clientIp);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}