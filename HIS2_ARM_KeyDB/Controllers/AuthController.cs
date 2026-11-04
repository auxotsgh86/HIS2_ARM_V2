using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using HIS2_ARM_KeyDB.Attributes;
using Microsoft.Extensions.Logging;
using HIS2_ARM_KeyDB.Services;
using HIS2_ARM_KeyDB.Models.Auth; // 引入拆分後的 Models
using Microsoft.AspNetCore.Http;
using System;
using System.Security.Claims;
using Dapper;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Dto;
using HIS2_ARM_KeyDB.Models;
using HIS2_ARM_KeyDB.Services.Auth;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Oracle.ManagedDataAccess.Client;

namespace HIS2_ARM_KeyDB.Controllers
{
    /// <summary>
    /// 【中央身分認證與授權核心 API 控制器】
    /// 負責處理全新 HIS2 架構下的身分驗證、Token 換發、安全性搜身與全域登出狀態撤銷。
    /// </summary>
    [ApiController]
    [Route("api/v1/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;
        private readonly ILogger<AuthController> _logger;
        private readonly ILegacyTokenService _legacyTokenService;
        private readonly ITokenService _tokenService;
        private readonly OracleDbService _rmdb;

        private const string ACCESS_COOKIE_NAME = "ARM_ACCESS_TOKEN";
        private const string REFRESH_COOKIE_NAME = "ARM_REFRESH_TOKEN";

        public AuthController(
            AuthService authService,
            ILogger<AuthController> logger,
            ILegacyTokenService legacyTokenService,
            ITokenService tokenService,
            OracleDbService rmdb)
        {
            _authService = authService;
            _logger = logger;
            _legacyTokenService = legacyTokenService;
            _tokenService = tokenService;
            _rmdb = rmdb;
        }

        private CookieOptions CreateCookieOptions(DateTime expiresAt)
        {
            return new CookieOptions
            {
                HttpOnly = true,
                // 如果 httpClient 到 caddy 是 https ，這裡就要設 true，否則設 false
                // 否則會導致瀏覽器不會帶 Cookie，造成 401
                Secure = IsHttpsRequest(),
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = expiresAt
            };
        }
        
        /// <summary>
        /// 💡 判斷「Client → Caddy/直連」這一段是否為 HTTPS。
        /// UseForwardedHeaders 已將 Caddy 轉發的 X-Forwarded-Proto 解析回 Request.Scheme，
        /// 故正式站台（Caddy 有 tls 終止）此處會是 true；本機純 HTTP 開發時為 false。
        /// </summary>
        private bool IsHttpsRequest() => Request.IsHttps;

        [HttpPost("login")]
        [AllowAnonymous]
        [BypassArmAuth]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.UserId))
            {
                return BadRequest(new { Status = 400, Detail = "無效的登入請求參數" });
            }

            string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            string userAgent = Request.Headers["User-Agent"].ToString();

            // 為了相容舊系統，在登入成功後，會將 Token 同步寫入舊版 Oracle 資料庫
            var (ok, resp, error) =
                await _authService.LoginToRmdbAndUpdateCacheDbSessionInfoAsync(req, clientIp, userAgent);

            if (!ok || resp == null)
            {
                _logger.LogWarning("【防禦警報】身分認證遭拒。帳號: {UserId}, 原因: {error}", req.UserId, error);
                return Unauthorized(new { Status = 401, Detail = error ?? "登入失敗或系統內部錯誤" });
            }

            try
            {
                Response.Cookies.Append(ACCESS_COOKIE_NAME, resp.AccessToken,
                    CreateCookieOptions(resp.AccessExpiresAt));
                Response.Cookies.Append(REFRESH_COOKIE_NAME, resp.RefreshToken,
                    CreateCookieOptions(resp.RefreshExpiresAt));

                _logger.LogInformation("【狀態同步】使用者 {UserId} 成功登入，全域安全憑證已順利佈署。", resp.UserId);

                return Ok(new
                {
                    UserId = resp.UserId,
                    Username = resp.Username,
                    RoleIds = resp.RoleIds,
                    AccessExpiresAt = resp.AccessExpiresAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【致命錯誤】登入控制器執行傳輸層處理時異常。帳號: {UserId}", resp.UserId);
                return StatusCode(500, new { Status = 500, Detail = "伺服器通訊處理異常" });
            }
        }
        
        /// <summary>
        /// 💡 專屬 Controller 的私有方法：負責處理 HTTP Cookie 細節
        /// </summary>
        private void SetJwtCookies(
            (string Token, string Jti, DateTime ExpiresAt) access, 
            (string Token, string Jti, DateTime ExpiresAt) refresh)
        {
            // 共用資安選項
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,             // 禁止前端 JS 讀取 (防 XSS)
                Secure = IsHttpsRequest(),               // 限制 HTTPS 傳輸
                SameSite = SameSiteMode.Lax,  // 允許跨站跳轉攜帶
                Expires = access.ExpiresAt,
                Path = "/"
            };

            // 寫入 Access Token Cookie (路徑設為全域)
            var accessOptions = cookieOptions;
            Response.Cookies.Append("ARM_ACCESS_TOKEN", access.Token, accessOptions);

            // 寫入 Refresh Token Cookie (路徑限縮在 AuthController 以策安全)
            var refreshOptions =  new CookieOptions
            {
                HttpOnly = true,
                Secure = IsHttpsRequest(),
                SameSite = SameSiteMode.Lax,
                Expires = refresh.ExpiresAt,
                Path = "/api/v1/Auth"
            };
            Response.Cookies.Append("ARM_REFRESH_TOKEN", refresh.Token, refreshOptions);
        }

