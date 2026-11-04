using System.Net.Http.Json;
using HIS2_ARM_KeyDB_Client.Constants;
using HIS2_ARM_KeyDB_Client.Models.Menu;
using HIS2_ARM_KeyDB_Client.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace HIS2_ARM_KeyDB_Client.Services;

public sealed class MenuCacheService : IMenuCacheService
{
    private const string CacheKeyPrefix = "menu:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;

    public MenuCacheService(
        [FromKeyedServices(ArmCacheKeys.MenuCache)] IMemoryCache cache,
        IHttpClientFactory httpClientFactory)
    {
        _cache = cache;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<MenuViewModel>> GetMenuAsync(
        string userId, string cookieHeader, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(userId);

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            var armClient = _httpClientFactory.CreateClient("ARM");
            using var armRequest = new HttpRequestMessage(HttpMethod.Get, "api/system/His2Menu");
            armRequest.Headers.Add("Cookie", cookieHeader);

            var armResponse = await armClient.SendAsync(armRequest, cancellationToken);
            armResponse.EnsureSuccessStatusCode();

            return await armResponse.Content
                .ReadFromJsonAsync<List<MenuViewModel>>(cancellationToken: cancellationToken) ?? [];
        }) ?? [];
    }

    public void Invalidate(string userId) => _cache.Remove(BuildCacheKey(userId));

    private static string BuildCacheKey(string userId) => $"{CacheKeyPrefix}{userId}";
}