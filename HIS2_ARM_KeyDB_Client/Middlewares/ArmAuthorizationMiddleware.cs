using HIS2_ARM_KeyDB_Client.Attributes;
using HIS2_ARM_KeyDB_Client.Models.Auth;
using HIS2_ARM_KeyDB_Client.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using HIS2_ARM_KeyDB_Client.Constants;

namespace HIS2.ARM.Client.Middlewares
{
    public class ArmAuthorizationMiddleware
    {
        private readonly RequestDelegate _next;
        // ✅ 改用 Keyed Service，型別上就保證這是專屬於 ARM 驗證用的快取，
        //    不會跟凌群或其他服務共用同一個 IMemoryCache 實體。
        private readonly IMemoryCache _positiveCache;
        // 正向跟負向的 L1 cache 分開，方便日後統計或清除
        private readonly IMemoryCache _negativeCache;
        
        private readonly ILogger<ArmAuthorizationMiddleware> _logger;
        private readonly ArmClientOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;

        private const string ACCESS_COOKIE_NAME = "ARM_ACCESS_TOKEN";

        public ArmAuthorizationMiddleware(
            RequestDelegate next,
            [FromKeyedServices(ArmCacheKeys.AuthVerifyPositive)] IMemoryCache positiveCache,
            [FromKeyedServices(ArmCacheKeys.AuthVerifyNegative)] IMemoryCache negativeCache,
            ILogger<ArmAuthorizationMiddleware> logger,
            // IOptions<T> 是 .NET 中用來讀取設定值的強型別（Strongly-typed）介面。
            // 它的主要特點是生命週期為 Singleton（單例）、不支援熱更新以及提供 Value 屬性來取得設定物件。
            IOptions<ArmClientOptions> options,
            IHttpClientFactory httpClientFactory)
        {
            _next = next;
            _positiveCache = positiveCache;
            _negativeCache = negativeCache;
            _logger = logger;
            _options = options.Value;
            _httpClientFactory = httpClientFactory;

            if (string.IsNullOrWhiteSpace(_options.SystemId))
                throw new ArgumentNullException(nameof(options.Value.SystemId), "未設定當前子系統的身分 (SystemId)。");

            if (string.IsNullOrWhiteSpace(_options.ArmV2ApiUrl))
                throw new ArgumentNullException(nameof(options.Value.ArmV2ApiUrl), "未設定 ARM 權限中心的連線網址 (認證中心ApiUrl)。");
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint == null)
            {
                await _next(context);
                return;
            }

            bool isBypass = endpoint.Metadata.GetMetadata<BypassArmAuthAttribute>() != null ||
                            endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null;

            if (isBypass)
            {
                await _next(context);
                return;
            }

            var routeValues = context.Request.RouteValues;
            string? controllerName = routeValues["controller"]?.ToString();
            string? actionName = routeValues["action"]?.ToString();

            // 💡 新增：若不是 Controller 路由，嘗試改用 Razor Page 的路由值
            if (string.IsNullOrEmpty(controllerName))
            {
                string? pageName = routeValues["page"]?.ToString();
                if (!string.IsNullOrEmpty(pageName))
                {
                    controllerName = pageName;                 // 以頁面路徑作為分組依據，對應 ScanEndpoints 的 ControllerName
                    actionName = context.Request.Method;         // 以 HTTP Method 作為動作依據，對應 ScanEndpoints 的 Actions
                }
            }

            if (string.IsNullOrEmpty(controllerName) || string.IsNullOrEmpty(actionName))
            {
                await HandleUnauthorizedAsync(context, HttpStatusCode.Forbidden, "無效的路由目標");
                return;
            }

            string? token = null;

            // 確認如果沒有登入， ARM_ACCESS_TOKEN 會是 null
            if (context.Request.Cookies.TryGetValue(ACCESS_COOKIE_NAME, out string? cookieToken) &&
                !string.IsNullOrWhiteSpace(cookieToken))
            {
                token = cookieToken;
            }
            else
            {
                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) &&
                    authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("ARM 拒絕存取: 未提供合法憑證");
                await HandleUnauthorizedAsync(context, HttpStatusCode.Unauthorized, "未登入或憑證已失效");
                return;
            }

            // 走道這裡表示確認有 token ，
            // 驗證 token 是否有效並取得對某 action 的存取權限
            var authResult = await GetOrVerifyAccessAsync(context, token, controllerName, actionName);

