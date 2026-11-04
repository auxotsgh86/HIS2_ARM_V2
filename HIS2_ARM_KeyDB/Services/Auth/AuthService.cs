// 檔案：Services/AuthService.cs

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;
using HIS2_ARM_KeyDB.Models.Auth;
using HIS2_ARM_KeyDB.Services.Cache;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
// 💡 CacheTokenManager：L1 雙引信失效機制

// 確保有您的 Request/Response/Result 定義

namespace HIS2_ARM_KeyDB.Services.Auth
{
    public class AuthService
    {
        private readonly Data.OracleDbService _rmdb;
        private readonly ValkeyDbService _cacheDb;
        private readonly ITokenService _token;
        private readonly ILegacyTokenService _legacyTokenService; // 負責處理舊版 Token 雙寫
        private readonly IMemoryCache _memoryCache;
        private readonly CacheTokenManager _tokenManager; // 💡 L1 快取雙引信管理器
        private readonly ICacheSyncService _cacheSync; // 💡 [移植自 ArmPermissionService] L2 Cache Miss 自我修復
        private readonly ILogger<AuthService> _logger;

        // 快取防護 (Cache Stampede Protection)
        // private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        // TODO: _dictKeyLocks 目前沒有清理機制，長期執行下 SemaphoreSlim 物件會隨著不同的
        // systemId/roleHash/controller/action 組合持續增加，不會自動移除。
        // 之後應該加上定期清理（例如背景 Task 移除 CurrentCount == 1 且沒有人等待的鎖），
        // 或改用有上限的快取結構（如 MemoryCache 搭配 sliding expiration）來管理這個字典本身。
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _dictKeyLocks = new();

        private static SemaphoreSlim GetLockForKey(string key)
            => _dictKeyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        public AuthService(
            Data.OracleDbService rmdb,
            ValkeyDbService cacheDb,
            ITokenService token,
            ILegacyTokenService legacyTokenService,
            IMemoryCache memoryCache,
            CacheTokenManager tokenManager,
            ICacheSyncService cacheSync,
            ILogger<AuthService> logger)
        {
            _rmdb = rmdb;
            _cacheDb = cacheDb;
            _token = token;
            _legacyTokenService = legacyTokenService;
            _memoryCache = memoryCache;
            _tokenManager = tokenManager;
            _cacheSync = cacheSync;
            _logger = logger;
        }

        // =========================================================================================
        // 模塊一：身分認證與憑證派發 (Authentication & Dual-Write)
        // =========================================================================================

        public async Task<(bool Ok, AuthResponse? Resp, string? Error)> LoginToRmdbAndUpdateCacheDbSessionInfoAsync(
            LoginRequest req, string clientIp, string userAgent)
        {
            try
            {
                // 1. 沿用您原本最正確的寫法：透過 Oracle 取得使用者實體
                var user = await _rmdb.GetUserByIdFromRmdbAsync(req.UserId);
                if (user is null || !user.IsActive)
                {
                    return (false, null, "帳號不存在或已停用");
                }

                // 2. 沿用您原本的 BCrypt 驗證
                if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                {
                    _logger.LogWarning("Login failed: {UserId}", req.UserId);
                    return (false, null, "帳號或密碼錯誤");
                }

                // 3. 💡 完美解耦的 Oracle 雙寫：預先幫他把舊系統的 Token 準備好 (Upsert)
                // 完全不需要接回傳值，因為新版 JWT 已經純粹化，不需要包裝舊版 Token！
                await _legacyTokenService.GetOrGenerateTokenToRmdbAsync(user.UserId, clientIp);

                // 4. 💡 簽發極致純粹的新版憑證
                var (accessToken, accessJti, accessExp) = _token.GenerateAccessToken(user);
                var (refreshToken, refreshJti, refreshExp) = _token.GenerateRefreshToken(user.UserId);

                // 5. 將 Session 寫入 KeyDB (L2 快取)
                await _cacheDb.SetSessionAsync(new SessionInfo
                {
                    Jti = accessJti,
                    UserId = user.UserId,
                    ClientIp = clientIp,
                    UserAgent = userAgent,
                    IssuedAt = DateTime.UtcNow,
                    ExpiresAt = accessExp,
                    RefreshJti = refreshJti
                }, _token.AccessExpire);

                await _cacheDb.SetRefreshSessionAsync(new RefreshSession
                {
                    RefreshJti = refreshJti,
                    AccessJti = accessJti,
                    UserId = user.UserId,
                    IssuedAt = DateTime.UtcNow,
                    ExpiresAt = refreshExp
                }, _token.RefreshExpire);

                // 6. 登入時預填 user_roles，供後續授權驗證 (CheckPermissionAsync) 高速取用
                await _cacheDb.SetUserRolesAsync(user.UserId, user.RoleIds);
                await _rmdb.UpdateLastLoginAsync(user.UserId, clientIp);

                _logger.LogInformation("Login: {UserId} roles=[{R}]", user.UserId, string.Join(",", user.RoleIds));

                // 7. 回傳結果
                return (true, new AuthResponse
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    UserId = user.UserId,
                    Username = user.Username,
                    RoleIds = user.RoleIds,
                    AccessExpiresAt = accessExp,
                    RefreshExpiresAt = refreshExp
                }, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoginToRmdbAndUpdateCacheDbSessionInfoAsync 發生未預期異常 (UserId: {UserId})",
                    req.UserId);
                return (false, null, "登入過程發生系統異常");
            }
        }

