using HIS2_ARM_KeyDB_Client.Enums;
using HIS2_ARM_KeyDB.Models.Auth.Enum;

namespace HIS2_ARM_KeyDB.Models.Auth;

/// <summary>
    /// 扁平的權限與選單查詢結果載荷
    /// 💡 採用全大寫與蛇形命名法 (Snake Case)，與 Oracle SQL 欄位 100% 絕對對齊，
    /// 無須額外設定 Dapper，即可達成光速自動映射。
    /// </summary>
    public class MenuQueryResult
    {
        // ==========================================
        // 系統層級 (System Level)
        // ==========================================
        /// <summary>
        /// 系統代碼 (對應 s.SYSTEM_ID)
        /// </summary>
        public string SYSTEM_ID { get; set; } = string.Empty;
        
        /// <summary>
        /// 系統名稱 (對應 s.SYSTEM_NAME)
        /// </summary>
        public string SYSTEM_NAME { get; set; } = string.Empty;
        
        /// <summary>
        /// 系統排序權重 (對應 s.SORT_VALUE AS SYSTEM_SORT)
        /// </summary>
        public int SYSTEM_SORT { get; set; } 

        // ==========================================
        // 選單層級 (Menu Level)
        // ==========================================
        /// <summary>
        /// 選單代碼 (對應 m.MENU_ID)
        /// </summary>
        public string MENU_ID { get; set; } = string.Empty;
        
        /// <summary>
        /// 選單名稱 (對應 m.MENU_NAME)
        /// </summary>
        public string MENU_NAME { get; set; } = string.Empty;
        
        /// <summary>
        /// 選單圖示 (對應 NULL AS MENU_ICON)
        /// 💡 設為 nullable (string?)，精準接住 SQL 傳來的 NULL 值，
        /// 讓後續 MenuUtility 能正確觸發 menuName[..1] 的首字防呆邏輯
        /// </summary>
        public string? MENU_ICON { get; set; }
        
        /// <summary>
        /// 選單排序權重 (對應 m.SORT_VALUE AS MENU_SORT)
        /// </summary>
        public int MENU_SORT { get; set; }

        // ==========================================
        // 功能與路由對應 (Function & Route Level)
        // ==========================================
        /// <summary>
        /// 控制器名稱 (對應 f.CONTROLLER_NAME)
        /// </summary>
        public string CONTROLLER_NAME { get; set; } = string.Empty;
        
        /// <summary>
        /// 功能名稱 (對應 f.FUNCTION_NAME)
        /// </summary>
        public string FUNCTION_NAME { get; set; } = string.Empty;
        
        /// <summary>
        /// 動作名稱 (對應 f.ACTION_NAME)
        /// </summary>
        public string ACTION_NAME { get; set; } = string.Empty;
        
        /// <summary>
        /// 路由路徑 (對應 f.ROUTE_PATH)
        /// </summary>
        public string ROUTE_PATH { get; set; } = string.Empty;
        
        /// <summary>
        /// 功能排序權重 (對應 f.FUNCTION_SORT)
        /// </summary>
        public int FUNCTION_SORT { get; set; }

        // ==========================================
        // 行為與結構控制 (UI Behavior & Hierarchy)
        // ==========================================
        /// <summary>
        /// 是否顯示於畫面上 (對應 f.IS_SHOW)
        /// </summary>
        public string IS_SHOW { get; set; } = string.Empty;
        
        /// <summary>
        /// 是否允許另開視窗 (對應 f.CAN_OINW)
        /// </summary>
        public string CAN_OINW { get; set; } = string.Empty;
        
        /// <summary>
        /// 樹狀結構層級深度 (對應 f.FUNCTION_LEVEL)
        /// </summary>
        public int FUNCTION_LEVEL { get; set; }
        
        /// <summary>
        /// 父節點代碼 (對應 f.PARENT_CODE)
        /// 💡 供後續組裝樹狀結構 (FindMenu 遞迴) 尋找掛載點使用
        /// </summary>
        public string PARENT_CODE { get; set; } = string.Empty;

        // ==========================================
        // 權限控制 (Permissions)
        // ==========================================
        /// <summary>
        /// 該角色擁有的位元權限 (對應 f.PERM_BITS)
        /// 🚀 關鍵改變：維持列舉型別，Dapper 依然會自動將資料庫的數字轉譯為 CrudpPermissions Enum
        /// </summary>
        public CrudpPermissions PERM_BITS { get; set; }
    }