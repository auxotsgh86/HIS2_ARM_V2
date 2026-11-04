namespace HIS2_ARM_KeyDB.Helpers;

    /// <summary>
    /// 負責 UTC 與「舊系統認定時區」之間的轉換。
    ///
    /// 背景：部分舊系統資料表（例如 HIS2_LOGIN_TOKEN）以 host local time 判斷時效，
    /// 本服務內部一律使用 UTC，僅在讀寫這類 legacy 表的邊界上，透過本類別做轉換。
    ///
    /// 時區設定來源優先序：
    ///   1. appsettings.json 的 "LegacySettings:TimeZoneId"（建議明確設定，例如部署到不同院區/主機時）
    ///   2. 若未設定，退回硬編碼預設值 "Asia/Taipei"
    ///
    /// 刻意不讀取 TimeZoneInfo.Local（OS 預設時區）：
    /// container 的系統時區通常與「舊系統實際認定的時區」無關（很多 .NET base image 預設 TZ=UTC），
    /// 若改用 OS 預設值，行為會隨部署環境隱性改變、且不會出現在 code review / git diff 中，
    /// 一旦時區跑掉會很難追查。因此使用明確設定 + 硬編碼 fallback，而非依賴執行環境狀態。
    ///
    /// 若設定的 TimeZoneId 打錯字或系統找不到，會直接拋出例外（fail fast），
    /// 避免這類設定錯誤要到正式環境跑出資料異常才被發現。
    /// </summary>
    public class LegacyTimeZoneHelper
    {
        private const string DefaultTimeZoneId = "Asia/Taipei";
        private readonly TimeZoneInfo _legacyTimeZone;
 
        public LegacyTimeZoneHelper(IConfiguration configuration, ILogger<LegacyTimeZoneHelper> logger)
        {
            var configuredId = configuration["LegacySettings:TimeZoneId"];
            var timeZoneId = string.IsNullOrWhiteSpace(configuredId) ? DefaultTimeZoneId : configuredId;
 
            try
            {
                _legacyTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException ex)
            {
                // Fail fast：設定錯誤應該在啟動階段就被發現，而不是等到某筆資料時間算錯才被注意到。
                logger.LogError(ex, "找不到設定的時區 ID：{TimeZoneId}，請確認 appsettings.json 的 LegacySettings:TimeZoneId 是否正確" +
                    "（Linux/container 請用 IANA ID 如 \"Asia/Taipei\"；Windows host 請用 \"Taipei Standard Time\"）。", timeZoneId);
                throw;
            }
            catch (InvalidTimeZoneException ex)
            {
                logger.LogError(ex, "時區資料本身無效：{TimeZoneId}", timeZoneId);
                throw;
            }
        }
 
        /// <summary>將內部 UTC 時間轉換成舊系統認定的 local time，用於寫入 / 查詢 legacy 表。</summary>
        public DateTime ToLegacyLocalTime(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _legacyTimeZone);
 
        /// <summary>將舊系統 local time 轉回內部 UTC，用於讀取 legacy 表資料後的內部運算。</summary>
        public DateTime FromLegacyLocalTime(DateTime local) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _legacyTimeZone);
    }