        public async Task<(bool Ok, AuthResponse? Resp, string? Error)> RefreshAsync(string refreshTokenStr,
            string clientIp, string userAgent)
        {
            try
            {
                if (_token.ValidateToken(refreshTokenStr, out _) is null)
                    return (false, null, "Refresh Token 無效");

                var jti = _token.ExtractJti(refreshTokenStr);
                if (jti is null) return (false, null, "Token 缺少 jti");

                var rs = await _cacheDb.GetRefreshSessionAsync(jti);
                if (rs is null) return (false, null, "Refresh Session 不存在或已撤銷");

                await _cacheDb.RevokeSessionAsync(rs.AccessJti);

                var user = await _rmdb.GetUserByIdFromRmdbAsync(rs.UserId);
                if (user is null || !user.IsActive) return (false, null, "帳號已停用");

                // 💡 雙寫展延
                await _legacyTokenService.GetOrGenerateTokenToRmdbAsync(user.UserId, clientIp);

                var (newAccess, newJti, newExp) = _token.GenerateAccessToken(user);

                await _cacheDb.SetSessionAsync(new SessionInfo
                {
                    Jti = newJti,
                    UserId = user.UserId,
                    ClientIp = clientIp,
                    UserAgent = userAgent,
                    IssuedAt = DateTime.UtcNow,
                    ExpiresAt = newExp,
                    RefreshJti = jti
                }, _token.AccessExpire);

                rs.AccessJti = newJti;
                await _cacheDb.SetRefreshSessionAsync(rs, rs.ExpiresAt - DateTime.UtcNow);
                await _cacheDb.SetUserRolesAsync(user.UserId, user.RoleIds);

                return (true, new AuthResponse
                {
                    AccessToken = newAccess,
                    RefreshToken = refreshTokenStr,
                    UserId = user.UserId,
                    Username = user.Username,
                    RoleIds = user.RoleIds,
                    AccessExpiresAt = newExp,
                    RefreshExpiresAt = rs.ExpiresAt
                }, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshAsync 發生未預期異常");
                return (false, null, "憑證換發過程發生系統異常");
            }
        }

        public async Task<bool> LogoutAsync(string accessTokenStr)
        {
            var jti = _token.ExtractJti(accessTokenStr);
            if (jti is null) return false;

            var s = await _cacheDb.GetSessionAsync(jti);
            if (s?.RefreshJti is not null)
                await _cacheDb.RevokeRefreshSessionAsync(s.RefreshJti);

            await _cacheDb.RevokeSessionAsync(jti);

            // 若有實作 RevokeRmdbTokenAsync，可在此呼叫以註銷舊版 Token
            if (s != null)
            {
                await _legacyTokenService.RevokeRmdbTokenAsync(s.UserId);
            }

            return true;
        }

        private async Task<JwtValidationResponse> ValidateAsync(string accessTokenStr)
        {
            if (_token.ValidateToken(accessTokenStr, out _) is null)
                return new JwtValidationResponse(false, null, "Token 無效或已過期");

            var jti = _token.ExtractJti(accessTokenStr);
            if (jti is null)
                return new JwtValidationResponse(false, null, "Token 缺少 jti");

            var session = await _cacheDb.GetSessionAsync(jti);
            if (session is null)
                return new JwtValidationResponse(false, null, "Session 不存在或已撤銷");

            return new JwtValidationResponse(true, session.UserId, null);
        }

        // =========================================================================================
        // 模塊二：微服務 Server-to-Server 權限驗證邏輯 (Authorization & L1/L2 Cache)
        // =========================================================================================

        public async Task<PermissionVerifyResult> CheckPermissionAsync(string tokenStr, string systemId,
            string controllerName, string actionName)
        {
            if (string.IsNullOrWhiteSpace(tokenStr) || string.IsNullOrWhiteSpace(systemId) ||
                string.IsNullOrWhiteSpace(controllerName) || string.IsNullOrWhiteSpace(actionName))
            {
                return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "請求參數不完整" };
            }

            try
            {
                // 1. 沿用您的 ValidateAsync 驗證 Token 存活狀態
                var valResp = await ValidateAsync(tokenStr);
                if (!valResp.IsValid || string.IsNullOrEmpty(valResp.UserId))
                {
                    return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = valResp.Message ?? "無效的憑證" };
                }

                string userId = valResp.UserId;

                // 2. 取得角色 (若 KeyDB 沒有，則 fallback 到 Oracle 查詢)
                var userRoles = await _cacheDb.GetUserRolesAsync(userId);
                if (userRoles == null || !userRoles.Any())
                {
                    var user = await _rmdb.GetUserByIdFromRmdbAsync(userId);
                    if (user == null || !user.IsActive)
                    {
                        return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "帳號無效或已停用" };
                    }

                    userRoles = user.RoleIds;

                    // 帳號有效，但尚未指派任何角色：直接回覆，避免寫入註定無用的空快取(且每次仍會回源查 Oracle)
                    if (userRoles == null || !userRoles.Any())
                    {
                        return new PermissionVerifyResult
                            { IsAllowed = false, UserId = userId, ErrorMessage = "未指派任何角色" };
                    }

                    await _cacheDb.SetUserRolesAsync(userId, userRoles);
                }

