# HIS2 ARM V2 Outbox 設計架構說明

> 本文件整理 HIS2 ARM V2 權限異動、Oracle、Outbox、Valkey、L1/L0 Cache 的架構。
> Outbox 基礎（Phase 1–4）已實作；本版新增「權限版本」機制作為快取正確性依據，並將 Pub/Sub 與 SignalR 降為加速器。

---

## 1. 設計目標

HIS2 ARM V2 的權限資料以 Oracle 為 Source of Truth，Valkey 作為 L2 Cache，ARM 本機 `IMemoryCache` 作為 L1 Cache，下游 WebAPI 本機快取作為 L0 Cache。

權限資料異動後，必須可靠地把「資料異動」傳遞到快取與各子系統，而不能依賴 Controller 在資料庫 Commit 後自行呼叫 Cache 或 SignalR。

本設計主要解決：

1. Oracle 交易成功後，權限異動事件不可遺失。
2. DB Transaction 與 Outbox Event 必須 Atomic。
3. Cache 更新與通知由背景 Worker 處理，不阻塞使用者的管理操作。
4. 多台 ARM Instance 與多台 WebAPI 的本機快取，正確性不得依賴推播送達。
5. 權限異動必須只影響真正受影響的 User / Role / Controller。
6. Outbox 採 At-Least-Once Delivery，因此 Handler 必須具備 Idempotent 特性。
7. 依賴服務正常時，Oracle 提交後 60 秒內完成共享快取更新與版本遞增。

---

## 2. 整體架構

```text
┌──────────────────────────────────────────────────────────────┐
│                    ARM Admin / Management                    │
│                                                              │
│ AdminController / PermMgr / UserMgr / Other Mutation Paths  │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               │ Oracle Transaction
                               │ + Outbox INSERT
                               ▼
┌──────────────────────────────────────────────────────────────┐
│                         Oracle DB                            │
│                                                              │
│  ARM_USERS                                                   │
│  ARM_USER_ROLES                                              │
│  ARM_ROLES                                                   │
│  ARM_PERMISSIONS                                             │
│  ARM_ACTION_DEF                                              │
│                                                              │
│  ARM_OUTBOX_EVENTS                                           │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               │ Poll
                               ▼
┌──────────────────────────────────────────────────────────────┐
│                       OutboxWorker                           │
│                                                              │
│  Claim → Dispatch → Handler → Mark Processed                │
│             │                                                │
│             ▼                                                │
│       PermissionChangeDispatcher                             │
└──────────────────────────────┬───────────────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
       Event Handler     Impact Service     Cache Sync
              │                │                │
              │                ▼                ▼
              │             UserId[]        Valkey L2
              │                │          (共享權限 Key)
              │                ▼
              │       INCR perm_ver:{userId}   ← 正確性依據
              │                │
              └────────────────┴──────────────┐
                                               ▼
                                   推播（加速器，可丟失）
                                   ├── Pub/Sub → ARM L1
                                   └── SignalR → WebAPI L0

   讀取端（ARM L1 / WebAPI L0）每次命中：
     比對本機條目版本 == perm_ver:{userId} ?
       相同 → 使用快取
       不同 → 丟棄並重新驗證
```

---

## 3. 為什麼需要 Outbox

如果直接採用：

```text
Oracle Commit
    ↓
Delete / Reload Valkey
    ↓
SignalR
```

會有典型的可靠性問題。

例如：

```text
Oracle Commit 成功
        ↓
程式在呼叫 Valkey 前 Crash
        ↓
Cache 沒更新
        ↓
SignalR 也沒送出
```

此時 Oracle 已經是新資料，但 Cache 與 Client 不知道資料已經改變。

Outbox 將事件與業務資料寫入同一個 Oracle Transaction：

```text
BEGIN TRANSACTION

    UPDATE ARM_PERMISSIONS ...

    INSERT INTO ARM_OUTBOX_EVENTS ...

COMMIT
```

只要 Transaction 成功，業務資料與 Outbox Event 就會同時存在。

之後即使 ARM Application Process 立即停止，OutboxWorker 重新啟動後仍可以繼續處理事件。

---

# 4. ARM_OUTBOX_EVENTS

目前已確認的 DDL：