            // 把外部驗證結果橋接進框架原生身分系統
            // ArmAuthorizationMiddleware 手動把 ARMV2 驗證過的結果重新「灌」進 context.User，讓：
            // 1.下游的[Authorize] attribute
            // 2. Razor Page / View 裡的 User.Identity.Name
            // 3. Controller 裡的 User.FindFirst(ClaimTypes.NameIdentifier)
            // 可以正常的運作
            if (authResult.IsAllowed)
            {
                // 💡 [架構特徵：無縫接軌 .NET 核心身分驗證]
                // 建立標準的 ClaimsPrincipal，讓 MVC 原生的 [Authorize] 與 User.Identity 完美運作！
                var claims = new[] {
                    new Claim(ClaimTypes.NameIdentifier, authResult.UserId ?? ""),
                    // 💡 修正：直接將 UserId 作為 Name Claim 寫入，移除已不存在的 UserName
                    new Claim(ClaimTypes.Name, authResult.UserId ?? "") 
                };
                // ArmAuth 是認證的種類 AuthenticationType = "ArmAuth"
                var identity = new ClaimsIdentity(claims, "ArmAuth");
                context.User = new ClaimsPrincipal(identity);

                // 為了向下相容保留原本的寫法
                context.Items["UserId"] = authResult.UserId;

                await _next(context);
            }
            else
            {
                _logger.LogWarning("ARM 拒絕存取: System={System}, Controller={Ctrl}, Action={Act}, Reason={Reason}",
                    _options.SystemId, controllerName, actionName, authResult.ErrorMessage);

                await HandleUnauthorizedAsync(context, HttpStatusCode.Forbidden,
                    authResult.ErrorMessage ?? "角色權限不足，拒絕操作");
            }
        }

        /// <summary>
        /// 💡 智慧回應分流 (Smart Response Routing)
        /// 根據請求來源，精準回傳 302 網頁轉址或 401/403 JSON 格式
        /// </summary>
        private async Task HandleUnauthorizedAsync(HttpContext context, HttpStatusCode statusCode, string message)
        {
            var request = context.Request;

            // 現代化的精準判定：判斷是否為前端 AJAX / Fetch 呼叫
            bool isAjax = request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
                          request.Headers["Sec-Fetch-Mode"] == "cors";

            // 判斷是否預期接收 HTML (傳統瀏覽器網址列換頁)
            bool acceptsHtml = request.Headers["Accept"].ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

            // 💡 [架構特徵：跨域轉址防禦]
            // 利用設定檔中的 認證中心ApiUrl 組合出絕對路徑 (Absolute URL)，
            // 確保無論 MRA 系統在哪個 Port 或網域運行，都能精準跳轉到正確的 ARM 登入中心。
            var targetPath = request.PathBase + request.Path + request.QueryString;
            var encodedReturnUrl = Uri.EscapeDataString(targetPath);
            string absoluteLoginUrl = $"{_options.ArmV2ApiUrl.TrimEnd('/')}/Account/Login?returnUrl={encodedReturnUrl}";

            // 💡 [架構分流] 403 Forbidden = 有身分但沒有權限 → 顯示提示頁，不直接跳登入
            //               401 Unauthorized = 根本沒有身分  → 直接跳登入頁
            bool isForbidden = statusCode == HttpStatusCode.Forbidden;

            if (!isAjax && acceptsHtml)
            {
                if (isForbidden)
                {
                    // 💡 有身分但無此功能權限：導向 MRA 的 ARMError 提示頁
                    // ARMError.cshtml 顯示錯誤訊息，使用者點確認後再導回登入頁
                    string pathBase = request.PathBase.ToString().TrimEnd('/');
                    var encodedMsg = Uri.EscapeDataString(message);
                    var encodedLoginUrl = Uri.EscapeDataString(absoluteLoginUrl);
                    string forbiddenUrl = $"{pathBase}/Home/ARMError?errorMsg={encodedMsg}&returnUrl={encodedLoginUrl}";
                    context.Response.Redirect(forbiddenUrl);
                }
                else
                {
                    // 傳統瀏覽器輸入網址，或是點擊 <a> 連結換頁，給予 302 絕對轉址
                    context.Response.Redirect(absoluteLoginUrl);
                }
            }
            else
            {
                // API 測試工具 (Bruno) 或前端 Fetch 呼叫，給予純粹的 JSON
                // 在 JSON 附上絕對的 RedirectUrl，讓前端開發者可以在 axios interceptor 中統一處理跳轉
                context.Response.StatusCode = (int)statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    Error = message,
                    RedirectUrl = isForbidden ? null : (object)absoluteLoginUrl
                });
            }
        }

        private async Task<PermissionVerifyResult> GetOrVerifyAccessAsync(HttpContext context, string token, string controller, string action)
        {
            // 如果 cache 失效，直接 call ArmV2 驗證
            if (_options.CacheTtlSeconds <= 0)
            {
                return await VerifyWithArmV2ServicesAsync(token, controller, action);
            }

            // 解 JWT 獲取 userId
            string? userId = GetUserIdFromToken(token);
            string cacheKey = $"arm:auth:{userId}:{_options.SystemId}:{controller}:{action}";

            // 先查正向、再查負向（也可以反過來，看覺得哪種比較常命中）
            if (_positiveCache.TryGetValue(cacheKey, out PermissionVerifyResult? cachedPositive) && cachedPositive != null)
            {
                return cachedPositive;
            }

            if (_negativeCache.TryGetValue(cacheKey, out PermissionVerifyResult? cachedNegative) && cachedNegative != null)
            {
                return cachedNegative;
            }
            
            // cache 沒有查到，可能是第一次登入，直接查 ArmV2
            var result = await VerifyWithArmV2ServicesAsync(token, controller, action);

            // result 不是 null 就是有值
            if (result != null)
            {
                // 如果不是 null 就分有通過跟沒通過
                if (result.IsAllowed)
                {
                    var options = new MemoryCacheEntryOptions
                    {
                        Size = 1, // 👈 這筆資料算「1 個單位」
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.CacheTtlSeconds),
                        Priority = Microsoft.Extensions.Caching.Memory.CacheItemPriority.Normal
                    };
                    _positiveCache.Set(cacheKey, result, options);
                }
                else
                {
                    int negativeCacheTtl = Math.Min(3, _options.CacheTtlSeconds);
                    var options = new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(negativeCacheTtl),
                        Priority = Microsoft.Extensions.Caching.Memory.CacheItemPriority.Low
                    };
                    _negativeCache.Set(cacheKey, result, options);
                }
            }
            // 走到這裡 result 有三種情況，
            // result null，回傳驗證服務無結果
            // result 有通過，存入 positive cache，回傳有通過認證
            // result 沒通過，存入 negative cache，回傳沒通過認證
            return result ?? new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "驗證服務未回傳有效結果" };
        }

        private string? GetUserIdFromToken(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();

                if (!handler.CanReadToken(token))
                {
                    _logger.LogWarning("ARM Token 格式無法解析 (非合法 JWT)");
                    return null;
                }

                var jwtToken = handler.ReadJwtToken(token);

                // 依實際 ARM 簽發的 Token 內容調整 claim type，
                // 常見的有 sub、nameid、ClaimTypes.NameIdentifier 等
                string? userId = jwtToken.Claims
                    .FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub
                                      || c.Type == ClaimTypes.NameIdentifier
                                      || c.Type == "nameid")
                    ?.Value;

                return userId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析 ARM Token 失敗");
                return null;
            }
        }

        private async Task<PermissionVerifyResult> VerifyWithArmV2ServicesAsync(
            string token,
            string controller,
            string action,
            CancellationToken cancellationToken = default)
        {
            var client = _httpClientFactory.CreateClient("ArmClient");

            // 💡 直接在這裡賦予單獨的 Timeout 時間 (例如 3 分鐘)
            // 這樣就不需要去 Program.cs 註冊了，邏輯高度內聚！
            client.Timeout = TimeSpan.FromMinutes(3);

            string verifyApiUrl = $"{_options.ArmV2ApiUrl.TrimEnd('/')}/api/v1/Auth/Verify";

            var requestPayload = new
            {
                Token = token,
                SystemId = _options.SystemId,
                ControllerName = controller,
                ActionName = action
            };

            try
            {
                // 記得傳入 cancellationToken，讓使用者關閉瀏覽器時也能提早中斷
                var response = await client.PostAsJsonAsync(verifyApiUrl, requestPayload, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<PermissionVerifyResult>(cancellationToken: cancellationToken);
                    return result ?? new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "無效的回傳格式" };
                }

                _logger.LogWarning("ARM 中心拒絕授權: HTTP {StatusCode}", response.StatusCode);
                return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "ARM 授權中心拒絕存取" };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("呼叫 ARM 授權中心已被取消 (可能是逾時或前端已斷線)。");
                return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "授權服務連線逾時" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "呼叫 ARM 授權中心發生未預期失敗。");
                return new PermissionVerifyResult { IsAllowed = false, ErrorMessage = "授權服務暫時無法連線" };
            }
        }
    }
    
}