using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using System.Linq;
using HIS2_ARM_KeyDB.Attributes;
using HIS2_ARM_KeyDB.Services;
using HIS2_ARM_KeyDB.Services.Auth; // 💡 請替換為您專案中 AuthService 實際所在的命名空間

namespace HIS2_ARM_KeyDB.Pages.Account
{
    /// <summary>
    /// 【全域登出處理中心】
    /// 適用於 Razor Pages 架構，專職接收跨子系統（如 MRA）傳遞過來的 302 登出跳轉請求。
    /// 清除全域 HttpOnly Cookie 憑證並註銷 Redis/KeyDB 狀態後，將使用者重導向回指定來源系統。
    /// </summary>
    [AllowAnonymous]
    [BypassArmAuth] // 💡 強固設計：登出流程不需進行身分攔截，允許匿名存取以利順暢清除憑證
    [IgnoreAntiforgeryToken] // 💡 強固設計：允許外部子系統透過 GET 直接觸發登出跳轉，免除 CSRF 權限阻擋
    public class LogoutModel : PageModel
    {
        private readonly AuthService _authService;

        // 💡 維持高度維護性：與 AuthController 保持絕對一致的 Cookie 名稱定義
        private const string ACCESS_COOKIE_NAME = "ARM_ACCESS_TOKEN";
        private const string REFRESH_COOKIE_NAME = "ARM_REFRESH_TOKEN";

        public LogoutModel(AuthService authService)
        {
            _authService = authService;
        }

        /// <summary>
        /// 處理由瀏覽器 302 跳轉帶入的 GET 登出請求
        /// 路由位置：/Account/Logout
        /// </summary>
        /// <param name="returnUrl">登出完成後，期望讓瀏覽器返回的子系統根路徑（例如：/HIS2_MRA/）</param>
        /// <returns></returns>
        public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
        {
            // 1. 嘗試從 HttpOnly Cookie 中提取 Access Token，並向後端驗證中心 (KeyDB/Redis) 註銷權限
            if (Request.Cookies.TryGetValue(ACCESS_COOKIE_NAME, out string? accessToken) && !string.IsNullOrEmpty(accessToken))
            {
                // 呼叫底層核心服務，將該 Token 標記為失效，防止該憑證在過期前被惡意重複使用
                await _authService.LogoutAsync(accessToken);
            }

            // 2. 強制抹除瀏覽器端持有的所有全域憑證 Cookie
            // 💡 關鍵強固點：必須明確指定 Path = "/"，才能完整覆蓋並清除全域網域根目錄下的 Cookie
            var cookieOptions = new CookieOptions { Path = "/" };
            Response.Cookies.Delete(ACCESS_COOKIE_NAME, cookieOptions);
            Response.Cookies.Delete(REFRESH_COOKIE_NAME, cookieOptions);

            // 3. 安全重導向邏輯
            // 💡 優雅設計：在 Caddy 反向代理架構下，MRA 與 ARM 皆在同一網域 (localhost) 下運行。
            // 傳入的 "/HIS2_MRA/" 會被 .NET 視為合法的 LocalUrl，因此能完美支援跨子系統的閉環跳轉。
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                // 若無指定返回路徑，或傳入惡意外部網址，則安全地導回中央登入頁面
                return RedirectToPage("/Account/Login");
            }
        }
    }
}