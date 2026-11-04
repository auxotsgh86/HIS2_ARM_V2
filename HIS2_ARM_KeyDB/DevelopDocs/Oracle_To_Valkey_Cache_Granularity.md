# Oracle → Valkey(KeyDB) 快取重載粒度設計文件

## 文件目的

本文件定義 ARM 權限系統中，Oracle 端三種業務異動（Action 需求權限、Role 權限、User-Role 配置）
在透過 Outbox 事件驅動更新 Valkey/KeyDB 快取時，各自對應的**快取 Key 粒度**與**重載策略**，
確保「事件粒度」與「快取 Key 粒度」精準對齊，避免誤傷無關資料（Blast Radius）與不必要的效能浪費。

---

## 核心設計原則

> **Key 的粒度，應該等於你想要重載/刪除的最小單位。**

若 Redis/Valkey 的一把 Key 內混雜了多個「異動最小單位」不相關的資料，
重載時就必須做 field 級的精準過濾（複雜、易出錯）；
若 Key 本身已經精準到異動最小單位，重載時就可以安全地整把 `KeyDeleteAsync` + 全量灌回（簡單、安全）。

本系統依此原則，將三層快取全部設計為「Key 粒度 = 對應 Oracle 表的變更最小單位」。

---

## 一、Oracle 異動來源與對應的三種 Outbox 事件

| Oracle 資料表 | 異動語意 | 對應 Outbox 事件 | 事件攜帶資訊 |
|---|---|---|---|
| `ARM_ACTION_DEF` | API/畫面的功能門檻（REQUIRED_BITS）異動 | `ActionRequirementChangedEvent` | `Actions[] { System, Controller, ActionName }` |
| `ARM_PERMISSIONS` | 角色被賦予的權限位元（PERMS_BITS）異動 | `RolePermissionChangedEvent` | `RoleIds[]` |
| `ARM_USER_ROLES` | 使用者與角色的配置關係異動 | `UserRoleChangedEvent` | `UserIds[]` |

三者皆實作 `IPermissionChangedEvent`，由 `PermissionChangeDispatcher` 依型別分派給對應的
`IPermissionEventHandler`（`ActionRequirementChangedEventHandler` / `RolePermissionChangedEventHandler` /
`UserRoleChangedEventHandler`）。

---

## 二、Valkey/KeyDB 三層快取結構與粒度對照

| 快取層級 | Redis Key 格式 | Key 的粒度 | 資料結構 | 對應的維護服務 |
|---|---|---|---|---|
| 層二：User-Role | `user_roles:{userId}` | **單一 User** | STRING (JSON `string[]`) | `ValkeyDbService.DeleteUserRolesAsync` |
| 層三：角色權限 | `role:perm:{systemId}:{roleId}` | **System + Role 交集** | HASH（field=`{Controller}.{ActionName\|*}`） | `ICacheSyncService.LoadRolePermissionsToCacheAsync` |
| 層三：功能門檻 | `sys:required:{systemId}:{controller}` | **System + Controller 交集** | HASH（field=`ActionName`） | `ICacheSyncService.LoadControllerActionDefinitionsToCacheAsync` |

> 💡 `sys:required` 原本是「整個系統一把 Key」，已重構為「System + Controller」兩維度交集，
> 理由詳見下方「設計沿革」。

---

## 三、事件 ↔ 快取粒度 對應總表

| Outbox 事件 | Key 粒度是否天然對齊？ | 重載策略 | 呼叫方法 |
|---|---|---|---|
| `UserRoleChangedEvent` | ✅ 完全對齊（User） | 整把 KeyDelete，交由下次請求 Lazy Loading | `ValkeyDbService.DeleteUserRolesAsync(userId)` |
| `RolePermissionChangedEvent` | ✅ 完全對齊（System+Role），但需先查出角色涉及的所有 System | 整把 KeyDelete + 全量灌回，跨系統迴圈處理 | `ICacheSyncService.LoadRolePermissionsAcrossAllSystemsAsync(roleId)` |
| `ActionRequirementChangedEvent` | ✅ 對齊（System+Controller），需先依 (System, Controller) 去重分組 | 整把 KeyDelete + 全量灌回 | `ICacheSyncService.LoadControllerActionDefinitionsToCacheAsync(systemId, controller)` |

### 3.1 UserRoleChangedEvent（User-Role 配置異動）

