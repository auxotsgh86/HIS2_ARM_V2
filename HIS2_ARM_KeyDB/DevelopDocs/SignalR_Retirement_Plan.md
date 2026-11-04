# SignalR 退場與權限版本同步計畫

> 狀態：設計／盤點文件  
> 範圍：HIS2_ARM_KeyDB 與 HIS2_ARM_KeyDB_Client  
> 原則：先讓權限快取正確性不依賴推播，再移除 SignalR；不重做既有 Outbox / Dispatcher / Impact 架構。

---

## 1. 結論

本專案其實已經定義過不依賴推播送達的機制：**Valkey 的 `perm_ver:{userId}` 權限版本**。

既有文件：

- `HIS2_ARM_KeyDB/DevelopDocs/HIS2_ARM_Outbox_Architecture.md`
  - §15：權限版本是本機快取正確性依據。
  - §16：Pub/Sub 與 SignalR 僅為加速器，可丟失。
  - §17：WebAPI L0 快取應統一由 `L0AuthCacheService` 管理，MenuCache 同樣比對版本。
  - §18：ARM L1 快取也應記錄並比對版本。
- `HIS2_ARM_KeyDB/DevelopDocs/Permission_Cache_Invalidation_Architecture.md`
  - 定義 Event、Dispatcher、Handler、ImpactService 與 NotificationService 的責任分離。

因此，SignalR 退場不需要再設計一套新的通知架構，也不應新增另一個平行的 PermissionVersion 概念。應落實既有的 `perm_ver` 設計。

## 2. 目前程式碼盤點

依目前 Repository 的程式碼搜尋結果：

| 項目 | 現況 | 判斷 |
|---|---|---|
| Oracle Outbox | 已有寫入與背景 Worker | 保留 |
| Permission Dispatcher / Handlers | 已有五種事件處理流程 | 保留 |
| PermissionImpactService | 已集中計算受影響 UserIds | 保留 |
| Valkey `perm_ver:{userId}` | 在架構文件有定義，程式碼尚未找到讀寫實作 | 必須補齊 |
| L0AuthCacheService | 已存在，但 Middleware 尚未統一透過它讀寫 | 必須整合 |
| ArmAuthorizationMiddleware | 目前自行操作 keyed IMemoryCache | 必須重構 |
| MenuCacheService | 目前以 UserId + 5 分鐘 TTL 快取，尚未比對權限版本 | 必須重構 |
| ArmNotificationObserver | 透過 SignalR 清除 L0 Auth / Menu Cache | 過渡期保留 |
| ArmNotificationService | 透過 SignalR 發送失效事件 | 過渡期保留 |

重要：目前程式碼搜尋只在開發文件中找到 `perm_ver`，尚未找到 Valkey 版本讀寫的實際實作。因此不能現在就直接移除 SignalR。

## 3. 目標架構

權限異動端維持原本 Outbox 流程：

```text
Oracle Transaction
  ├─ 更新權限資料
  └─ 寫入 Outbox
          │
          ▼
      OutboxWorker
          │
          ▼
  Permission Dispatcher
          │
          ▼
       Event Handler
          ├─ 更新／刪除共享 Valkey 權限快取
          ├─ PermissionImpactService 計算受影響 UserIds
          └─ INCR perm_ver:{userId}
```

讀取端不依賴推播：

```text
使用者請求
    │
    ▼
取得 UserId
    │
    ▼
本機快取命中？
    ├─ 否 → 重新驗證，記錄當前 perm_ver
    │
    └─ 是 → 讀取 Valkey perm_ver
                 │
                 ├─ 相同 → 使用本機快取
                 └─ 不同 → 丟棄舊快取並重新驗證
```

Pub/Sub 與 SignalR 在過渡期可以保留，但只作為提前清除本機快取的最佳化。即使完全沒有推播，版本比對仍須保證下一次請求不會使用舊的授權快取。

## 4. 實作階段

### Phase 1：完成 Valkey 權限版本 API

在 Valkey 存取層新增明確方法：

- `GetPermissionVersionAsync(userId)`
- `IncrementPermissionVersionAsync(userId)`

設計要求：

