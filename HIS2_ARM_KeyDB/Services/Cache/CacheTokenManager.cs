using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;

namespace HIS2_ARM_KeyDB.Services.Cache;

/// <summary>
/// 快取權杖管理器 (Singleton)
/// 負責管理分散式 L1 快取的「引信」(CancellationTokenSource)
/// 支援角色(Role)與功能(Action)的雙引信精準清除機制
/// </summary>
public class CacheTokenManager
{
    // 存放每個 Key (可以是 RoleId 或是 ActionKey) 對應的引爆器
    // 使用 OrdinalIgnoreCase 確保大小寫容錯
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取得指定 Key 的過期權杖 (若無則建立新的)
    /// </summary>
    /// <param name="key">例如 "MRA_DOCTOR" 或 "ACT:HIS2_MRA:Mram030.Index"</param>
    public IChangeToken GetChangeToken(string key)
    {
        // GetOrAdd 確保同一個 Key 永遠只會拿到同一個引爆器
        var cts = _tokens.GetOrAdd(key, _ => new CancellationTokenSource());
        return new CancellationChangeToken(cts.Token);
    }

    /// <summary>
    /// 引爆 (觸發) 指定 Key 的權杖，讓綁定此權杖的 L1 快取瞬間失效
    /// </summary>
    public void Evict(string key)
    {
        // 若字典中存在該引爆器，則將其取出並移除
        if (_tokens.TryRemove(key, out var cts))
        {
            try
            {
                // 🚀 觸發取消！所有綁定此引信的 MemoryCache 項目將被 .NET 底層瞬間回收
                cts.Cancel();  
            }
            finally
            {
                // 釋放非受控資源，避免記憶體洩漏
                cts.Dispose(); 
            }
        }
    }
}