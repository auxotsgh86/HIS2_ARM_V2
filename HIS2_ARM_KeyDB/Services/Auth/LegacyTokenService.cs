using System.Data;
using Dapper;
using HIS2_ARM_KeyDB.Helpers;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Oracle.ManagedDataAccess.Client;

namespace HIS2_ARM_KeyDB.Services.Auth
{
    /// <summary>
    /// 【過渡期專用服務】負責處理中央集權雙寫機制，直接透過 Dapper 操縱 Oracle 舊版 Token
    /// 💡 達成與舊專案 100% 解耦，不引入任何舊版程式碼。
    ///
    /// ⚠️ 時區注意事項：
    /// HIS2_LOGIN_TOKEN 這張表由「舊系統」直接讀取，舊系統以其認定的 local time 判斷 LOGOUT_TIME 是否過期。
    /// 本服務內部一律使用 UTC，僅在寫入 / 查詢此表的邊界上，透過 LegacyTimeZoneHelper 轉換成 local time。
    /// 切勿改回直接寫入 UTC，否則會與舊系統的過期判斷產生時差誤差。
    /// </summary>
    public class LegacyTokenService : ILegacyTokenService
    {
        private readonly string _rmdbConnectionString;
        private readonly ILogger<LegacyTokenService> _logger;
        private readonly int _defaultLogoutMinutes;
        private readonly LegacyTimeZoneHelper _timeZoneHelper;
 
        public LegacyTokenService(
            IConfiguration configuration,
            ILogger<LegacyTokenService> logger,
            LegacyTimeZoneHelper timeZoneHelper)
        {
            _logger = logger;
            _rmdbConnectionString = configuration.GetConnectionString("Oracle") ?? string.Empty;
            _defaultLogoutMinutes = configuration.GetValue<int>("LegacySettings:LogoutMinutes", 480);
            _timeZoneHelper = timeZoneHelper;
        }
 
