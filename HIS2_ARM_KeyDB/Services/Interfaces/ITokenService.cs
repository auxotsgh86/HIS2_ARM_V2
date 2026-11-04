using System.Security.Claims;
using HIS2_ARM_KeyDB.Models;
using Microsoft.IdentityModel.Tokens;

namespace HIS2_ARM_KeyDB.Services.Interfaces
{
    public interface ITokenService
    {
        /// <summary>
        /// 簽發包含舊版造橋簽證之 Access Token
        /// </summary>
        /// <returns>回傳 Tuple：(JWT字串, 唯一識別碼 Jti, 過期時間)</returns>
        (string Token, string Jti, DateTime ExpiresAt) GenerateAccessToken(User user);

        /// <summary>
        /// 簽發 Refresh Token
        /// </summary>
        (string Token, string Jti, DateTime ExpiresAt) GenerateRefreshToken(string userId);

        ClaimsPrincipal? ValidateToken(string token, out SecurityToken? validatedToken);

        string? ExtractJti(string token);

        public TimeSpan AccessExpire { get; }
        public TimeSpan RefreshExpire { get; }

        (
            (string Token, string Jti, DateTime ExpiresAt) Access,
            (string Token, string Jti, DateTime ExpiresAt) Refresh
            ) GenerateTokens(User user);
    }
}