```sql
CREATE TABLE HIS2USER2.ARM_OUTBOX_EVENTS (
    EVENT_ID         VARCHAR2(36)   NOT NULL,
    EVENT_TYPE       VARCHAR2(50)   NOT NULL,
    AGGREGATE_KEY    VARCHAR2(200)  NULL,
    PAYLOAD_JSON     CLOB           NOT NULL,
    STATUS           VARCHAR2(20)   DEFAULT 'PENDING' NOT NULL,
    RETRY_COUNT      NUMBER(5)      DEFAULT 0 NOT NULL,
    MAX_RETRY        NUMBER(5)      DEFAULT 5 NOT NULL,
    NEXT_RETRY_TIME  DATE           DEFAULT SYSDATE NOT NULL,
    LAST_ERROR       VARCHAR2(2000) NULL,
    LOCKED_BY        VARCHAR2(100)  NULL,
    LOCKED_TIME      DATE           NULL,
    CREATE_USER      VARCHAR2(20)   DEFAULT NULL,
    CREATE_TIME      DATE           DEFAULT SYSDATE NOT NULL,
    PROCESSED_TIME   DATE           NULL,
    CONSTRAINT PK_ARM_OUTBOX_EVENTS PRIMARY KEY (EVENT_ID),
    CONSTRAINT CK_OUTBOX_STATUS CHECK (STATUS IN ('PENDING', 'PROCESSING', 'PROCESSED', 'FAILED'))
);

COMMENT ON TABLE HIS2USER2.ARM_OUTBOX_EVENTS IS 'Outbox Event 表，用於可靠事件發送與重試';

COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.EVENT_ID        IS 'Event 唯一識別碼，建議 GUID';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.EVENT_TYPE      IS '事件類型';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.AGGREGATE_KEY   IS '事件主要聚合對象，例如 RoleId / UserId / Controller';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.PAYLOAD_JSON    IS 'Event 完整內容';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.STATUS          IS 'PENDING / PROCESSING / PROCESSED / FAILED';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.RETRY_COUNT     IS '已重試次數';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.MAX_RETRY       IS '最大重試次數';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.NEXT_RETRY_TIME IS '下次允許處理時間';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.LAST_ERROR      IS '最後一次錯誤資訊';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.LOCKED_BY       IS '目前取得事件的 Worker';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.LOCKED_TIME     IS 'Lock 時間，用於 stale processing recovery';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.CREATE_USER     IS '建立事件的使用者';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.CREATE_TIME     IS 'Event 建立時間';
COMMENT ON COLUMN HIS2USER2.ARM_OUTBOX_EVENTS.PROCESSED_TIME  IS '成功處理時間';

CREATE INDEX IDX_OUTBOX_POLL
    ON HIS2USER2.ARM_OUTBOX_EVENTS (STATUS, NEXT_RETRY_TIME, CREATE_TIME);

CREATE INDEX IDX_OUTBOX_AGGREGATE_KEY
    ON HIS2USER2.ARM_OUTBOX_EVENTS (AGGREGATE_KEY);
```

### 欄位用途

| 欄位 | 用途 |
|---|---|
| `EVENT_ID` | Event 唯一識別碼，建議 GUID |
| `EVENT_TYPE` | 事件類型 |
| `AGGREGATE_KEY` | 事件主要聚合對象，例如 RoleId / UserId / Controller |
| `PAYLOAD_JSON` | Event 完整內容 |
| `STATUS` | `PENDING / PROCESSING / PROCESSED / FAILED` |
| `RETRY_COUNT` | 已重試次數 |
| `MAX_RETRY` | 最大重試次數 |
| `NEXT_RETRY_TIME` | 下次允許處理時間 |
| `LAST_ERROR` | 最後一次錯誤資訊 |
| `LOCKED_BY` | 目前取得事件的 Worker |
| `LOCKED_TIME` | Lock 時間，用於 stale processing recovery |
| `CREATE_USER` | 建立事件的使用者 |
| `CREATE_TIME` | Event 建立時間 |
| `PROCESSED_TIME` | 成功處理時間 |

---

# 5. Event 類型

最終規劃五種權限相關 Event：

```text
UserRoleChangedEvent
RolePermissionChangedEvent
ActionRequirementChangedEvent
RoleStatusChangedEvent
UserStatusChangedEvent
```

---

## 5.1 UserRoleChangedEvent

來源：

```text
ARM_USER_ROLES
```

語意：特定 User 的角色集合發生變化。

影響範圍：

```text
UserId
    ↓
user_roles:{UserId}
    ↓
該 User 的 Authorization Cache
```

不需要因為某個 User 的角色異動，而讓同 Role 的其他 User 全部失效。

Handler 主要工作：

1. Delete `user_roles:{userId}`
2. `INCR perm_ver:{userId}`
3. 推播 `PermissionCacheInvalidated`（加速）

L1/L0 的舊授權條目因版本不符，在下一次命中時即被丟棄重驗，不依賴 TTL。

---

# 6. RolePermissionChangedEvent

來源：

```text
ARM_PERMISSIONS
```

語意：某個 Role 在某個 System 下的 Permission 發生變化。

Payload 必須記錄受影響的 `(SystemId, RoleId)` 配對，而不是只有 `RoleId`：

```csharp
public sealed class RolePermissionChangedEvent : IPermissionChangedEvent
{
    public required RoleScope[] Targets { get; init; }  // (SystemId, RoleId)
}
```

原因：Worker 若在處理時才查「目前仍有效的權限分佈在哪些 System」，當某 System 下的最後一筆權限被停用或刪除時，該 System 就不會出現在查詢結果中，舊的 `role:perm:{systemId}:{roleId}` Key 就會被遺漏。

影響範圍：

```text
(SystemId, RoleId)[]
   │
   ├── role:perm:{SystemId}:{RoleId}（重載；若已無有效權限則刪除）
   │
   └── ARM_USER_ROLES
             │
             ▼
          UserId[]
             │
             ▼
       INCR perm_ver:{userId}
```

### 6.1 Reload Role Permission L2 Cache

對 Payload 中每一個 `(SystemId, RoleId)`：

- 從 Oracle 讀取最新有效權限
- 若有資料：寫入暫存 Key 後原子切換（見 §31.1）
- 若無資料：刪除舊 Key

### 6.2 找出 Role 所屬 Users

使用：

```sql
SELECT DISTINCT USER_ID
FROM HIS2USER2.ARM_USER_ROLES
WHERE ROLE_ID IN :RoleIds
  AND IS_DC = 'N'
  AND IS_ACTIVE = 'Y'
```

再對這些 User 發送 Cache Invalidation。

---

# 7. ActionRequirementChangedEvent

來源：

```text
ARM_ACTION_DEF
```

語意：某 Controller / Action 的 Required Bits 發生變化。

重要設計：

> `ARM_ACTION_DEF` 的 Cache 粒度是 Controller，而不是單一 Action。

因此只需要重新載入：

```text
sys:required:{systemId}:{controller}
```

不需要重新載入整個 System。

---

## 7.1 User Impact

