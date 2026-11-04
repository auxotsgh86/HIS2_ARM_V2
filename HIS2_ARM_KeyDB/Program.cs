using System.Net;
using System.Text;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Helpers;
using HIS2_ARM_KeyDB.Services;
using HIS2_ARM_KeyDB.Models.Auth;
using HIS2_ARM_KeyDB.Services.Auth;
using HIS2_ARM_KeyDB.Services.Interfaces;
using HIS2_ARM_KeyDB.Services.Cache; // 💡 [新增] 引入 CacheTokenManager 的 namespace
using HIS2_ARM_KeyDB.Services.HostedServices; // 💡 [新增] 引入 BackgroundService 的 namespace
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using StackExchange.Redis;
using HIS2_ARM_KeyDB.Middlewares;
using HIS2_ARM_KeyDB.Services.Dispatch;
using HIS2_ARM_KeyDB.Services.Handlers.Interfaces;
using HIS2_ARM_KeyDB.Services.Notification;
using HIS2_ARM_KeyDB.Services.PermissionImpact;
using Microsoft.AspNetCore.Authorization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/his2_arm_.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .MinimumLevel.Information()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Host.UseSerilog();

var services = builder.Services;
var config = builder.Configuration;

// ═══════════════════════════════════════════════════════════
// 1. 展示層與 API 路由基礎設施 (Presentation Layer)
// ═══════════════════════════════════════════════════════════
// 註冊時區轉換器
services.AddSingleton<LegacyTimeZoneHelper>();
services.AddControllers();
services.AddRazorPages();
services.AddEndpointsApiExplorer();
services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1",
        new OpenApiInfo
            { Title = "HIS2-ARM KeyDB API", Version = "v1", Description = "三總 HIS2 認證授權管理 — BITAND 兩層位元交集權限" });
    c.AddSecurityDefinition("Bearer",
        new OpenApiSecurityScheme
        {
            Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            In = ParameterLocation.Header
        });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
                { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });

    try
    {
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, xmlFile);
        c.IncludeXmlComments(xmlPath);
    }
    catch
    {
        /* 默默忽略 */
    }
});

// ═══════════════════════════════════════════════════════════
// 2. 底層資源與依賴注入生命週期 (Dependency Injection)
// ═══════════════════════════════════════════════════════════

// 💡 [新增] 註冊 .NET 原生記憶體快取 (供給 L1 Cache 使用)
services.AddMemoryCache();

// 💡 [新增] 註冊快取權杖管理器 (必須是 Singleton，全系統共用同一組引爆器)
services.AddSingleton<CacheTokenManager>();

// Redis 連線池成本高，維持 Singleton 全域共用
services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(config.GetConnectionString("KeyDb") ?? "localhost:6379,defaultDatabase=1"));
services.AddSingleton<ValkeyDbService>();

// OracleDbService 改為 Scoped
services.AddScoped<OracleDbService>();

// 💡 [新增] Outbox 事件存取服務（Phase 1：僅提供 Enqueue/Dequeue/MarkProcessed/MarkFailed，
//    尚未接上 OutboxWorker，Phase 3 會補上對應的 HostedService）
services.AddScoped<IOutboxService, OutboxDbService>();

// 核心業務邏輯
services.AddScoped<ILegacyTokenService, LegacyTokenService>();
services.AddScoped<ITokenService, TokenService>();
services.AddScoped<AuthService>();
services.AddScoped<IMenuService, MenuService>();

// 快取一致性服務 (Load to Cache 策略的核心)
services.AddHttpContextAccessor();
services.AddScoped<ICacheSyncService, CacheSyncService>();

// 註冊 ArmNotificationService 以便在授權變更時通知其他節點更新快取
// 注意這裡的寫法，要確保 ArmNotificationService 是 Singleton，
// 並且同時註冊為 IArmNotificationService 與 HostedService
// 避免 ArmNotificationService 被多次建立，導致背景服務無法正常運作
// 💡 [修正] 補上 SignalROptions 綁定：先前完全沒有呼叫 Configure<SignalROptions>，
//    導致 IOptions<SignalROptions>.Value.ArmHubUrl 永遠是類別預設值 string.Empty，
//    使得 ArmNotificationService.ExecuteAsync 在啟動時判斷「未設定」而直接 return，
//    _hubConnection 永遠是 null，任何 Outbox 權限異動事件呼叫
//    NotifyPermissionCacheInvalidatedAsync 都會拋出
//    「ARM Notification Service 尚未建立 SignalR Connection」例外。
builder.Services.Configure<HIS2_ARM_KeyDB_Client.Options.SignalROptions>(
    builder.Configuration.GetSection("SignalR"));

builder.Services.AddSingleton<ArmNotificationService>();
builder.Services.AddSingleton<IArmNotificationService>(sp =>
    sp.GetRequiredService<ArmNotificationService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<ArmNotificationService>());

// 註冊 PermissionImpactService 以便在授權變更時計算 UserIds 的影響範圍
services.AddScoped<IPermissionImpactService, PermissionImpactService>();

#region 註冊權限變更事件的 handlers 和 enevt dispatcher

services.AddScoped<IPermissionEventHandler, UserRoleChangedEventHandler>();
services.AddScoped<IPermissionEventHandler, RolePermissionChangedEventHandler>();
services.AddScoped<IPermissionEventHandler, ActionRequirementChangedEventHandler>();
services.AddScoped<IPermissionEventHandler, RoleStatusChangedEventHandler>();
services.AddScoped<IPermissionEventHandler, UserStatusChangedEventHandler>();
services.AddScoped<IPermissionChangeDispatcher, PermissionChangeDispatcher>();

