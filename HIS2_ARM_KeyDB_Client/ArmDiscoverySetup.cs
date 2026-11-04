using HIS2_ARM_KeyDB_Client.Handlers;
using HIS2_ARM_KeyDB_Client.Models;
using HIS2_ARM_KeyDB_Client.Options;
using HIS2_ARM_KeyDB_Client.Services.Interfaces;
// 💡 確保引入 Options
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace HIS2.ARM.Client
{
    // ═══════════════════════════════════════════════════════════
    // 1. 傳輸合約 (Models) (見 Models 資料夾)
    // ═══════════════════════════════════════════════════════════


    // ═══════════════════════════════════════════════════════════
    // 2. API 拓撲掃描器 (Scanner)
    // ═══════════════════════════════════════════════════════════
    public class ArmEndpointScanner : IArmEndpointScanner
    {
        private readonly IEnumerable<EndpointDataSource> _endpointDataSources;

        public ArmEndpointScanner(IEnumerable<EndpointDataSource> endpointDataSources)
        {
            _endpointDataSources = endpointDataSources;
        }

        public IEnumerable<ArmEndpointPayload> ScanEndpoints()
        {
            // 展開所有 EndpointDataSource（MVC/API/RazorPages 都會註冊自己的 DataSource）
            var allEndpoints = _endpointDataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .ToList();

            // 1. Controller (含 ApiController、傳統路由、屬性路由 全部涵蓋)
            var controllerPayloads = allEndpoints
                .Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>())
                .Where(d => d != null)
                .GroupBy(d => d!.ControllerName)
                .Select(group => new ArmEndpointPayload
                {
                    ControllerName = group.Key,
                    EndpointType = "Controller",
                    Actions = group.Select(d => d!.ActionName)
                        .Distinct()
                        .OrderBy(name => name)
                        .ToList()
                });

            // 2. Razor Pages：用 endpoint metadata 抓 PageActionDescriptor + HttpMethodMetadata
            var pagePayloads = allEndpoints
                .Select(e => new
                {
                    Endpoint = e,
                    Page = e.Metadata.GetMetadata<PageActionDescriptor>()
                })
                .Where(x => x.Page != null)
                .GroupBy(x => x.Page!.ViewEnginePath)
                .Select(group => new ArmEndpointPayload
                {
                    ControllerName = group.Key,
                    EndpointType = "Page",
                    Actions = group
                        .Select(x => x.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>())
                        .Where(m => m != null)
                        .SelectMany(m => m!.HttpMethods)
                        .Distinct()
                        .OrderBy(name => name)
                        .ToList()
                });

            return controllerPayloads.Concat(pagePayloads);
        }
    }


    // ═══════════════════════════════════════════════════════════
    // 3. 非同步註冊背景服務 (Hosted Service)
    // ═══════════════════════════════════════════════════════════
    public class ArmRegistrationHostedService : BackgroundService
    {
        private readonly IArmEndpointScanner _scanner;
        private readonly HttpClient _httpClient;
        private readonly ArmClientOptions _options;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ILogger<ArmRegistrationHostedService> _logger;

        public ArmRegistrationHostedService(
            IArmEndpointScanner scanner,
            IHttpClientFactory httpClientFactory,
            IOptions<ArmClientOptions> options,
            IHostApplicationLifetime appLifetime,
            ILogger<ArmRegistrationHostedService> logger)
        {
            _scanner = scanner;
            _httpClient = httpClientFactory.CreateClient("ArmClient");
            _options = options.Value;
            _appLifetime = appLifetime;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var appStartedTcs = new TaskCompletionSource();
            await using var registration = _appLifetime.ApplicationStarted.Register(() => appStartedTcs.SetResult());
            await appStartedTcs.Task;

            if (string.IsNullOrEmpty(_options.SystemId) || string.IsNullOrEmpty(_options.ArmV2ApiUrl))
            {
                _logger.LogWarning("[ARM Client] 未設定 SystemId 或 認證中心ApiUrl，跳過自動註冊。");
                return;
            }

            if (string.IsNullOrEmpty(_options.SystemPassword))
            {
                _logger.LogWarning("[ARM Client] 未設定 SystemPassword，跳過自動註冊。");
                return;
            }
            

            // ==========================================================
            // 🚀 階段 1：M2M 登入 ARM 中心，取得 HttpOnly Cookie 憑證
            //          只在「登入成功」時才會跳出迴圈，避免重複登入。
            // ==========================================================
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var loginPayload = new { userId = _options.SystemId, password = _options.SystemPassword };
                    var loginResponse =
                        await _httpClient.PostAsJsonAsync("api/v1/Auth/login", loginPayload, stoppingToken);

                    if (loginResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("[ARM Client] 系統帳號 ({SystemId}) 登入成功，已取得全域 Cookie 憑證，準備執行註冊。",
                            _options.SystemId);
                        break;
                    }

                    string errorMsg = await loginResponse.Content.ReadAsStringAsync(stoppingToken);
                    _logger.LogWarning("[ARM Client] 系統帳號 ({SystemId}) 登入 ARM 中心失敗，無法取得註冊憑證。原因: {Msg}。5秒後重試...",
                        _options.SystemId, errorMsg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[ARM Client] 無法連線至 ARM 中心進行登入 ({Message})。5秒後重試...", ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // ==========================================================
            // 🚀 階段 2：帶著 HttpOnly Cookie 執行拓撲註冊
            //          只針對「註冊」失敗重試，不會再重新登入。
            // ==========================================================
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var payload = _scanner.ScanEndpoints();

                    // 💡 架構優化：HttpClient 已設定 BaseAddress，此處直接使用相對路徑
                    var requestUrl = $"api/v1/Bootstrap/register-endpoints/{_options.SystemId}";

                    // HttpClient 會自動從剛剛的登入回應中把 Cookie 夾帶進這個 Request 中
                    var response = await _httpClient.PostAsJsonAsync(requestUrl, payload, stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("[ARM Client] 成功將 {Count} 個 Controller 的 API 拓撲註冊至 ARM 中心 ({SystemId})",
                            payload.Count(), _options.SystemId);
                        break;
                    }

                    _logger.LogWarning("[ARM Client] 註冊失敗，狀態碼: {StatusCode}。5秒後重試...", response.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[ARM Client] 無法連線至 ARM 中心進行註冊 ({Message})。5秒後重試...", ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 4. DI 容器擴充方法 (Extensions)
    // ═══════════════════════════════════════════════════════════
    public static class ArmClientServiceCollectionExtensions
    {
        public static IServiceCollection AddArmDiscoveryClient(
            this IServiceCollection services,
            Action<ArmClientOptions> configureOptions)
        {
            // 註冊強型別 Options
            services.AddOptions<ArmClientOptions>()
                .Configure(configureOptions);

            // 提前建立一個實體，以便在註冊 HttpClient 時能拿到設定的 URL
            // (在記憶體中建立一個完全空白的 ArmClientOptions)
            var options = new ArmClientOptions();
            // 執行從 Program.cs 傳進來的「設定規則 (configureOptions)」
            // 執行完畢後，這個 options 裡面就有資料了！
            configureOptions(options);
            services.AddSingleton<IArmEndpointScanner, ArmEndpointScanner>();

            services.AddHttpContextAccessor();
            services.AddTransient<ForwardedForHandler>();

            // 💡 關鍵修正：將 認證中心ApiUrl 設定為 HttpClient 的 BaseAddress
            // ⚠️ "ArmClient"：專供【系統層級 M2M 登入 / 拓撲註冊】使用，保留 Cookie 能力（BFF 登入必備）
            services.AddHttpClient("ArmClient", client =>
                {
                    if (!string.IsNullOrWhiteSpace(options.ArmV2ApiUrl))
                    {
                        // 確保 URL 結尾有斜線，這是 HttpClient BaseAddress 的標準規範
                        var baseUrl = options.ArmV2ApiUrl.TrimEnd('/') + "/";
                        client.BaseAddress = new Uri(baseUrl);
                    }
                })
                // 👈 掛上 ForwardedForHandler，讓輸出的封包都有 X-Forwarded-For Header
                .AddHttpMessageHandler<ForwardedForHandler>()
                .ConfigurePrimaryHttpMessageHandler(() =>
                {
                    // 賦予此 HttpClient 接收與夾帶 HttpOnly Cookie 的能力 (BFF 登入必備)
                    return new SocketsHttpHandler
                    {
                        CookieContainer = new CookieContainer(),
                        UseCookies = true,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                    };
                });

            // ⚠️ "ArmVerifyClient"：專供【每個使用者請求的權限驗證】使用，
            //    刻意關閉 Cookie 夾帶能力，避免跟系統層級的 M2M Cookie 共用同一個 CookieContainer，
            //    確保驗證請求只依賴 Body 裡明確傳入的使用者 Token，不會被系統身分污染。
            services.AddHttpClient("ArmVerifyClient", client =>
            {
                if (!string.IsNullOrWhiteSpace(options.ArmV2ApiUrl))
                {
                    var baseUrl = options.ArmV2ApiUrl.TrimEnd('/') + "/";
                    client.BaseAddress = new Uri(baseUrl);
                }
            }).ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new SocketsHttpHandler
                {
                    UseCookies = false, // 明確關閉 Cookie 自動夾帶
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                };
            });

            services.AddHostedService<ArmRegistrationHostedService>();

            return services;
        }
    }
}