雖然 Action Definition 本身是 Controller 層級 Cache，但實際 Authorization 受到該 Action 影響的 User 必須找出來。

影響關係：

```text
Action
  ↓
ARM_PERMISSIONS
  ↓
Role
  ↓
ARM_USER_ROLES
  ↓
User
```

而 Permission 判斷必須包含：

```sql
p.ACTION_NAME = :ActionName
OR p.ACTION_NAME IS NULL
```

也就是同時考慮：

1. Action-specific Permission
2. Controller-level Generic Permission

最後得到 `UserId[]`，送出統一的 `PermissionCacheInvalidated` 通知。

---

# 8. RoleStatusChangedEvent

來源：

```text
ARM_ROLES
```

語意：某 Role 的 `IS_ACTIVE` 狀態改變。

影響範圍：

```text
RoleId
   ↓
ARM_USER_ROLES
   ↓
UserId[]
   ├── Delete user_roles:{userId}
   └── INCR perm_ver:{userId}
```

### 重要設計決定

`role:perm:{systemId}:{roleId}` 不需要因為 Role Status 改變而 Reload，因為它代表的是 Role 本身擁有哪些 Permission，與 `ARM_ROLES.IS_ACTIVE` 是不同資料。

但 **必須刪除受影響使用者的 `user_roles:{userId}`**。原因：授權計算從 `user_roles` 取角色集合；若不清除，即使 L1/L0 已失效，重算時仍會讀到停用角色。

同時 Oracle 回填 `user_roles` 的查詢必須排除 `ARM_ROLES.IS_ACTIVE = 'N'` 的角色（見 §30）。

---

# 9. UserStatusChangedEvent

來源：

```text
ARM_USERS
```

語意：特定 User 的啟用 / 停用狀態發生變化。

影響範圍只有該 User：

```text
UserId
   ├── User Authorization Cache
   ├── User Role Cache
   └── Session / Refresh Session
```

如果 User 被停用，除了 Cache Invalidation，也應該撤銷該 User 的 Session。

此 Event 與 `UserRoleChangedEvent` 必須分開，因為：

```text
UserRoleChanged
```

表示角色集合改變；

```text
UserStatusChanged
```

表示 User 本身的 Active 狀態改變。

兩者是不同的資料語意。

---

# 10. Event 與 Cache Impact Matrix

| Event | Source | Valkey L2 | User Impact | perm_ver |
|---|---|---|---|---|
| `UserRoleChanged` | `ARM_USER_ROLES` | Delete `user_roles:{userId}` | 該 User | INCR |
| `RolePermissionChanged` | `ARM_PERMISSIONS` | Reload/Delete `role:perm:{sys}:{role}`（依 Payload Targets） | Role 所屬 Users | INCR |
| `ActionRequirementChanged` | `ARM_ACTION_DEF` | Reload `sys:required:{sys}:{controller}` | 使用該 Action / Controller Permission 的 Users | INCR |
| `RoleStatusChanged` | `ARM_ROLES` | Delete `user_roles:{userId}`（不 Reload role:perm） | Role 所屬 Users | INCR |
| `UserStatusChanged` | `ARM_USERS` | Revoke Session + Delete `user_roles` | 該 User | INCR |

所有事件在共享 Key 更新完成後，對受影響 UserId 執行 `INCR perm_ver`，再推播（若保留）。

---

# 11. Dispatcher 設計

目前採用：

```text
PermissionChangeDispatcher
        │
        ├── UserRoleChangedEventHandler
        ├── RolePermissionChangedEventHandler
        ├── ActionRequirementChangedEventHandler
        ├── RoleStatusChangedEventHandler
        └── UserStatusChangedEventHandler
```

Dispatcher 只負責：

1. 找到對應 Handler
2. 執行 Handler
3. 找不到 Handler 時報錯

Dispatcher 不負責：

- Oracle SQL
- Valkey 操作
- Impact 計算
- SignalR

因此責任保持單純。

---

# 12. Handler 設計

Handler 是事件處理的主要協調者。

典型流程：

```text
Event
 ↓
Handler
 ├── CacheSync / Valkey（共享 Key）
 ├── PermissionImpactService → UserId[]
 ├── INCR perm_ver:{userId}
 └── 推播（Pub/Sub / SignalR，加速用）
```

順序不可調換：版本遞增必須在共享 Key 更新完成之後，否則讀到新版本的節點重驗時可能讀到舊資料。

Handler 不應自己撰寫大量 SQL。

Oracle Impact Query 集中於：

```text
PermissionImpactService
```

Cache Reload 集中於：

```text
CacheSyncService
```

SignalR 通知集中於：

```text
ArmNotificationService
```

---

# 13. PermissionImpactService

此 Service 的責任只有一件事：

> 根據權限異動事件，找出受到影響的 UserId。

例如：

```csharp
GetAffectedUserIdsAsync(RolePermissionChangedEvent evt)
```

會：

```text
RoleId[]
   ↓
ARM_USER_ROLES
   ↓
UserId[]
```

而：

```csharp
GetAffectedUserIdsAsync(ActionRequirementChangedEvent evt)
```

會：

```text
System + Controller + Action
          ↓
ARM_PERMISSIONS
          ↓
ARM_USER_ROLES
          ↓
UserId[]
```

### Impact Query 與 Effective Permission Query 必須分開

這是很重要的設計原則。

Impact Query 的目標是：

> 找出誰需要被通知 / 失效。

因此即使 Role 剛被停用，也不能因為 `IS_ACTIVE='N'` 而把它從 Impact Query 排除，否則反而找不到需要失效的 User。

Effective Permission Query 的目標則是：

