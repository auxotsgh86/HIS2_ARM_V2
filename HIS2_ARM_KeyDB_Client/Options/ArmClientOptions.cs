namespace HIS2_ARM_KeyDB_Client.Options
{
    /// <summary>
    /// ARM 授權中介軟體與背景服務的共用設定選項
    /// </summary>
    public class ArmClientOptions
    {
        /// <summary>
        /// 當前子系統的身分識別 (例如: HIS2_MRA)
        /// </summary>
        public string SystemId { get; set; } = string.Empty;

        /// <summary>
        /// ARM 權限中心的 API 根網址 (例如: https://localhost/ArmV2)
        /// 💡 用於 L1 Cache 未命中時，呼叫 L2 進行權限驗證，以及系統啟動時的自動註冊
        /// </summary>
        public string ArmV2ApiUrl { get; set; } = string.Empty;

        /// <summary>
        /// 子系統向 ARM 中心執行 M2M 登入與註冊時使用的系統專屬密碼
        /// 💡 資安防護：正式環境應利用環境變數 (ArmClientOptions__SystemPassword) 進行覆寫，避免明碼進入版控
        /// </summary>
        public string SystemPassword { get; set; } = string.Empty;

        /// <summary>
        /// 本機 L1 快取的存活時間 (秒)。若設定為 0 則停用 L1 快取。
        /// </summary>
        public int CacheTtlSeconds { get; set; } = 30;
    }
}