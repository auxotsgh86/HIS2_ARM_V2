using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HIS2_ARM_KeyDB.Attributes;
using Microsoft.AspNetCore.Authorization;

namespace HIS2_ARM_KeyDB.Pages.Account
{
    // 💡 [架構特徵] 掛載 Bypass 屬性，確保在未登入狀態下，
    // ArmAuthorizationMiddleware 不會把載入登入頁面的請求也給擋下 (避免無限重導迴圈)。
    [AllowAnonymous]
    [BypassArmAuth]
    public class LoginModel : PageModel
    {
        // 綁定從 URL 傳入的 returnUrl 參數 (例如：/Account/Login?returnUrl=%2FAdmin%2FUsers)
        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public IActionResult OnGet()
        {
            // 若未來需要在後端檢查是否已持有有效 Cookie 並自動跳轉，可實作於此。
            // 目前採用 BFF 架構，我們將狀態判斷與跳轉邏輯交由前端狀態機統一處理。
            return Page();
        }
    }
}