using System.Collections.Concurrent;
using HIS2_ARM_KeyDB_Client.Constants;
using HIS2_ARM_KeyDB_Client.Enums;
using HIS2_ARM_KeyDB_Client.Options;
using HIS2_ARM_KeyDB_Client.Services.Interfaces;
using HIS2.ARM.Client.Middlewares;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HIS2_ARM_KeyDB_Client.Services;

/// <summary>
/// L0 Authorization Cache。
///
/// 使用 Process-local IMemoryCache 保存 User 對 Action 的權限結果。
///
/// Cache Key:
/// arm:auth:{userId}:{system}:{controller}:{action}
///
/// Cache Value:
/// CrudpPermissions
///
/// Positive Cache:
/// permissions != None
///
/// Negative Cache:
/// permissions == None
/// </summary>
public class L0AuthCacheService : IL0AuthCacheService
{
    private const string CacheKeyPrefix = "arm:auth:";

    private readonly IMemoryCache _positiveCache;
    private readonly IMemoryCache _negativeCache;
    private readonly ArmClientOptions _options;

    /// <summary>
    /// Cache Key Index。
    ///
    /// IMemoryCache 本身沒有提供依 Prefix 查詢 Key 的能力，
    /// 因此額外維護一份 Index，供 Invalidate(UserId) 使用。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _keyIndex = new();

    public L0AuthCacheService(
        [FromKeyedServices(ArmCacheKeys.AuthVerifyPositive)]
        IMemoryCache positiveCache,

        [FromKeyedServices(ArmCacheKeys.AuthVerifyNegative)]
        IMemoryCache negativeCache,

        IOptions<ArmClientOptions> options)
    {
        _positiveCache = positiveCache;
        _negativeCache = negativeCache;
        _options = options.Value;
    }

    /// <summary>
    /// CacheTtlSeconds > 0 才啟用 L0 Cache。
    /// </summary>
    private bool IsCacheEnabled =>
        _options.CacheTtlSeconds > 0;

    /// <summary>
    /// 建立統一的 Authorization Cache Key。
    /// </summary>
    private static string BuildCacheKey(
        string userId,
        string system,
        string controller,
        string action)
    {
        return $"{CacheKeyPrefix}{userId}:{system}:{controller}:{action}";
    }

    /// <summary>
    /// 建立 Cache Entry Options。
    /// </summary>
    private MemoryCacheEntryOptions CreateCacheOptions(
        TimeSpan ttl,
        CacheItemPriority priority)
    {
        var options = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = ttl,
            Priority = priority
        };

        options.RegisterPostEvictionCallback(
            (key, _, _, state) =>
            {
                var service =
                    (L0AuthCacheService)state!;

                service.RemoveKeyFromIndexIfCacheMissing(
                    key.ToString());
            },
            this);

        return options;
    }
    
    private void RemoveKeyFromIndexIfCacheMissing(string key)
    {
        if (_positiveCache.TryGetValue(key, out _))
        {
            return;
        }

        if (_negativeCache.TryGetValue(key, out _))
        {
            return;
        }

        _keyIndex.TryRemove(key, out _);
    }
    
    private void RemoveCacheEntry(string key)
    {
        _positiveCache.Remove(key);
        _negativeCache.Remove(key);

        _keyIndex.TryRemove(key, out _);
    }
    
    private MemoryCacheEntryOptions CreatePositiveCacheOptions()
    {
        return CreateCacheOptions(
            TimeSpan.FromSeconds(_options.CacheTtlSeconds),
            CacheItemPriority.Normal);
    }

    private MemoryCacheEntryOptions CreateNegativeCacheOptions()
    {
        var negativeCacheTtl =
            Math.Min(3, _options.CacheTtlSeconds);

        return CreateCacheOptions(
            TimeSpan.FromSeconds(negativeCacheTtl),
            CacheItemPriority.Low);
    }
    
    /// <inheritdoc />
    public bool TryGet(
        string userId,
        string system,
        string controller,
        string action,
        out CrudpPermissions permissions)
    {
        permissions = CrudpPermissions.None;

        if (!IsCacheEnabled)
        {
            return false;
        }

        var cacheKey = BuildCacheKey(
            userId,
            system,
            controller,
            action);

        /*
         * Positive Cache
         */
        if (_positiveCache.TryGetValue(
                cacheKey,
                out CrudpPermissions positivePermissions))
        {
            permissions = positivePermissions;
            return true;
        }

        /*
         * Negative Cache
         *
         * 注意：
         * None 是一個有效的 Cache Value。
         * 因此不能只靠 permissions == None 判斷 Cache Miss。
         *
         * 必須靠 TryGetValue() 的 bool 判斷是否 Hit。
         */
        if (_negativeCache.TryGetValue(
                cacheKey,
                out CrudpPermissions negativePermissions))
        {
            permissions = negativePermissions;
            return true;
        }

        return false;
    }

    private void RemoveOtherCache(
        string cacheKey,
        bool positive)
    {
        if (positive)
        {
            _negativeCache.Remove(cacheKey);
        }
        else
        {
            _positiveCache.Remove(cacheKey);
        }
    }
    
    /// <inheritdoc />
    public void Set(
        string userId,
        string system,
        string controller,
        string action,
        CrudpPermissions permissions)
    {
        if (!IsCacheEnabled)
        {
            return;
        }

        var cacheKey = BuildCacheKey(
            userId,
            system,
            controller,
            action);

        var isPositive =
            permissions != CrudpPermissions.None;

        RemoveOtherCache(
            cacheKey,
            isPositive);

        if (isPositive)
        {
            _positiveCache.Set(
                cacheKey,
                permissions,
                CreatePositiveCacheOptions());
        }
        else
        {
            _negativeCache.Set(
                cacheKey,
                permissions,
                CreateNegativeCacheOptions());
        }

        _keyIndex.TryAdd(
            cacheKey,
            0);
    }

    /// <inheritdoc />
    public void Invalidate(string userId)
    {
        var prefix =
            $"{CacheKeyPrefix}{userId}:";

        /*
         * 先複製符合條件的 Key。
         *
         * 不直接對 ConcurrentDictionary.Keys
         * 進行修改，避免在列舉期間產生不必要的干擾。
         */
        var keys = _keyIndex.Keys
            .Where(key =>
                key.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
            .ToArray();

        foreach (var key in keys)
        {
            _positiveCache.Remove(key);
            _negativeCache.Remove(key);

            /*
             * PostEvictionCallback 也會執行這個動作。
             *
             * 這裡提前 Remove 沒問題，因為 TryRemove 是 Idempotent 的。
             */
            _keyIndex.TryRemove(
                key,
                out _);
        }
    }

    /// <inheritdoc />
    public void Invalidate(
        string userId,
        string system,
        string controller,
        string action)
    {
        var cacheKey = BuildCacheKey(
            userId,
            system,
            controller,
            action);

        _positiveCache.Remove(cacheKey);
        _negativeCache.Remove(cacheKey);

        _keyIndex.TryRemove(
            cacheKey,
            out _);
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        var keys =
            _keyIndex.Keys.ToArray();

        foreach (var key in keys)
        {
            _positiveCache.Remove(key);
            _negativeCache.Remove(key);

            _keyIndex.TryRemove(
                key,
                out _);
        }
    }
}