        [HttpPost("verify")]
        [AllowAnonymous]
        [BypassArmAuth]
        public async Task<IActionResult> VerifyAccess([FromBody] VerifyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                _logger.LogWarning("【授權驗證】收到無效的授權驗證請求 (Payload 為空或未帶 Token)");
                return BadRequest(new { Status = 400, Detail = "無效的驗證請求" });
            }

            try
            {
                var result = await _authService.CheckPermissionAsync(
                    request.Token,
                    request.SystemId,
                    request.ControllerName,
                    request.ActionName
                );

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【授權驗證】執行權限驗證時發生嚴重異常。系統別: {SystemId}, 目標: {ControllerName}/{ActionName}",
                    request.SystemId, request.ControllerName, request.ActionName);

                return StatusCode(500, new { Status = 500, Detail = "授權服務內部異常" });
            }
        }

        [HttpPost("refresh")]
        [AllowAnonymous]
        [BypassArmAuth]
        public async Task<IActionResult> Refresh()
        {
            if (!Request.Cookies.TryGetValue(REFRESH_COOKIE_NAME, out string? refreshToken) ||
                string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogWarning("【憑證續期】更換憑證失敗：瀏覽器未帶有合法的隨身 Refresh Token Cookie。");
                return Unauthorized(new { Status = 401, Detail = "Refresh Token 遺失或已過期，請重新登入" });
            }

            string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            string userAgent = Request.Headers["User-Agent"].ToString();

            var (ok, resp, error) = await _authService.RefreshAsync(refreshToken, clientIp, userAgent);

            if (!ok || resp == null)
            {
                _logger.LogWarning("【憑證續期】授權核心拒絕換發 Token。原因: {Error}", error);
                return Unauthorized(new { Status = 401, Detail = error ?? "憑證換發失敗" });
            }

            try
            {
                Response.Cookies.Append(ACCESS_COOKIE_NAME, resp.AccessToken,
                    CreateCookieOptions(resp.AccessExpiresAt));
                Response.Cookies.Append(REFRESH_COOKIE_NAME, resp.RefreshToken,
                    CreateCookieOptions(resp.RefreshExpiresAt));

                _logger.LogInformation("【憑證續期】使用者 {UserId} 憑證換發成功。", resp.UserId);

                return Ok(new { AccessExpiresAt = resp.AccessExpiresAt });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【致命錯誤】憑證續期控制器執行傳輸層處理時異常。帳號: {UserId}", resp.UserId);
                return StatusCode(500, new { Status = 500, Detail = "伺服器通訊處理異常" });
            }
        }

        [HttpPost("logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout()
        {
            if (Request.Cookies.TryGetValue(ACCESS_COOKIE_NAME, out string? accessToken) &&
                !string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    await _authService.LogoutAsync(accessToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "【狀態註銷】通知授權中心註銷 Token 時發生異常，系統啟動降級強制登出。");
                }
            }

            Response.Cookies.Delete(ACCESS_COOKIE_NAME, new CookieOptions { Path = "/" });
            Response.Cookies.Delete(REFRESH_COOKIE_NAME, new CookieOptions { Path = "/" });

            _logger.LogInformation("【狀態註銷】全域憑證已成功銷毀，使用者安全登出。");
            return Ok(new { Message = "登出成功，全域憑證已撤銷並從瀏覽器抹除" });
        }

        /// <summary>
        /// 取得供舊版 HIS 系統 (Legacy System) 跨系統跳轉用的 Token
        /// </summary>
        [HttpGet("legacy-token")]
        [BypassArmAuth] // 💡 基礎建設 API，不納入 BITAND 業務權限盤查
        public async Task<IActionResult> GetLegacyToken()
        {
            // 1. 從 JWT 中安全地取得目前登入者的 UserId
            // var userId = User.Identity?.Name;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("嘗試取得 Legacy Token 失敗：無法從 JWT 解析出 UserId");
                return Unauthorized(new { Status = 401, Detail = "無法識別使用者身分" });
            }

            // 2. 從 HttpContext 中安全地取得 Client IP，
            // 以配合雙寫機制的驗證
            // (就算架構有經過 Nginx 或 Caddy 反向代理，只要確保有掛載 ForwardedHeaders 中介軟體，就可以正確解析出原始 Client IP)
            string? clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (string.IsNullOrEmpty(clientIp))
            {
                _logger.LogWarning("使用者 {UserId} 嘗試取得 Legacy Token 失敗：無法解析出 Client IP",  userId);
                
                return Unauthorized(new
                {
                    Status = 401,
                    Detail = "無法解析出使用者端 IP，請確認網路環境或聯絡資訊室"
                });
            }

            // 3. 呼叫您封裝好的強固服務：取得現有 Token 或生成全新 Token
            string legacyToken = await _legacyTokenService.GetOrGenerateTokenToRmdbAsync(userId, clientIp);

            if (string.IsNullOrEmpty(legacyToken))
            {
                _logger.LogError("使用者 {UserId} 嘗試取得 Legacy Token 失敗 (可能為資料庫連線異常)", userId);
                return StatusCode(500, new { Status = 500, Detail = "無法產生舊版驗證憑證，請聯絡資訊室" });
            }

            return Ok(new { Token = legacyToken, UserId = userId });
        }

        /// <summary>
        /// 處理從舊版 HIS 系統發起的單一登入請求
        /// 💡 收取 Legacy Token，查核後直接在 Header 種植 HttpOnly JWT Cookie
        /// </summary>
        [HttpPost("login-legacy")] // 改為 HttpPost 較符合登入行為的語意
        [AllowAnonymous]
        [BypassArmAuth]
        public async Task<IActionResult> LoginWithLegacyToken([FromBody] LegacyLoginRequest request)
        {
            // 1. 防呆：確保 Token 存在
            if (request == null || string.IsNullOrWhiteSpace(request.LegacyToken))
            {
                _logger.LogWarning("Legacy SSO 登入失敗：未提供 Token");
                return BadRequest(new { Status = 400, Detail = "登入失敗：遺失驗證憑證" });
            }

            try
            {
                // 2. 嚴格驗證 Legacy Token，並從資料庫查出真實的 UserId
                var realUserId = await _authService.GetRealUserIdFromLagecyToken(request.LegacyToken);
                
                // 3. 驗證失敗處理：拒絕信任
                if (string.IsNullOrEmpty(realUserId))
                {
                    _logger.LogWarning("Legacy SSO 登入失敗：Token 無效或已過期 ({Token})", request.LegacyToken);
                    return Unauthorized(new { Status = 401, Detail = "無效或已過期的登入憑證，請重新登入" });
                }

                // 4. 憑證有效！執行核發 JWT Cookie 的標準流程
                // 💡 共用您原本登入邏輯中產生 JWT 並 Set-Cookie 的方法
                var _user = await _rmdb.GetUserByIdFromRmdbAsync(realUserId);
                
                // 🛡️ 新增防呆：防範「憑證合法但查無此人」的邊界狀況
                if (_user == null)
                {
                    _logger.LogWarning("Legacy SSO 登入失敗：Token 合法，但 RMDB 中找不到對應的使用者 ({UserId})", realUserId);
                    // 回傳 401，讓前端知道這張 Token 不能用，需要重新走正常登入流程
                    return Unauthorized(new { Status = 401, Detail = "無法在系統中找到您的帳號資料，請聯繫資訊室" });
                }
                
                var tokenCombinations = _tokenService.GenerateTokens(_user);
                
                SetJwtCookies(tokenCombinations.Access, tokenCombinations.Refresh);

                _logger.LogInformation("使用者 {UserId} 成功利用 Legacy Token 完成 ArmV2 登入", realUserId);

                // 5. 成功回傳 (不需要 Redirect)
                // 回傳使用者基本資訊，讓呼叫端知道登入成功
                return Ok(new
                {
                    Status = 200,
                    Message = "登入成功",
                    UserId = realUserId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy SSO 登入過程中發生異常。Token: {Token}", request.LegacyToken);
                return StatusCode(500, new { Status = 500, Detail = "跨系統登入發生異常，請聯絡資訊室" });
            }
        }
    }
}