1. Key 固定為 `perm_ver:{userId}`，避免不同服務自行拼接。
2. Key 不存在視為版本 0。
3. 版本遞增必須發生在共享權限快取更新成功之後。
4. 重複處理 Outbox 事件允許版本多次遞增，讀取端只比較相等與否。
5. 版本查詢失敗時，受保護的授權快取不得退回使用未確認版本的舊資料。

### Phase 2：接通 Outbox Handler

所有會影響使用者有效權限的 Handler，在共享 Valkey Key 更新成功、且已取得受影響 UserIds 後，逐一遞增版本。

順序：

1. 更新或刪除共享權限快取。
2. 計算受影響 UserIds。
3. 遞增這些 User 的 `perm_ver`。
4. （過渡期）發送 Pub/Sub／SignalR 加速通知。

若版本遞增失敗，Outbox 事件不得標記為完成，必須依既有重試機制重試。所有步驟需具備 At-Least-Once 下的冪等性。

### Phase 3：統一 L0 Auth Cache

目前 Middleware 直接操作 keyed `IMemoryCache`，而 `L0AuthCacheService.Invalidate(userId)` 管理自己的 key index，兩者未共用同一套條目管理。

應先統一讀寫責任：

- Middleware 只透過 `IL0AuthCacheService` 讀寫。
- Cache entry 必須保存建立時的 PermissionVersion。
- Cache 命中時比對 Valkey 最新版本。
- 版本不符時丟棄舊條目並重新呼叫 ARM Verify API。
- 版本查詢不可用時採 fail-closed，不可沿用未確認版本的 Positive Cache。

完成後，SignalR 的 Auth Cache 清除只剩效能用途，不再承擔正確性。

### Phase 4：MenuCache 與 JWT 角色新鮮度

MenuCache 必須使用同一個 `perm_ver:{userId}`：

- 快取項目記錄 PermissionVersion。
- 取得選單時檢查版本。
- 版本不符時清除舊選單並重新取得。
- 使用者 Token 必須以明確的 Bearer Token 傳給 `ArmVerifyClient`，不得轉送整個 Cookie Header，也不得使用未註冊的 HttpClient 名稱。

**必須區分「角色權限內容異動」與「使用者角色集合異動」，兩者對 JWT 的依賴不同。**

目前 `MenuService.GetUserMenuTreeAsync()` 的實際流程是：

1. 從 JWT 的 `ClaimTypes.Role` 取得 RoleIds。
2. 使用這些 RoleIds 查詢 Oracle 目前的 `ARM_PERMISSIONS` 資料。
3. 依目前資料中的有效狀態與 Read Bit 組出選單。

因此：

- **RolePermissionChanged（既有角色的功能權限異動）**：JWT 中的 RoleIds 不需要改變。只要重新呼叫 Menu API，SQL 就會讀取 Oracle 已提交的最新 `ARM_PERMISSIONS`，選單結果就是最新權限。單純修改角色的 PERMS_BITS，不需要為此刷新 Token。
- **UserRoleChanged（使用者新增／移除角色）**：JWT 中的 RoleIds 是簽發時快照。舊 Token 不會自動包含新角色，也不會自動移除舊角色。此情境才需要刷新 Token，或讓 Menu API 依已驗證的 UserId 從權威資料來源取得最新有效 RoleIds。
- **RoleStatusChanged（角色啟用／停用）**：目前選單 SQL 沒有 JOIN `ARM_ROLES` 檢查角色的 `IS_ACTIVE`。應確認停用角色不會因舊 JWT Role Claim 而繼續貢獻選單；可在選單查詢中排除停用角色，或由服務端先取得最新有效 RoleIds。
- **ActionRequirementChanged（API 門檻異動）**：目前選單 SQL 主要依 `ARM_PERMISSIONS` 的 Read Bit 判斷顯示，沒有直接查詢 `ARM_ACTION_DEF`。因此 Action 門檻異動是否改變選單呈現，須依實際 UI 規則決定；它仍可能需要使授權快取失效，但不能假設一定改變選單內容。

另外，以上「重新呼叫 Menu API」的前提是確實打到 API。子系統的 `MenuCacheService` 目前另有以 UserId 為 Key、TTL 5 分鐘的本機快取；快取命中時不會呼叫 Menu API。因此移除 SignalR 後，仍須讓 MenuCache 透過 `perm_ver:{userId}` 察覺相關異動，才能在下一次取用時丟棄舊選單並重新呼叫 API。