> 判斷目前這個 User 是否真的擁有有效權限。

因此 Effective Permission Query 必須正確檢查：

```text
ARM_USERS.IS_ACTIVE
ARM_ROLES.IS_ACTIVE
ARM_USER_ROLES.IS_ACTIVE
ARM_PERMISSIONS.IS_ACTIVE
ARM_ACTION_DEF.IS_ACTIVE
```

兩種 Query 的目的不同，不應混在一起。

---

# 14. L1 / L2 / L0 Cache

目前 Cache 層次：

```text
L0
WebAPI 程序（下游子系統）
    │
    ├── L0AuthCacheService
    └── MenuCacheService

L1
ARM Instance
    │
    └── IMemoryCache

L2
Valkey
    │
    ├── user_roles:{userId}
    ├── role:perm:{systemId}:{roleId}
    ├── sys:required:{systemId}:{controller}
    └── perm_ver:{userId}            ← 每使用者權限版本

Source of Truth
Oracle
```

---

# 15. 權限版本（perm_ver）：本機快取的正確性依據

這是整個架構最重要的一段。

多台 ARM Instance 與多台 WebAPI 各有本機快取；推播（Pub/Sub、SignalR）無法保證送達每一台。因此正確性不建立在「推播有沒有到」，而是建立在「讀取時能否察覺過期」。

## 15.1 機制

Valkey 為每個使用者維護一個整數版本：

```text
perm_ver:{userId}
```

- 不存在視為 0。
- 任何影響該使用者授權的事件，Handler 在共享 Key 更新完成後執行 `INCR`。
- 可設長 TTL（例如 30 天）做清理；過期後讀到「不存在」等於 0，本機條目必然不符，仍會重驗。

讀取端（ARM L1 / WebAPI L0）：

```text
請求進來，解出 userId
  ↓
本機快取命中？
  ├─ 否 → 驗證，結果連同當前 perm_ver 存入本機
  └─ 是 → GET perm_ver:{userId}
            ├─ 與條目記錄的版本相同 → 使用快取
            └─ 不同 → 丟棄，重新驗證，存入新版本
```

版本比對應與現有 Valkey 查詢（例如 Session 檢查）併入同一 pipeline，實際多出的網路往返接近零。

## 15.2 為什麼能取代推播送達保證

| 故障情境 | 結果 |
|---|---|
| SignalR / Pub/Sub 推播漏收 | 下一個請求比對版本不符，立即重驗 |
| 節點重啟、本機快取全空 | 重驗時自然取得最新版本 |
| 節點推播連線斷但 HTTP 仍服務 | 同上，不需要隔離 |
| Worker 重試重複處理 | INCR 多加一次無害，只多觸發一次重驗 |
| 新節點加入 | 第一次驗證就帶最新版本，不需註冊 |

## 15.3 Valkey 不可用時

無法比對版本時採 **fail-closed**：拒絕受保護請求。Valkey 同時是 Session 存放處，它不可用時驗證本來就無法進行；一致地拒絕比放行舊授權安全。

## 15.4 帳號停用不需特例

帳號停用與其他事件走同一流程：撤銷 Session、清 `user_roles`、INCR 版本。不需額外拒絕標記或 ACK 屏障。

---

# 16. 推播：加速器，非正確性依據

Pub/Sub 與 SignalR 保留，但定位改為「讓節點提前清掉會被版本比對淘汰的條目」，省下異動後第一次命中的重驗。

它們可以丟失、可以慢、可以斷線，都不影響正確性。

## 16.1 Valkey Pub/Sub → ARM L1

現有頻道 `role_perm_update` 與 `CacheTokenManager` 雙引信機制目前沒有發布端呼叫（`PublishRolePermUpdateAsync` 無人使用）。

階段 1 不動它；階段 3 依觀察數據決定移除或改為以 UserId 為單位的通知。

## 16.2 SignalR → WebAPI L0

Server 統一送 `PermissionCacheInvalidated(UserIds)`，Client 依 UserId 清本機 Auth/Menu 快取。Client 不需知道事件類型。

ArmHub 目前單一執行個體；若擴充為多執行個體需 backplane，但因為已非正確性依據，缺 backplane 只影響加速效果。

---

# 17. WebAPI 快取模組必須統一

目前 `ArmAuthorizationMiddleware` 直接寫入 keyed `IMemoryCache`，而 `L0AuthCacheService.Invalidate(userId)` 只清它自己維護的 key index；middleware 寫入的條目不在 index 中，因此通知到達也清不掉實際使用的快取。

必須改為：middleware 的讀寫都透過 `L0AuthCacheService`，條目帶版本，命中時由同一模組負責比對。`MenuCacheService` 同樣帶版本比對。

---

# 18. ARM L1 快取

`AuthService` 的 L1 條目（`arm:l1auth:{systemId}:{roleHash}:{controller}:{action}`）同樣記錄寫入時的 `perm_ver`，命中時比對。

`roleHash` 機制保留：角色集合改變時 key 自然不同。但不再依賴 TTL 淘汰舊 key，由版本比對保證。

`CacheTokenManager` 雙引信在階段 1 保留不動；它目前沒有發布端，實際上未生效。

---

# 19. SignalR 的角色

見 §16.2。SignalR 只負責把 `PermissionCacheInvalidated(UserIds)` 通知出去作為加速；不計算 Impact、不查 Oracle、不修改 Valkey、不判斷權限，也不是正確性依據。

Payload：

```csharp
public sealed class PermissionCacheInvalidatedEvent
{
    public string[] UserIds { get; init; } = [];
}
```

---

# 20. Outbox Worker

OutboxWorker 是整個可靠事件處理鏈的核心。