                // 3. 進入 L1/L2 快取位元遮罩比對引擎 (取代原本直接 hit Oracle 的 CheckRolesPermissionAsync)
                string roleHash = GenerateRoleHash(userRoles);
                string l1CacheKey = $"arm:l1auth:{systemId}:{roleHash}:{controllerName}:{actionName}";

                if (!_memoryCache.TryGetValue(l1CacheKey, out bool isAuthorized))
                {
                    // await _cacheLock.WaitAsync();
                    var keyLock = GetLockForKey(l1CacheKey);
                    await keyLock.WaitAsync();

                    try
                    {
                        if (!_memoryCache.TryGetValue(l1CacheKey, out isAuthorized))
                        {
                            isAuthorized =
                                await EvaluatePermissionFromCacheDbAsync(systemId, userRoles, controllerName,
                                    actionName);

                            var cacheOptions = new MemoryCacheEntryOptions()
                                // 💡 TTL 只是「保底防線」（Pub/Sub 斷線等極端情況），實際失效主要靠下方雙引信主動觸發，
                                // 因此不需要設太長；改用較短的 TTL，也拿掉 sliding，避免熱門 key 被無限續命、放大保底窗口。
                                .SetAbsoluteExpiration(TimeSpan.FromMinutes(30));

                            // 💡 [雙引信機制，比照 ArmPermissionService.HasPermissionAsync]
                            // 引信 A：綁定所有角色的 Token（角色權限異動 → 立即引爆）
                            // RoleId 跨子系統共用，key 需加上 systemId 前綴，避免其他系統改了同名角色的權限，
                            // 卻誤連帶引爆本系統無關的 L1 快取
                            foreach (var roleId in userRoles)
                            {
                                cacheOptions.AddExpirationToken(_tokenManager.GetChangeToken($"{systemId}:{roleId}"));
                            }

                            // 引信 B：綁定 Action 專屬的 Token（Action 門檻異動 → 立即引爆）
                            // key 格式需與 CacheDbPubSubBackgroundService 收到 "ACTION_UPDATE:" 廣播時組出的 tokenKey 一致
                            string actionTokenKey = $"ACT:{systemId}:{controllerName}.{actionName}";
                            cacheOptions.AddExpirationToken(_tokenManager.GetChangeToken(actionTokenKey));

                            _memoryCache.Set(l1CacheKey, isAuthorized, cacheOptions);
                        }
                    }
                    finally
                    {
                        // _cacheLock.Release();
                        keyLock.Release();
                    }
                }

