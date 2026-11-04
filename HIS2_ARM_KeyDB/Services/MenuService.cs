using System.Security.Claims;
using Dapper;
using HIS2_ARM_KeyDB_Client.Models.Menu;
using HIS2_ARM_KeyDB.Models.Auth;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Oracle.ManagedDataAccess.Client;

namespace HIS2_ARM_KeyDB.Services;

public class MenuService : IMenuService
{
    private readonly string _connectionString;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public MenuService(IConfiguration config, IHttpContextAccessor httpContextAccessor)
    {
        _connectionString = config.GetConnectionString("Oracle") ?? string.Empty;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<List<MenuViewModel>> GetUserMenuTreeAsync(string userId)
    {
        // ✅ 透過 IHttpContextAccessor 取得 Claims
        var user = _httpContextAccessor.HttpContext?.User;

        var roleIds = user?.Claims
            .Where(c => c.Type == ClaimTypes.Role) // 依實際 ClaimType 調整
            .Select(c => c.Value)
            .ToList() ?? new List<string>();

        if (!roleIds.Any())
            return new List<MenuViewModel>();

        string sql = """
                     SELECT
                         -- ==========================================
                         -- 系統層級對應 (System Level)
                         -- ==========================================
                         a.SORT_VALUE       AS menu_sys_sort_value,
                         b.SORT_VALUE       AS function_sort_value,
                         a.SYSTEM_ID,
                         c.SYSTEM_NAME,
                     
                         -- ==========================================
                         -- 選單層級對應 (Menu Level)
                         -- ==========================================
                         a.MENU_ID,
                         a.MENU_NAME,
                         NULL               AS MENU_ICON,   -- 💡 全場唯一必須保留 AS 的地方：為 NULL 賦予欄位名稱
                     
                         -- ==========================================
                         -- 功能與路由對應 (Function & Route Level)
                         -- ==========================================
                         b.CONTROLLER_NAME,
                         b.FUNCTION_NAME,
                         b.ACTION_NAME,
                     
                         -- ==========================================
                         -- 行為與結構控制對應 (UI Behavior & Hierarchy)
                         -- ==========================================
                         b.IS_SHOW,
                         b.CAN_OINW,
                         b.FUNCTION_LEVEL,
                         b.PARENT_CODE
                     
                     FROM HIS2USER2.ARMMENU a
                     INNER JOIN HIS2USER2.ARMFUNCTION b
                         ON a.MENU_ID   = b.MENU_ID
                        AND a.SYSTEM_ID = b.SYSTEM_ID
                     INNER JOIN HIS2USER2.ARM_SYSTEM c
                         ON a.SYSTEM_ID = c.SYSTEM_ID
                     
                     WHERE 1 = 1
                       AND a.IS_DC     = 'N'
                       AND b.IS_DC     = 'N'
                       -- 可以顯示的功能才列入選單，避免前端渲染時出現「無法存取」的按鈕 
                       AND b.IS_SHOW   = 'Y' 
                       AND c.IS_DC     = 'N'
                       AND c.IS_ACTIVE = 'Y'
                     
                       -- ==========================================
                       -- 權限檢核 (Authorization Check)
                       -- 邏輯：只要該 Controller 轄下有任何一筆 (Action 或全域)
                       -- 具備檢視權限 (Bit 2)，即判定授權通過，顯示選單。
                       -- 使用 EXISTS 避免一對多授權造成的資料膨脹與 DISTINCT 開銷。
                       -- ==========================================
                       AND EXISTS (
                           SELECT 1
                           FROM HIS2USER2.ARM_PERMISSIONS arp
                           WHERE arp.SYSTEM_ID  = b.SYSTEM_ID
                             -- 僅綁定 Controller，不綁定 Action，滿足「轄下任一有權限即開放 Controller」之業務需求
                             AND arp.CONTROLLER = CASE
                                                       WHEN b.CONTROLLER_NAME = 'MRAM030'
                                                       THEN UPPER(SUBSTR(b.CONTROLLER_NAME, 1, 1)) || LOWER(SUBSTR(b.CONTROLLER_NAME, 2))
                                                       ELSE b.CONTROLLER_NAME
                                                   END
                             AND arp.ROLE_ID IN :RoleIds
                             AND BITAND(arp.PERMS_BITS, 2) > 0
                             -- 嚴格過濾已停用或註銷之權限，避免幽靈權限放行
                             AND arp.IS_DC     = 'N'
                             AND arp.IS_ACTIVE = 'Y'
                       )
                     
                     ORDER BY
                         a.SYSTEM_ID,
                         menu_sys_sort_value,
                         function_sort_value
                     """;

        using var connection = new OracleConnection(_connectionString);

        var flatMenus = await connection.QueryAsync<MenuQueryResult>(
            sql,
            new { RoleIds = roleIds }
        );

        return BuildTree(flatMenus);
    }

    /// <summary>
    /// 將扁平的資料庫權限結果，轉換為前端 UI 可直接遞迴渲染的樹狀結構 (組合模式)
    /// </summary>
    /// <param name="flatMenus">從資料庫撈出且帶有權限過濾的扁平清單</param>
    /// <returns>樹狀選單 ViewModel</returns>
    private List<MenuViewModel> BuildTree(IEnumerable<MenuQueryResult> flatMenus)
    {
        return flatMenus
            // =================================================================
            // 1. 第一層：以【系統】(System) 進行分群
            // =================================================================
            .GroupBy(s => new { s.SYSTEM_ID, s.SYSTEM_NAME, s.SYSTEM_SORT })
            .OrderBy(sysGroup => sysGroup.Key.SYSTEM_SORT)
            .Select(sysGroup => new MenuViewModel
            {
                ID = sysGroup.Key.SYSTEM_ID,
                NAME = sysGroup.Key.SYSTEM_NAME,
                // 系統層級通常無圖示欄位，直接套用首字防呆機制
                ICON = string.IsNullOrWhiteSpace(sysGroup.Key.SYSTEM_NAME) ? "" : sysGroup.Key.SYSTEM_NAME[..1],
                ACTION_NAME = "",
                LEVEL = 1,
                ACTIVE = false,
                IS_SHOW = "Y", // 系統層級預設顯示
                CAN_OINW = "N", // 系統層級預設不可另開視窗

                // =================================================================
                // 2. 第二層：以【選單】(Menu) 進行分群，並掛載至第一層的子清單
                // =================================================================
                FunctionList = sysGroup
                    .GroupBy(m => new { m.MENU_ID, m.MENU_NAME, m.MENU_SORT, m.MENU_ICON })
                    .OrderBy(menuGroup => menuGroup.Key.MENU_SORT)
                    .Select(menuGroup => new MenuViewModel
                    {
                        ID = menuGroup.Key.MENU_ID,
                        NAME = menuGroup.Key.MENU_NAME,
                        // 選單圖示 Fallback 邏輯：有值用值，無值取名稱首字，若連名稱都無則給空字串
                        ICON = string.IsNullOrWhiteSpace(menuGroup.Key.MENU_ICON)
                            ? (string.IsNullOrWhiteSpace(menuGroup.Key.MENU_NAME) ? "" : menuGroup.Key.MENU_NAME[..1])
                            : menuGroup.Key.MENU_ICON,
                        ACTION_NAME = "",
                        LEVEL = 2,
                        ACTIVE = false,
                        IS_SHOW = "Y",
                        CAN_OINW = "N",

                        // =================================================================
                        // 3. 第三層：具體【功能】(Function) 與路由，掛載至第二層的子清單
                        // =================================================================
                        FunctionList = menuGroup
                            .OrderBy(f => f.FUNCTION_SORT)
                            .Select(f => new MenuViewModel
                            {
                                ID = f.CONTROLLER_NAME,
                                NAME = f.FUNCTION_NAME,
                                ACTION_NAME = f.ACTION_NAME,
                                ICON = "", // 樹狀結構的末端節點通常不需要圖示
                                LEVEL = 3,
                                ACTIVE = false,
                                // 直接繼承資料庫的行為設定
                                IS_SHOW = f.IS_SHOW,
                                CAN_OINW = f.CAN_OINW,

                                // 確保末端節點的子清單為空實體，避免前端渲染時發生 null reference
                                FunctionList = new List<MenuViewModel>()
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }
}