- **對應快取層**：層二 `user_roles:{userId}`
- **與 `ICacheSyncService` 無關**：這是層二快取，不屬於 `ICacheSyncService`（管理層三）的職責範圍。
- **處理方式**：`UserRoleChangedEventHandler` 針對每個受影響的 `userId`，直接呼叫
  `ValkeyDbService.DeleteUserRolesAsync(userId)`，讓下次請求觸發 Lazy Loading 從 Oracle 回填最新角色清單。
- **不需要回填**：`user_roles` 走 Cache-Aside，砍掉即可，不用主動預熱。

### 3.2 RolePermissionChangedEvent（角色權限異動）

- **對應快取層**：層三 `role:perm:{systemId}:{roleId}`
- **關鍵前提**：`ARM_ROLES.ROLE_ID` 是全域唯一 PK（未綁定 SystemId），而
  `ARM_PERMISSIONS` 允許同一個 `ROLE_ID` 同時存在於多個 `SYSTEM_ID` 底下
  （`UNIQUE (SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME)`）。
  → **Role 是跨系統共用的實體**，一次角色權限異動可能同時影響好幾個子系統的快取。
- **處理方式**：
  1. `OracleDbService.GetDistinctSystemIdsByRoleAsync(roleId)` 查出該角色目前有效權限分佈在哪些 `SystemId`
  2. 針對每個 `SystemId`，呼叫既有的 `LoadRolePermissionsToCacheAsync(systemId, roleId)`
  3. 上述封裝為 `ICacheSyncService.LoadRolePermissionsAcrossAllSystemsAsync(roleId)`
- **為什麼可以整把 Key 砍**：`role:perm:{systemId}:{roleId}` 這把 Key 本身就已經是「該角色 + 該系統」
  交集下的最小單位，Hash 內每個 field 都只屬於同一角色，物理上與其他角色的 Key 完全隔離，
  不會有「砍掉這個角色卻誤傷別的角色」的風險。

### 3.3 ActionRequirementChangedEvent（Action 需求門檻異動）

- **對應快取層**：層三 `sys:required:{systemId}:{controller}`
- **關鍵前提**：Action 門檻不一定每個都有定義，可能依 Controller 分組管理；
  同一個 Controller 底下有多個 Action，但不同 Controller 彼此的門檻設定完全獨立。
- **處理方式**：
  1. `ActionRequirementChangedEventHandler` 將事件內的 `Actions[]` 依 `(System, Controller)` 去重分組
  2. 對每一組呼叫 `ICacheSyncService.LoadControllerActionDefinitionsToCacheAsync(systemId, controller)`
  3. 內部透過 `OracleDbService.GetActionDefsForCacheAsync(systemId, controller)`（Controller 級查詢）
     取得最新資料，整把 `KeyDeleteAsync` 該 Controller 的 Key 後全量灌回
- **為什麼不能整個系統重載**：若沿用「整個系統一把 Key」的舊設計，砍 Key 時會誤傷同系統下所有無關
  Controller 的快取，造成短暫大範圍拒絕存取（`requiredBits == null` → `EvaluatePermissionFromCacheDbAsync`
  直接回傳 `false`）。因此改為 Key 精準到 Controller，Blast Radius 從「整個系統」縮小到「單一 Controller」。
- **為什麼不用細到單一 Action**：因為 Key 已經精準到 Controller，Hash 內部全部都是同一個 Controller
  的 Action，不會有跨 Controller 誤傷的問題，細到 Action field 級反而增加不必要的複雜度
  （還要額外做 `HashKeysAsync` 過濾殘留欄位）。

---

## 四、設計沿革（為什麼從「整系統一把 Key」改成「System+Controller」）

### 舊設計的問題

```
sys:required:{systemId}   ← 一把 HASH，塞了該系統所有 Controller 的門檻
    field "Patient.Update" → 8
    field "Record.ManageRecord" → 15
    field "Admin.Delete" → 8
    ...
```

只改了 `Record.ManageRecord` 一個門檻，卻要：
1. `KeyDeleteAsync` 整把 Key（連 `Patient`、`Admin` 等無關 Controller 也被砍了）
2. 從 Oracle 撈整個系統的資料重灌
3. 砍掉到灌回這段空窗期，其他無關 Controller 的請求會 Cache Miss，
   而 `AuthService.EvaluatePermissionFromCacheDbAsync` 對 `requiredBits == null` 的處理是直接 `return false`，
   等於瞬間讓無關 Controller 的所有請求被拒絕存取