                return new PermissionVerifyResult
                {
                    IsAllowed = isAuthorized,
                    UserId = userId,
                    ErrorMessage = isAuthorized ? null : "沒有該功能的存取權限"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "驗證權限時發生異常。UserId={UserId}, Target={Target}", "Unknown",
                    $"{systemId}/{controllerName}/{actionName}");
                return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "伺服器驗證異常" };
            }
        }

        public async Task<string?> GetRealUserIdFromLagecyToken(string legacyToken)
        {
            return await _rmdb.GetUserIdFromLegacyTokenAsync(legacyToken);
        }

        private async Task<bool> EvaluatePermissionFromCacheDbAsync(string systemId, IEnumerable<string> roleIds,
            string controller, string action)
        {
            var roles = roleIds.ToList();

            // 💡 [架構優化] 將「API 門檻查詢」與「多角色權限查詢」打包成同一次 Redis 網路往返 (Pipelining)，
            // 取代原本先 await GetRequiredBitsAsync 再 await GetEffectiveBitsAsync 的兩次 round-trip寫法。
            var (requiredBits, rolePerms) =
                await _cacheDb.GetPermissionEvaluationDataAsync(systemId, roles, controller, action);

            if (!requiredBits.HasValue) return false;

            // 若門檻設定為 0，代表此功能完全開放 (Public API)，直接放行
            if (requiredBits.Value == 0) return true;

            int finalUserMask = 0;

            // 💡 [移植自 ArmPermissionService.ComputePermissionFromL2Async]
            foreach (var roleId in roles)
            {
                var bits = rolePerms[roleId];

                // Cache Miss 自我修復：KeyDB 完全查無此角色的權限資料時（快取被清空或系統剛重啟），
                // 交給 ICacheSyncService 回 Oracle 撈取並回填，避免持續誤判為「無權限」
                if (bits.ActionBits == null && bits.ControllerBits == null)
                {
                    await _cacheSync.LoadRolePermissionsToCacheAsync(systemId, roleId);

                    var rebuilt = await _cacheDb.GetEffectiveBitsAsync(systemId, new[] { roleId }, controller, action);
                    bits = rebuilt[roleId];
                }

                int roleEffectiveMask = bits.ActionBits ?? bits.ControllerBits ?? 0;
                finalUserMask |= roleEffectiveMask;

                // Early-return：位元已經湊滿門檻需求，不用再算剩下的角色
                if ((finalUserMask & requiredBits.Value) == requiredBits.Value) return true;
            }

            return (finalUserMask & requiredBits.Value) == requiredBits.Value;
        }

        // =========================================================================================
        // 模塊三：快取生命週期管理與輔助工具
        // =========================================================================================


        private string GenerateRoleHash(IEnumerable<string> roleIds)
        {
            var sortedRoles = roleIds.OrderBy(id => id);
            string combinedRoles = string.Join(",", sortedRoles);

            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combinedRoles));
                return Convert.ToHexString(hashBytes).Substring(0, 16);
            }
        }
    }
}