基本生命週期：

```text
PENDING
   ↓
PROCESSING
   ↓
Handler
   ↓
PROCESSED
```

失敗：

```text
PROCESSING
   ↓
retry
   ↓
PENDING
```

超過最大次數：

```text
PENDING / PROCESSING
   ↓
FAILED
```

---

# 21. 多 Worker / 多 Instance Lock

Outbox 可能同時由多個 ARM Instance 處理，因此必須避免同一 Event 被多個 Worker 同時 Claim。

Oracle 的：

```sql
FOR UPDATE SKIP LOCKED
```

適合多 Consumer Queue。

### Oracle 注意事項

不要直接使用：

```sql
FETCH FIRST 20 ROWS ONLY
FOR UPDATE SKIP LOCKED
```

Oracle 的 `row_limiting_clause` 與 `for_update_clause` 不能如此組合。

若不要求嚴格 FIFO，可採用類似：

```sql
SELECT EVENT_ID,
       EVENT_TYPE,
       PAYLOAD_JSON,
       RETRY_COUNT,
       MAX_RETRY
FROM HIS2USER2.ARM_OUTBOX_EVENTS
WHERE STATUS = 'PENDING'
  AND NEXT_RETRY_TIME <= SYSDATE
  AND ROWNUM <= :BatchSize
FOR UPDATE SKIP LOCKED
```

但此方式不保證嚴格 `CREATE_TIME` 順序。

若未來有嚴格 FIFO 需求，應另設計 Claim SQL，而不是硬套 `FETCH FIRST`。

---

# 22. PROCESSING Stale Recovery

Worker 可能發生：

```text
Claim Event
   ↓
STATUS = PROCESSING
   ↓
Process Crash
```

此時 Event 不能永遠卡在 `PROCESSING`。

因此 `LOCKED_TIME` 的存在就是為了偵測 stale processing。

例如：

```text
PROCESSING
AND LOCKED_TIME < SYSDATE - timeout
```

可以由 Recovery 機制重新設定：

```text
PENDING
```

或依 retry policy 處理。

---

# 23. Retry Policy

Outbox 採 At-Least-Once Delivery。

暫時性錯誤（Oracle / Valkey 連線失敗等）採封頂退避持續重試，不因次數耗盡而放棄；超過 60 秒未完成須告警。

永久性錯誤（payload 無法反序列化、未知 EventType 等）才進入 `FAILED`，由人工修復後沿用原 `EventId` 重播。

退避排程：

```text
30 秒 → 1 分鐘 → 2 分鐘 → 5 分鐘 → 10 分鐘（封頂）
```

`MAX_RETRY` 欄位保留作為告警閾值，不再作為停止重試的條件。

---

# 24. At-Least-Once 的重要性

Outbox 不追求 Exactly-Once。

例如：

```text
Handler 成功
   ↓
SignalR 成功
   ↓
MarkProcessed 尚未成功
   ↓
Worker Crash
```

重新啟動後可能再次處理同一 Event。

因此：

```text
Cache Reload
Cache Delete
INCR perm_ver
Pub/Sub
SignalR
```

都必須允許重複執行。`INCR` 重複只會多觸發一次重驗，無害。

這是合理的設計。

---

# 25. Transaction Boundary

最重要的原則：

> Business Mutation 與 Outbox INSERT 必須使用同一個 Oracle Connection + Transaction。

例如：

```text
OracleConnection
      │
      └── OracleTransaction
              │
              ├── MERGE ARM_PERMISSIONS
              │
              └── INSERT ARM_OUTBOX_EVENTS

COMMIT
```

不能：

```text
Commit Business Data
        ↓
另外開 Connection
        ↓
Insert Outbox
```

因為第二步失敗時，會造成：

```text
Business Data 已成功
Outbox 不存在
```

事件因此可能永久遺失。

---

# 26. IOutboxService

建議介面責任：

```csharp
public interface IOutboxService
{
    Task EnqueueAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string eventType,
        string? aggregateKey,
        string payloadJson,
        string? createUser,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<OutboxEvent>> DequeueAsync(
        int batchSize,
        string workerId,
        CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        OutboxEvent evt,
        Exception exception,
        CancellationToken cancellationToken = default);
}
```

其中 `EnqueueAsync()` 特別重要：

它必須使用呼叫端提供的：

```text
IDbConnection
IDbTransaction
```

不能自己開 Connection、自己 Commit。

如此才能保證 Atomic Transaction。

---

# 27. Outbox Event Model

```csharp
public sealed class OutboxEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? AggregateKey { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public int MaxRetry { get; init; }
}
```

Event Type：

```csharp
public static class OutboxEventTypes
{
    public const string UserRoleChanged = "UserRoleChanged";
    public const string RolePermissionChanged = "RolePermissionChanged";
    public const string ActionRequirementChanged = "ActionRequirementChanged";
    public const string RoleStatusChanged = "RoleStatusChanged";
    public const string UserStatusChanged = "UserStatusChanged";
}
```

---

# 28. 現有 Mutation Path 必須統一

目前系統仍存在一些直接修改 Oracle 後直接更新 Cache / Publish 的舊路徑。

正式導入 Outbox 後，這些路徑應逐步統一成：

```text
Mutation
  ↓
Oracle Business Update
  +
Outbox Insert
  ↓
Commit
```

而不再由 Controller 直接負責：

```text
DeleteRolePermsAsync
PublishRolePermUpdateAsync
DeleteRequiredBitsAsync
SignalR
```

這些副作用應移交給 Outbox Handler。

---

# 29. 目前需要整理的 Mutation Path

## 29.1 AdminController.UpdateRolePermission