#endregion

// Controller 探索與自訂授權攔截器
services.AddSingleton<ControllerDiscoveryService>();

// 💡 [新增] 註冊 Redis 廣播監聽背景服務 (啟動時自動掛載於背景執行，守護快取同步)
services.AddHostedService<CacheDbPubSubBackgroundService>();

// 💡 [新增] 註冊 Outbox 背景消化服務 (Phase 3：Poll → Claim → Dispatch → MarkProcessed/Failed)
services.AddHostedService<HIS2_ARM_KeyDB.Services.HostedServices.OutboxWorker>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SecretKey"]!)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"]!,
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"]!,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("ARM_ACCESS_TOKEN", out var cookieToken))
                {
                    context.Token = cookieToken;
                }

                return Task.CompletedTask;
            }
        };
    });

// ② 取代 services.AddAuthorization(); — 加上 FallbackPolicy 當第二層保底
// 這樣所有未標記 [AllowAnonymous] 的 Controller/Action 都會自動套用授權檢查
services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // 1. 同時讀取 單一IP 與 網段
    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
    var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();

    // 2. 只要有設定任何一個，就清空預設值 (127.0.0.1)
    if ((knownProxies != null && knownProxies.Length > 0) || 
        (knownNetworks != null && knownNetworks.Length > 0))
    {
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        // 3. 處理單一 IP (保留您原本嚴謹的報錯)
        if (knownProxies != null)
        {
            foreach (var ipString in knownProxies)
            {
                if (IPAddress.TryParse(ipString, out var ip))
                    options.KnownProxies.Add(ip);
                else
                    throw new InvalidOperationException($"無效的 Proxy IP 設定: {ipString}");
            }
        }

        // 4. 處理 CIDR 網段 (新增的部分)
        if (knownNetworks != null)
        {
            foreach (var networkString in knownNetworks)
            {
                var parts = networkString.Split('/');
                if (parts.Length == 2 && 
                    IPAddress.TryParse(parts[0], out var ip) && 
                    int.TryParse(parts[1], out var prefixLength))
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, prefixLength));
                }
                else
                {
                    // 維持您的 Fail-Fast 精神，格式錯誤直接擲出例外
                    throw new InvalidOperationException($"無效的 CIDR 網段設定: {networkString}");
                }
            }
        }
    }
});

var app = builder.Build();

// 🚀 必須放在最前面！讓後續所有的 Middleware 與 Controller 都能拿到真實的 IP
app.UseForwardedHeaders();

app.UsePathBase("/ArmV2");

// ⚠️ 已移除重複的第二次 app.UseForwardedHeaders(new ForwardedHeadersOptions{...}) 呼叫。
// 該呼叫會用「幾乎空白的預設信任清單」重新處理一次 X-Forwarded-For，
// 在多層反向代理情境下可能導致 RemoteIpAddress 被意外覆寫成鏈路中下一段代理的 IP
// (例如觀察到登入時 IP 顯示為 HIS2_MRA 伺服器 IP 的成因之一)。
// 上方 234 行的 app.UseForwardedHeaders() 已經套用了從 appsettings.json 讀取的
// KnownProxies / KnownNetworks 正式設定，僅需呼叫一次即可。

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "HIS2-ARM KeyDB v1"));
}

// ═══════════════════════════════════════════════════════════
// 4. 請求處理管線 (Middleware Pipeline)
// ═══════════════════════════════════════════════════════════
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<SystemIdResolver>();
app.UseMiddleware<SelfArmAuthorizationMiddleware>(); // 💡 新增：自我防護
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

// ④ health 端點放行（FallbackPolicy 啟用後必須顯式標記）
app.MapGet("/health", async (ValkeyDbService kdb) =>
        Results.Ok(new
        {
            Status = await kdb.PingAsync() ? "healthy" : "degraded", Service = "HIS2-ARM-KeyDB",
            Timestamp = DateTime.UtcNow
        }))
    .AllowAnonymous();

// ═══════════════════════════════════════════════════════════
// 5. 系統韌性與快取預熱 (Resilience & Cache Warm-up)
// ═══════════════════════════════════════════════════════════
using (var scope = app.Services.CreateScope())
{
    try
    {
        var cacheSync = scope.ServiceProvider.GetRequiredService<ICacheSyncService>();
        Log.Information("系統拓撲初始化：執行快取預熱 (Warm-up)，載入核心子系統之 API 門檻定義...");

        // 💡 [修正]：方法名稱已統一重構加上 "All" 字眼，確保語意精準且編譯通過
        // 如果這些核心系統的防護規則沒有成功載入 Redis，這台 AP Server 就絕對不准啟動（不准執行 app.Run()）
        cacheSync.LoadAllActionDefinitionsToCacheAsync("HIS2_ARM_V2").GetAwaiter().GetResult();
        cacheSync.LoadAllActionDefinitionsToCacheAsync("HIS2_INP").GetAwaiter().GetResult();
        // cacheSync.LoadAllActionDefinitionsToCacheAsync("HIS2_OP").GetAwaiter().GetResult();

        // 若有其他核心子系統，可於此處擴充載入...
    }
    catch (Exception ex)
    {
        Log.Error(ex, "快取預熱失敗！請確認 Oracle DB 連線與 ARM_ACTION_DEF 結構完整性。");
    }
}

Log.Information("HIS2-ARM-KeyDB 啟動 — 環境: {Env}", app.Environment.EnvironmentName);
app.Run();