        public async Task<string> GetOrGenerateTokenToRmdbAsync(string userId, string clientIp)
        {
            if (string.IsNullOrWhiteSpace(_rmdbConnectionString))
            {
                _logger.LogError("【雙寫機制】Oracle 連線字串未設定，放棄雙寫同步。");
                return string.Empty;
            }
 
            // 防呆處理 IP，避免寫入 DB 時發生 NullException
            string safeClientIp = string.IsNullOrWhiteSpace(clientIp) ? "127.0.0.1" : clientIp;
 
            try
            {
                using IDbConnection db = new OracleConnection(_rmdbConnectionString);
 
                // 🚨 這張表由舊系統以其 local time 判斷過期，因此比對用的「現在時間」
                // 必須轉換成 local time，才能跟表中既有的 local time 語意一致。
                var nowLocal = _timeZoneHelper.ToLegacyLocalTime(DateTime.UtcNow);
 
                // 1. 💡 輕量化查詢：直接用 <string> 接值，不需要額外宣告 Model
                string checkSql = """
                                  SELECT LOGIN_TOKEN 
                                  FROM HIS2USER2.HIS2_LOGIN_TOKEN 
                                  WHERE USER_ID = :userId 
                                    AND USER_IP = :safeClientIp 
                                    AND LOGOUT_TIME > :nowLocal 
                                    AND ROWNUM = 1
                                  """;
 
                var existingToken = await db.QueryFirstOrDefaultAsync<string>(checkSql, new { userId, safeClientIp, nowLocal });
 
                // 有 existingToken ，展延其 LOGOUT_TIME；沒有則生成新 Token 並寫入
                // 沒有就不展延。改成直接製造 newToken(legacy token)，並寫入新紀錄。
                if (!string.IsNullOrWhiteSpace(existingToken))
                {
                    // 計算新的登出時間，寫入時同樣要轉成舊系統的 local time
                    var newLogoutTimeLocal = _timeZoneHelper.ToLegacyLocalTime(DateTime.UtcNow).AddMinutes(_defaultLogoutMinutes);
 
                    string updateSql = """
                                       UPDATE HIS2USER2.HIS2_LOGIN_TOKEN 
                                       SET LOGOUT_TIME = :logoutTime
                                       WHERE LOGIN_TOKEN = :token
                                       """;
 
                    await db.ExecuteAsync(updateSql, new 
                    { 
                        logoutTime = newLogoutTimeLocal, 
                        token = existingToken 
                    });
    
                    _logger.LogInformation("【雙寫機制】使用者 {UserId} 的舊版 Token 仍在有效時間內，已展延壽命。", userId);
                    return existingToken;
                }
                else {
                    // 2B. 自主產生亂碼，寫入新紀錄
                    // ToString("N") 產生不帶連字號的 GUID，並轉為大寫，符合舊系統 Token 格式
                    string newToken = Guid.NewGuid().ToString("N").ToUpper();
 
                    // 寫入舊系統的表，統一使用 local time，
                    // 由 loginTimeLocal 推算 logoutTimeLocal，語意上是「local 登入時間 + N 分鐘 = local 登出時間」。
                    var loginTimeLocal = _timeZoneHelper.ToLegacyLocalTime(DateTime.UtcNow);
                    var logoutTimeLocal = loginTimeLocal.AddMinutes(_defaultLogoutMinutes);
 
                    string insertSql = """
                                       INSERT INTO HIS2USER2.HIS2_LOGIN_TOKEN (
                                           LOGIN_ID, 
                                           LOGIN_TIME, 
                                           USER_ID, 
                                           LOGOUT_TIME, 
                                           USER_IP, 
                                           LOGIN_TOKEN
                                       ) VALUES (
                                           HIS2USER2.SEQ_HIS2_LOGIN_TOKEN.NEXTVAL, 
                                           :loginTime, 
                                           :userId, 
                                           :logoutTime, 
                                           :safeClientIp, 
                                           :newToken
                                       )
                                       """;
 
                    await db.ExecuteAsync(insertSql, new 
                    { 
                        loginTime = loginTimeLocal, 
                        userId, 
                        logoutTime = logoutTimeLocal, 
                        safeClientIp, 
                        newToken 
                    });
 
                    _logger.LogInformation("【雙寫機制】已成功為使用者 {UserId} 於 Oracle 新增舊版 Token 紀錄。", userId);
                    return newToken;
                }
            }
            catch (Exception ex)
            {
                // 🛡️ 容錯隔離：這段 try-catch 非常重要，它確保 Oracle 就算當機，也不會影響微服務核心的 JWT 登入！
                _logger.LogError(ex, "【雙寫機制】嘗試同步 Oracle 舊版 Token 時發生嚴重異常！UserId: {UserId}", userId);
                return string.Empty;
            }
        }
 
        public async Task RevokeRmdbTokenAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(_rmdbConnectionString)) return;
 
            try
            {
                using IDbConnection db = new OracleConnection(_rmdbConnectionString);
 
                // 提早將該使用者的有效 Token 標記為過期。
                // 這張表由舊系統以 local time 判斷過期，因此「現在時間」與寫入值都要轉成 local time。
                var nowLocal = _timeZoneHelper.ToLegacyLocalTime(DateTime.UtcNow);
 
                string updateSql = """
                                   UPDATE HIS2USER2.HIS2_LOGIN_TOKEN 
                                   SET LOGOUT_TIME = :nowLocal 
                                   WHERE USER_ID = :userId 
                                     AND LOGOUT_TIME > :nowLocal
                                   """;
 
                await db.ExecuteAsync(updateSql, new { userId, nowLocal });
 
                _logger.LogInformation("【雙寫機制】已成功撤銷使用者 {UserId} 的 Oracle 舊版 Token。", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【雙寫機制】撤銷 Oracle 舊版 Token 時發生異常。UserId: {UserId}", userId);
            }
        }
    }
}