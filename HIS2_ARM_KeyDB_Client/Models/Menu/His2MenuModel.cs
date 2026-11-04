namespace HIS2_ARM_KeyDB_Client.Models.Menu;

    /// <summary>
    /// 從資料庫取得的功能內容
    /// </summary>
    public class MenuModel
    {
        /// <summary>
        /// 系統代碼
        /// </summary>
        public string SYSTEM_ID { get; set; } = string.Empty;
        /// <summary>
        /// 系統名稱
        /// </summary>
        public string SYSTEM_NAME { get; set; } = string.Empty;
        /// <summary>
        /// 選單代碼
        /// </summary>
        public string MENU_ID { get; set; } = string.Empty;
        /// <summary>
        /// 選單名稱
        /// </summary>
        public string MENU_NAME { get; set; } = string.Empty;
        /// <summary>
        /// 選單圖示
        /// </summary>
        public string MENU_ICON { get; set; } = string.Empty;
        /// <summary>
        /// 控制器名稱
        /// </summary>
        public string CONTROLLER_NAME { get; set; } = string.Empty;
        /// <summary>
        /// 程式名稱
        /// </summary>
        public string FUNCTION_NAME { get; set; } = string.Empty;
        /// <summary>
        /// Action名稱
        /// </summary>
        public string ACTION_NAME { get; set; } = string.Empty;
        /// <summary>
        /// 是否顯示
        /// </summary>
        public string IS_SHOW { get; set; } = string.Empty;
        /// <summary>
        /// 是否可另開視窗
        /// </summary>
        public string CAN_OINW { get; set; } = string.Empty;
        /// <summary>
        /// 功能所在階層
        /// </summary>
        public int FUNCTION_LEVEL { get; set; } = 0;
        /// <summary>
        /// 功能父節點
        /// </summary>
        public string PARENT_CODE { get; set; } = string.Empty;
    }

    /// <summary>
    /// 選單顯示的功能內容
    /// </summary>
    public class MenuViewModel
    {
        /// <summary>
        /// 圖示
        /// </summary>
        public string ICON { get; set; } = string.Empty;
        /// <summary>
        /// 功能代碼
        /// </summary>
        public string ID { get; set; } = string.Empty;
        /// <summary>
        /// 功能名稱
        /// </summary>
        public string NAME { get; set; } = string.Empty;
        /// <summary>
        /// Action名稱
        /// </summary>
        public string ACTION_NAME { get; set; } = string.Empty;
        /// <summary>
        /// 層級
        /// </summary>
        public int LEVEL { get; set; } = 0;
        /// <summary>
        /// 是否為目前功能
        /// </summary>
        public bool ACTIVE { get; set; } = false;
        /// <summary>
        /// 是否顯示於選單上
        /// </summary>
        public string IS_SHOW { get; set; } = string.Empty;
        /// <summary>
        /// 是否可另開視窗
        /// </summary>
        public string CAN_OINW { get; set; } = string.Empty;
        /// <summary>
        /// 子項功能清單
        /// </summary>
        public List<MenuViewModel> FunctionList { get; set; } = [];
    }