### 新設計

```
sys:required:{systemId}:{controller}   ← Key 精準到 Controller
    field "ManageRecord" → 15
```

重載 `Record` Controller 時，只會影響 `sys:required:{systemId}:Record` 這把 Key，
其他 Controller（`Patient`、`Admin`……）的 Key 完全不受影響。

---

## 五、與 Redis Pub/Sub / SignalR 廣播的關係（L1/L0 快取失效）

Valkey/KeyDB（層二、層三）的重載，屬於**資料正確性**層面（Source of Truth 的映射）。

L1（ArmV2 自身 `IMemoryCache`）與 L0（各子系統 Client 端 `IMemoryCache`）的失效，
屬於**新鮮度（Freshness）**層面，透過以下機制達成：

- L1：`CacheTokenManager` 雙引信機制，搭配 Redis Pub/Sub 頻道 `role_perm_update` 廣播觸發
- L0：外部 SignalR NotifyHub 廣播 `PermissionCacheInvalidated` 事件，觸發 `L0AuthCacheService.Invalidate`

兩者都採用「**TTL 兜底 + Pub/Sub 加速失效**」的雙重設計（Bounded Staleness）：
即使某個 instance 短暫漏收廣播（網路抖動、重啟中），最壞情況也只是多撐著舊資料到 TTL 到期
（L1 為 30 分鐘，L0 為可設定的 `CacheTtlSeconds`），不會造成永久性的資料錯誤。

> 因此 Outbox 的可靠性保證，只需要涵蓋「Oracle → Valkey/KeyDB」這一段（因為這段沒有 TTL 兜底，
> 寫入失敗就是真的資料不一致），不需要延伸去保證 Pub/Sub / SignalR 廣播的送達率。

若外部 SignalR NotifyHub 未來也部署為多 instance，須確認是否已掛載 SignalR Backplane
（例如 `Microsoft.AspNetCore.SignalR.StackExchangeRedis`），否則連到不同 Hub instance 的 Client
彼此收不到對方的廣播訊息，L0 的「快速失效」機制會失靈，只剩 TTL 硬撐。

---

## 六、相關程式碼位置索引

| 項目 | 檔案位置 |
|---|---|
| Outbox 事件定義 | `Models/Events/ActionRequirementChangedEvent.cs`、`RolePermissionChangedEvent.cs`、`UserRoleChangedEvent.cs` |
| 事件分派器 | `Services/Dispatch/PermissionChangeDispatcher.cs` |
| 事件處理器 | `Services/Handlers/PermissionEventHandlers.cs` |
| Impact 計算（Role/Action → UserIds） | `Services/PermissionImpact/PermissionImpactService.cs` |
| 快取同步服務介面 | `Services/Interfaces/Iservice.cs`（`ICacheSyncService`） |
| 快取同步服務實作 | `Services/Cache/CacheSyncService.cs` |
| Valkey/KeyDB 存取層 | `Data/ValkeyDbService.cs` |
| Oracle 存取層（含 Controller/Role 跨系統查詢） | `Data/OracleDbService.cs` |
| L1 雙引信管理器 | `Services/Cache/CacheTokenManager.cs` |
| Redis Pub/Sub 監聽（L1 失效觸發） | `Services/HostedServices/CacheDbPubSubBackgroundService.cs` |
| L0 用戶端快取 | `HIS2_ARM_KeyDB_Client/Services/L0AuthCacheService.cs` |
| SignalR 觀察者（L0 失效觸發） | `HIS2_ARM_KeyDB_Client/Services/Notification/ArmNotificationObserver.cs` |

---

## 七、待辦事項（Outbox 本體尚未實作）

- [ ] Oracle DDL：新增 `ARM_OUTBOX_EVENTS` 表
- [ ] 寫入端：`AdminController` 的異動 API 改為在同一 Transaction 內寫入 Outbox 事件，
      取代現行「Commit 後同步直呼 `ValkeyDbService`」的寫法
- [ ] Outbox Worker：輪詢 Pending 事件、反序列化、呼叫 `IPermissionChangeDispatcher.DispatchAsync`、
      標記完成或重試
- [ ] 確認外部 SignalR NotifyHub 若為多 instance 部署，是否已掛載 Redis Backplane

---

*本文件對應討論日期：2026-08-28*