實作時不要把所有權限異動都當成 JWT RoleIds 過期。應只有角色集合／角色有效狀態來源改變時，才處理 Role Claims 的新鮮度；角色權限內容異動則由重新查詢 Oracle 的權限表反映。

### Phase 5：ARM L1 Cache 版本比對

依既有 Outbox 架構文件 §18：

- ARM AuthService 的 L1 Cache Entry 記錄 PermissionVersion。
- 命中時比對 Valkey 最新版本。
- 版本不符時丟棄並重新計算授權。
- 原有 roleHash 可保留，用於角色集合變動時區分 Cache Key。

### Phase 6：移除 SignalR 與非必要推播

只有在以下條件全部滿足後，才移除 SignalR：

- [ ] 所有權限異動 Handler 都會遞增受影響使用者的版本。
- [ ] L0 Auth Cache 命中時會比對版本。
- [ ] MenuCache 命中時會比對版本。
- [ ] ARM L1 Cache 命中時會比對版本。
- [ ] Valkey 版本查詢失敗時，受保護請求確實 fail-closed。
- [ ] 權限異動後，即使節點漏收任何通知，下一次請求仍不會使用舊授權。
- [ ] UserRoleChanged 的角色集合變動已處理 JWT Role Claims 新鮮度。
- [ ] RoleStatusChanged 不會讓停用角色透過舊 JWT Role Claim 繼續顯示選單。
- [ ] 多節點測試通過：節點 A 寫入權限異動，節點 B 不接收任何推播，仍能在下一次請求察覺版本變更。

完成後才移除：

- ARM 端 `ArmNotificationService` 與其 SignalR HostedService 註冊。
- Client 端 `ArmNotificationObserver` 與 SignalR Client 套件／設定。
- `SignalROptions`、ArmHub URL 設定及相關 DI 註冊。
- 不再使用的 Notification Event Model 與介面（確認無其他消費者後）。

## 5. 效能考量

版本檢查不是每次都重新呼叫完整授權 API，而是本機快取命中時，增加一次輕量的 Valkey 版本讀取。

建議：

1. 若目前 Session 驗證已讀取 Valkey，可將 PermissionVersion 與 Session 資料整合在同一次 Redis pipeline，避免增加網路往返。
2. 不要為了降低版本查詢而在本機長時間快取版本，否則會重新引入權限異動延遲。
3. 選單更新可以在頁面載入、重新取得焦點或明確刷新時進行；但後端授權必須在每次快取命中時確認版本。
4. TTL 僅作為記憶體回收機制，不再是權限新鮮度的主要保證。

## 6. 驗收測試

| 情境 | 預期結果 |
|---|---|
| 使用者角色新增／移除 | 下一次受保護請求偵測版本變更並重驗 |
| 角色權限異動 | 所有受影響使用者版本遞增 |
| Action Required Bits 異動 | 受影響使用者版本遞增 |
| 角色停用 | 相關使用者版本遞增，重新驗證時不再使用停用角色 |
| User 停用 | Session 撤銷且版本遞增 |
| SignalR 斷線或完全停用 | 不影響權限正確性 |
| WebAPI 節點重啟 | 本機快取重建時取得最新版本 |
| Valkey 無法讀取版本 | 受保護請求 fail-closed |
| 既有 Role 的 PERMS_BITS 異動 | 重新呼叫 Menu API 即讀取 Oracle 最新 ARM_PERMISSIONS；不因這種異動單獨刷新 Token |
| UserRoleChanged 後舊 Access Token 仍帶舊 RoleIds | 必須更新 Token，或由 Menu API 依 UserId 取得最新有效角色 |
| RoleStatusChanged 後舊 JWT 仍含停用 RoleId | Menu API 必須排除停用角色，避免舊 Claim 讓選單殘留 |

---

## 7. 最終原則

- Outbox 保證權限異動事件不遺失。
- Handler 更新共享權限快取並遞增使用者版本。
- PermissionVersion 保證本機快取不會無限期使用舊權限。
- SignalR／Pub/Sub 只負責加速，不負責正確性。
- Menu 是 UI 呈現；後端授權永遠是安全邊界。