目前流程曾包含：

```text
MERGE ARM_PERMISSIONS
Commit
DeleteRolePermsAsync
PublishRolePermUpdateAsync
```

正式架構應改成：

```text
MERGE ARM_PERMISSIONS
Insert RolePermissionChanged Outbox
Commit
```

之後由 Worker 處理 Cache / Impact / Pub/Sub / SignalR。

---

## 29.2 AdminController.UpdateActionRequirement

目前流程曾包含：

```text
MERGE ARM_ACTION_DEF
Commit
DeleteRequiredBitsAsync
Publish ACTION_UPDATE
```

正式架構應改成：

```text
MERGE ARM_ACTION_DEF
Insert ActionRequirementChanged Outbox
Commit
```

---

## 29.3 PermMgrController.BatchSavePermissions

目前透過：

```text
OracleDbService.BatchUpsertPermissionsAsync
```

因此 `BatchUpsertPermissionsAsync` 必須把 Outbox Insert 納入同一個 Transaction。

---

## 29.4 User Mutation

User 相關操作必須依實際資料異動產生：

```text
UserRoleChangedEvent
UserStatusChangedEvent
```

例如：

```text
UpdateUser
   ↓
角色集合有變化
   ↓
UserRoleChanged
```

而：

```text
UpdateUser
   ↓
IS_ACTIVE 改變
   ↓
UserStatusChanged
```

如果一次操作同時改變角色與 User Status，可以產生兩個 Event。

---

# 30. ARM_ROLES 與 ARM_USERS 的有效授權

權限判斷的有效性不能只看：

```text
ARM_PERMISSIONS.IS_ACTIVE
```

還必須考慮：

```text
ARM_USERS.IS_ACTIVE
ARM_ROLES.IS_ACTIVE
ARM_USER_ROLES.IS_ACTIVE
```

例如 Role 被停用：

```text
ARM_ROLES.IS_ACTIVE = 'N'
```

即使：

```text
ARM_PERMISSIONS.IS_ACTIVE = 'Y'
```

該 Role 仍不應提供有效權限。

因此 Role Status 改變時，必須立即讓該 Role 所屬 Users 的授權 Cache 失效。

---

# 31. Cache Sync 與 Impact 的責任分離

```text
CacheSyncService
    │
    └── 負責「Cache 裡面要放什麼」

PermissionImpactService
    │
    └── 負責「哪些 User 受到影響」
```

不要讓兩者互相混入。

例如：

```text
RolePermissionChanged
```

Handler 可以：

```text
CacheSyncService
    → Reload role:perm

PermissionImpactService
    → 找 UserId[]

NotificationService
    → SignalR
```

這樣責任非常清楚。

## 31.1 Reload 必須原子切換

目前 `CacheSyncService` 先 `KeyDelete` 再 `HashSet`，中間空窗的請求會讀到 Cache Miss；`requiredBits == null` 時授權直接回傳 false，這個拒絕結果還會進入 L1。

改為：寫入 `{key}:staging` → `RENAME staging key`（原子覆蓋）。準備失敗時舊 Key 保留。若 Oracle 已無資料則直接 `KeyDelete`。

## 31.2 同 AggregateKey 序列化

同一 `AggregateKey` 的事件不得並行寫入快取，避免較舊的載入結果覆蓋新資料。重載一律讀取 Oracle 當下最新狀態。

---

# 32. 最終 Handler 流程

所有 Handler 的最後兩步固定為：對受影響 UserId `INCR perm_ver` → 推播（加速）。

## UserRoleChanged

```text
UserRoleChangedEvent
        │
        ├── Delete user_roles:{userId}
        ├── INCR perm_ver:{userId}
        └── 推播 PermissionCacheInvalidated(userId)
```

---

## RolePermissionChanged

```text
RolePermissionChangedEvent { Targets: (SystemId, RoleId)[] }
        │
        ├── 對每個 Target：Reload/Delete role:perm:{system}:{role}（原子切換）
        │
        ├── PermissionImpactService → UserId[]
        │
        ├── INCR perm_ver:{userId} × N
        │
        └── 推播 PermissionCacheInvalidated(UserId[])
```

---

## ActionRequirementChanged

```text
ActionRequirementChangedEvent
        │
        ├── Group by System + Controller
        │
        ├── Reload sys:required:{system}:{controller}（原子切換）
        │
        ├── PermissionImpactService → UserId[]
        │
        ├── INCR perm_ver:{userId} × N
        │
        └── 推播 PermissionCacheInvalidated(UserId[])
```

---

## RoleStatusChanged

```text
RoleStatusChangedEvent
        │
        ├── 不 Reload role:perm
        │
        ├── PermissionImpactService → Role 所屬 UserId[]
        │
        ├── Delete user_roles:{userId} × N
        │
        ├── INCR perm_ver:{userId} × N
        │
        └── 推播 PermissionCacheInvalidated(UserId[])
```

---

## UserStatusChanged

```text
UserStatusChangedEvent
        │
        ├── Revoke Session / Refresh Session
        ├── Delete user_roles:{userId}
        ├── INCR perm_ver:{userId}
        │
        └── 推播 PermissionCacheInvalidated(userId)
```

---

# 33. 不採用的設計

## 33.1 Controller 直接處理 Cache

不採用：

```text
Controller
  → Oracle
  → Valkey
  → SignalR
```

原因：

- Transaction 與副作用耦合
- Controller 過重
- 容易遺漏某一條 mutation path
- Application Crash 時可能造成 Cache 不一致

---

## 33.2 另外建立 Repository 只轉呼叫 OracleDbService

不需要為了形式上的 Layering 而增加：

