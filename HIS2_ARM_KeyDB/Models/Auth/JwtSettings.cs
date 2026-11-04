using System.ComponentModel.DataAnnotations;

namespace HIS2_ARM_KeyDB.Models.Auth
{
    /// <summary>
    /// JWT 核心組態配置映射模型 (對應 appsettings.json 中的 JwtSettings 區塊)
    /// </summary>
    public class JwtSettings
    {
        [Required]
        public string SecretKey { get; set; } = "";

        [Required]
        public string Issuer { get; set; } = "";

        [Required]
        public string Audience { get; set; } = "";

        [Range(1, 1440)]
        public int AccessExpireMinutes { get; set; }

        [Range(1, 10080)]
        public int RefreshDurationMinutes { get; set; }

        [Range(1, 60)]
        public int PwdChangeTicketMinutes { get; set; }
    }

    /// <summary>
    /// Access Token 簽發結果載荷
    /// </summary>
    public class AccessTokenResult
    {
        /// <summary>
        /// 已簽章並加密完成的 JWT 字串
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// 該憑證的精確過期時間點
        /// </summary>
        public DateTime ExpiresAt { get; set; }
    }
}