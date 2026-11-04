using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using HIS2_ARM_KeyDB.Models;
using HIS2_ARM_KeyDB.Models.Auth;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace HIS2_ARM_KeyDB.Services.Auth;

public class TokenService : ITokenService
{
    private readonly JwtSettings _jwt;
    private readonly SymmetricSecurityKey _key;
    private readonly SigningCredentials _credentials;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    
    // 💡 供應給 AuthService 的 TTL 屬性
    public TimeSpan AccessExpire { get; }
    public TimeSpan RefreshExpire { get; }

    public TokenService(IOptions<JwtSettings> jwtOptions)
    {
        _jwt = jwtOptions.Value;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        _credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        
        // 💡 在建構時直接計算好 TimeSpan，後續呼叫不用重複運算
        // AccessToken 1 小時，RefreshToken 8 小時有效
        AccessExpire = TimeSpan.FromMinutes(_jwt.AccessExpireMinutes);
        RefreshExpire = TimeSpan.FromMinutes(_jwt.RefreshDurationMinutes);
    }
    
    private (string Token, string Jti, DateTime ExpiresAt) CreateToken(
        List<Claim> claims,
        DateTime expiresAt)
    {
        var jti = Guid.NewGuid().ToString("N").ToUpper();

        var fullClaims = new List<Claim>(claims)
        {
            new Claim(JwtRegisteredClaimNames.Jti, jti)
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(fullClaims),
            Expires = expiresAt,
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            SigningCredentials = _credentials
        };

        var token = _tokenHandler.CreateToken(descriptor);

        return (_tokenHandler.WriteToken(token), jti, expiresAt);
    }
    
    // 💡 實作回傳 Tuple，完美對接 AuthService
    public (string Token, string Jti, DateTime ExpiresAt) GenerateAccessToken(User user)
    {
        // 💡 最佳化：直接使用建構子算好的 AccessExpire
        var expiresAt = DateTime.UtcNow.Add(AccessExpire);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.UserId),
            new Claim(ClaimTypes.Name, user.Username),
        };

        if (user.RoleIds != null)
        {
            claims.AddRange(user.RoleIds.Select(r => new Claim(ClaimTypes.Role, r)));
        }
        
        return CreateToken(claims, expiresAt);
    }

    public (string Token, string Jti, DateTime ExpiresAt) GenerateRefreshToken(string userId)
    {
        // 💡 最佳化：直接使用建構子算好的 RefreshExpire
        var expiresAt = DateTime.UtcNow.Add(RefreshExpire);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        };

        return CreateToken(claims, expiresAt);
    }

    public ClaimsPrincipal? ValidateToken(string token, out SecurityToken? validatedToken)
    {
        validatedToken = null;

        try
        {
            return _tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidateIssuer = true,
                ValidIssuer =  _jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = _jwt.Audience,
                ValidateLifetime = true,
                // HIS2 系統是多節點部署（例如 Docker 容器或多台 App Server），
                // 只要任何一台的系統時鐘跟簽發 token 的那台有幾秒誤差（NTP 沒同步好），
                // access token 可能會在真正過期前就被判定失效，使用者會遇到莫名其妙的 401。
                // 建議至少留 30 秒~1 分鐘的容忍度
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out validatedToken);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    public string? ExtractJti(string token)
    {
        try
        {
            var jwtToken = _tokenHandler.ReadJwtToken(token);
            return jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        }
        catch
        {
            return null;
        }
    }
    
    // 💡 TokenService.cs
    public (
        (string Token, string Jti, DateTime ExpiresAt) Access, 
        (string Token, string Jti, DateTime ExpiresAt) Refresh
        ) GenerateTokens(User user)
    {
        // 1. 產生 Access Token (包含角色權限)
        var access = GenerateAccessToken(user);
    
        // 2. 產生 Refresh Token (僅含 UserId)
        var refresh = GenerateRefreshToken(user.UserId);

        return (access, refresh);
    }
}