```text
Controller
 → Service
   → Repository
     → OracleDbService
```

如果 Repository 只有：

```csharp
return _oracleDb.GetXXXAsync(...);
```

這只是增加維護成本。

目前直接讓 `OracleDbService` 作為 DB Access Layer 即可。

---

## 33.3 RoleStatusChanged 強制 Reload role:perm

不需要。Role Permission 與 Role Active Status 是不同資料。

但必須清除受影響使用者的 `user_roles` 並遞增版本，並確保 Oracle 回填排除停用角色（見 §8）。只清 L0/L1 而不清 `user_roles` 是授權漏洞。

---

## 33.4 每個事件都廣播所有 User

不採用。採用 Impact Query 精準找出受影響 User。

---

## 33.5 逐節點 ACK 與在線名冊

不採用。曾評估「每事件 × 每 consumer 程序寫一筆 Oracle ACK，配合 Valkey 心跳在線名冊、readiness 隔離」，以證明所有節點已清快取。成本（名冊、心跳、啟動代次、隔離邏輯）遠高於收益；`perm_ver` 比對以一次 Valkey GET 達到同等正確性。

---

## 33.6 Outbox 主表加 L0/L1/Menu 同步旗標

不採用。固定欄位無法表達同一快取層的多個執行個體。

---

## 33.7 帳號停用專用拒絕標記與 ACK 屏障

不採用。`perm_ver` 已涵蓋；停用與其他事件同流程。

---

## 33.8 TTL 作唯一兔底

不採用。撤權不得依賴時間淘汰；本機 TTL 保留作記憶體回收，不作正確性依據。

---

# 34. 最終責任分工

| 元件 | 責任 |
|---|---|
| Mutation Controller / Service | 修改 Oracle 業務資料 + Outbox |
| `OracleDbService` | Oracle DB Access |
| `IOutboxService` | Outbox 寫入 / Claim / 狀態管理 |
| `OutboxWorker` | Poll / Claim / Dispatch / Retry |
| `PermissionChangeDispatcher` | Event → Handler |
| Event Handler | 協調 Cache、Impact、版本遞增、推播 |
| `PermissionImpactService` | 找受影響 User |
| `CacheSyncService` | Reload Valkey L2（原子切換） |
| `ValkeyDbService` | Valkey CRUD / perm_ver INCR與GET / Pub/Sub |
| `CacheDbPubSubBackgroundService` | 接收 Pub/Sub 並清除本機 L1（加速，階段 3 評估移除） |
| `CacheTokenManager` | L1 Change Token（加速，階段 3 評估移除） |
| `ArmNotificationService` | SignalR 發送（加速） |
| ARM `AuthService` | L1 條目帶版本並比對 |
| Client `L0AuthCacheService` | L0 Auth 條目帶版本並比對；middleware 唯一入口 |
| Client `MenuCacheService` | Menu 條目帶版本並比對 |

---

# 35. 實作順序

Phase 1–4（Outbox 基礎、Transaction Integration、OutboxWorker、五種 Handler）已完成。

## Phase 5：權限版本與安全修正（不刪任何既有元件）

1. `RoleStatusChanged` Handler 清 `user_roles`；Oracle `GetUserByIdFromRmdbAsync` 回填排除 `ARM_ROLES.IS_ACTIVE='N'`
2. `RolePermissionChangedEvent` 改為 `Targets: (SystemId, RoleId)[]`；寫入端補齊；Handler 依 Target 重載或刪除
3. `ValkeyDbService` 新增 `IncrPermVersionAsync` / `GetPermVersionAsync`；所有 Handler 末尾對 UserIds INCR
4. `CacheSyncService` 改暫存 Key 原子切換
5. WebAPI：`ArmAuthorizationMiddleware` 改走 `L0AuthCacheService`；條目帶版本、命中時比對；`MenuCacheService` 同樣處理；Valkey 不可用 fail-closed
6. ARM `AuthService` L1 條目帶版本並比對

## Phase 6：觀察

監控「版本不符重驗」與「推播命中提前清除」次數。

## Phase 7：選擇性瘦身

依 Phase 6 數據決定是否移除 `role_perm_update` Pub/Sub、`CacheTokenManager`、SignalR 推播。

## Phase 8：故障測試

- Worker Crash / Stale PROCESSING
- Valkey 暫時失效（驗證 fail-closed）
- SignalR 全斷（驗證版本比對仍正確）
- Duplicate Event
- 同 AggregateKey 並發事件

---

# 36. Acceptance Criteria

正式上線前至少確認以下條件。

### Transaction

- [ ] Business Data 與 Outbox 同一 Transaction
- [ ] Business Rollback 時 Outbox 不存在
- [ ] Business Commit 成功時 Outbox 一定存在

### Worker

- [ ] 多 Worker 不會同時處理同一 Event
- [ ] Crash 後 PROCESSING Event 可以 Recovery
- [ ] 暫時性錯誤持續重試，超過 60 秒告警
- [ ] 永久性錯誤進入 FAILED，可沿用原 EventId 重播

### Cache

- [ ] Role Permission 修改會更新所有 Payload Targets 的 L2，含已無有效權限的 System
- [ ] Action Requirement 修改會更新 Controller L2
- [ ] Role Status 停用會清 user_roles；Oracle 回填不含停用角色
- [ ] User Role 修改會清除 user_roles
- [ ] User Status 修改會撤銷 Session 並清 user_roles
- [ ] Reload 為原子切換，無 Cache Miss 空窗

### 權限版本

