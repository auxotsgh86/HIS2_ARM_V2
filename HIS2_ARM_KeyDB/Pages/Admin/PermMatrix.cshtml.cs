using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HIS2_ARM_KeyDB.Pages.Admin;

/// <summary>
/// ARM 管理工作台 — 角色權限矩陣（前端 SPA 風格，資料全由 API 驅動）。
/// 頁面本身不載入業務資料；僅提供需登入的殼。
/// </summary>
[Authorize]
public class PermMatrixModel : PageModel
{
    public void OnGet()
    {
    }
}