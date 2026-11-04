using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;
using Microsoft.Extensions.Logging;

namespace HIS2_ARM_KeyDB.Pages.Admin;

/// <summary>
/// 人員管理列表頁面邏輯 (PageModel)
/// 負責處理 /Admin/Users 的 GET 請求，並提供資料給 Razor 視圖渲染。
/// </summary>
public class UsersModel : PageModel
{
    private readonly OracleDbService _oracleService;
    private readonly ILogger<UsersModel> _logger;

    public UsersModel(OracleDbService oracleService, ILogger<UsersModel> logger)
    {
        _oracleService = oracleService;
        _logger = logger;
    }

    /// <summary>
    /// 綁定前端的搜尋條件。
    /// [BindProperty(SupportsGet = true)] 允許將 URL Query String (例如 ?SearchUserId=123) 
    /// 自動對應到這個屬性，並將值保留在畫面上的 <input> 標籤中。
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? SearchUserId { get; set; }

    /// <summary>
    /// 提供給前端 Razor 視圖 (Model.Users) 渲染用的強型別資料集。
    /// 預設初始化為空集合，避免 null 參考例外。
    /// </summary>
    public List<User> Users { get; set; } = new();

    /// <summary>
    /// 頁面載入時觸發的生命週期事件 (相當於 MVC 的 [HttpGet] Action)。
    /// 負責執行資料庫查詢並更新 Model 狀態。
    /// </summary>
    public async Task OnGetAsync()
    {
        try
        {
            // 將搜尋條件 (SearchUserId) 交由 OracleDbService 執行查詢。
            // 第二個參數為 roleId，在列表頁中我們不需要依角色過濾，因此傳入 null。
            // 
            // 💡 架構備註：OracleDbService.GetUsersAsync 內部應該實作類似以下的邏輯：
            // var sql = "SELECT * FROM ARM_USERS WHERE 1=1";
            // if (!string.IsNullOrEmpty(SearchUserId)) sql += " AND USER_ID LIKE :SearchUserId || '%'";
            
            var (items, _) = await _oracleService.GetUsersAsync(
                SearchUserId, null, null, null, null, 1, 500);

            // 將 IEnumerable 轉換為 List 並指派給屬性，供前端遍歷渲染。
            Users = items.ToList();
            
            _logger.LogInformation(
                "載入人員清單完成。條件: {SearchUserId}, 筆數: {Count}", 
                SearchUserId ?? "無", 
                Users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入人員清單 (UsersModel.OnGetAsync) 時發生未預期錯誤");
            
            // 利用 TempData 將錯誤訊息傳遞給前端，由 Users.cshtml 頂部的 Alert 區塊顯示。
            TempData["ErrorMessage"] = "系統發生錯誤，無法載入人員資料。請稍後再試或聯繫系統管理員。";
            
            // 確保發生錯誤時，Users 依然是一個空集合，避免前端 @foreach 發生 NullReferenceException。
            Users = new List<User>(); 
        }
    }
}