- [ ] 每個 Handler 在共享 Key 更新後對受影響 UserId INCR perm_ver
- [ ] ARM L1 條目帶版本，命中時比對
- [ ] WebAPI L0 Auth / Menu 條目帶版本，命中時比對
- [ ] SignalR 全斷時，權限異動後下一個請求即讀到新權限
- [ ] Valkey 不可用時 WebAPI fail-closed
- [ ] middleware 寫入的條目可被 L0AuthCacheService 清除與比對

### 推播（加速）

- [ ] SignalR 發送 `PermissionCacheInvalidated`
- [ ] Client 收到後提前清除 L0 Auth / Menu
- [ ] Duplicate notification 不會造成錯誤

### Authorization

- [ ] `ARM_USERS.IS_ACTIVE` 正確生效
- [ ] `ARM_ROLES.IS_ACTIVE` 正確生效
- [ ] `ARM_USER_ROLES.IS_ACTIVE` 正確生效
- [ ] `ARM_PERMISSIONS.IS_ACTIVE` 正確生效
- [ ] `ARM_ACTION_DEF.IS_ACTIVE` 正確生效

---

# 37. 最終架構圖

```text
                           ┌─────────────────────┐
                           │   Admin / Mutation  │
                           └──────────┬──────────┘
                                      │
                                      ▼
                         ┌─────────────────────────┐
                         │ Oracle Transaction      │
                         │                         │
                         │ Business Mutation       │
                         │        +                │
                         │ Outbox INSERT           │
                         └──────────┬──────────────┘
                                    │ COMMIT
                                    ▼
                         ┌─────────────────────────┐
                         │ ARM_OUTBOX_EVENTS       │
                         │                         │
                         │ PENDING                 │
                         └──────────┬──────────────┘
                                    │
                                    ▼
                         ┌─────────────────────────┐
                         │      OutboxWorker       │
                         │                         │
                         │ Claim / Retry / Recover │
                         └──────────┬──────────────┘
                                    │
                                    ▼
                    ┌───────────────────────────────┐
                    │ PermissionChangeDispatcher    │
                    └──────────────┬────────────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              │                    │                    │
              ▼                    ▼                    ▼
        Event Handler       PermissionImpact      CacheSync
              │                    │                    │
              │                    ▼                    ▼
              │                 UserId[]        Valkey L2 共享 Key
              │                    │           (原子切換)
              │                    ▼
              │         INCR perm_ver:{userId}
              │                    │
              └────────────────────┴──────────────┐
                                                   ▼
                                   推播（加速，可丟失）
                                   ├── Pub/Sub → ARM L1 Evict
                                   └── SignalR → WebAPI L0 Evict

  ───────────────────────────────────────────────────────────
  讀取路徑（每個受保護請求）

  WebAPI L0 / ARM L1 命中
        ↓
  GET perm_ver:{userId}  （與 Session 查詢同 pipeline）
        ↓
  條目版本 == 當前版本？
    是 → 使用快取
    否 → 丟棄、重驗、存新版本
```

---

# 38. 核心原則總結

整個架構可以濃縮成六句話：

1. **Oracle 是 Source of Truth。**
2. **Business Mutation 與 Outbox Event 必須同 Transaction。**
3. **OutboxWorker 負責可靠地把 Event 推進 Cache / Notification Pipeline。**
4. **PermissionImpactService 只負責找真正受影響的 User。**
5. **Valkey Pub/Sub 負責跨 ARM Instance 的 L1 Invalidation，SignalR 負責通知 Client。**
6. **所有 Handler 必須可以重複執行，因為 Outbox 採 At-Least-Once Delivery。**

最終形成：

```text
Oracle
  ↓
Outbox
  ↓
Worker
  ↓
Dispatcher
  ↓
Handler
  ├── Valkey L2
  ├── Pub/Sub → ARM L1
  ├── Impact → UserId[]
  └── SignalR → Client L0
```

這個結構的核心不是把系統拆得越多越漂亮，而是把「資料異動」、「可靠事件傳遞」、「Cache Invalidation」與「Client Notification」的責任切乾淨，同時保留目前 `OracleDbService + CacheSyncService + PermissionImpactService + Handler` 的簡潔架構。

---

## 39. 相關程式位置

目前專案中與本架構直接相關的主要位置：

```text
Services
├── Cache
│   ├── CacheSyncService.cs
│   └── CacheTokenManager.cs
├── Dispatch
│   ├── IPermissionChangeDispatcher.cs
│   └── PermissionChangeDispatcher.cs
├── Handlers
│   ├── Interfaces/IPermissionEventHandler.cs
│   └── PermissionEventHandlers.cs
├── Notification
│   └── ArmNotificationService.cs
└── PermissionImpact
    └── PermissionImpactService.cs
```

此外：

```text
Services/HostedServices/OutboxWorker.cs
Services/HostedServices/CacheDbPubSubBackgroundService.cs   （加速，Phase 7 評估）
Data/OutboxDbService.cs
Data/ValkeyDbService.cs
Services/Auth/AuthService.cs                                  （L1 版本比對）
HIS2_ARM_KeyDB_Client/Middlewares/ArmAuthorizationMiddleware.cs
HIS2_ARM_KeyDB_Client/Services/L0AuthCacheService.cs
HIS2_ARM_KeyDB_Client/Services/MenuCacheService.cs
HIS2_ARM_KeyDB_Client/Services/Notification/ArmNotificationObserver.cs
```

---

**文件狀態：Outbox Phase 1–4 已實作；Phase 5（權限版本與安全修正）待實作**

**最後整理重點：** Outbox 保證「資料改了，後續該做的事一定有機會被可靠地做完」；`perm_ver` 保證「本機快取在讀取時一定能察覺自己已過期」。兩者合起來，正確性不再依賴任何推播是否送達。
