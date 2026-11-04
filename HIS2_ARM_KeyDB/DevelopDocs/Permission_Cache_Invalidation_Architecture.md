# Permission Cache Invalidation 架構說明

## 1. 目的

本文件說明 HIS2 ARM V2 權限異動後的 **Cache Invalidation（快取失效）架構**。

核心目標是將「權限異動」與「受影響使用者的計算」、「快取失效通知」分離，讓 Controller 不需要直接處理複雜的權限影響範圍與分散式快取同步。

---

## 2. 整體架構

```text
                    ┌──────────────────────────────┐
                    │ IPermissionChangedEvent      │
                    │      權限異動事件            │
                    └──────────────┬───────────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              ▼                    ▼                    ▼
      UserRoleChanged      RolePermissionChanged   ActionRequirementChanged
              │                    │                    │
              └────────────────────┼────────────────────┘
                                   ▼
                    IPermissionChangeDispatcher
                                   │
                                   ▼
                     PermissionChangeDispatcher
                                   │
                         找到對應 Handler
                                   │
              ┌────────────────────┼────────────────────┐
              ▼                    ▼                    ▼
        UserRoleHandler      RolePermissionHandler   ActionRequirementHandler
              │                    │                    │
              │             PermissionImpactService    │
              │                    │                    │
              │               Role → User              │
              │                                        │
              │                              Action → Role → User
              │                    │                    │
              └────────────────────┼────────────────────┘
                                   ▼
                     IArmNotificationService
                                   │
                                   ▼
                                SignalR
                                   │
                                   ▼
                         所有 AP Server 節點
                                   │
                                   ▼
                         L1 Cache Invalidation
```

---

## 3. 設計核心概念

### 3.1 Event：描述「發生了什麼」

所有權限異動事件實作共同的 `IPermissionChangedEvent`。

目前主要有三種事件：

| Event | 說明 | Impact 計算 |
|---|---|---|
| `UserRoleChangedEvent` | 使用者角色綁定異動 | 不需要額外查詢 |
| `RolePermissionChangedEvent` | 角色權限異動 | Role → User |
| `ActionRequirementChangedEvent` | API 所需權限異動 | Action → Role → User |

共同介面：

```csharp
public interface IPermissionChangedEvent
{
}
```

這個介面本身不負責任何業務邏輯，主要用途是讓 Dispatcher 可以統一接收所有權限異動事件。

---

## 4. Dispatcher

### 4.1 職責

`PermissionChangeDispatcher` 是權限異動事件的分派中樞。

它不應該知道：

- 如何計算受影響的 User
- 如何查詢 Role → User
- 如何查詢 Action → Role → User
- 如何清除 Cache
- 如何發送 SignalR

它只負責：

> 根據 Event 找到正確的 Handler，並交給 Handler 處理。

概念上：

```text
Event
  │
  ▼
Dispatcher
  │
  ├── UserRoleChangedEvent
  │       └── UserRoleChangedEventHandler
  │
  ├── RolePermissionChangedEvent
  │       └── RolePermissionChangedEventHandler
  │
  └── ActionRequirementChangedEvent
          └── ActionRequirementChangedEventHandler
```

### 4.2 Handler 唯一性

目前 Dispatcher 使用 Handler 的 `CanHandle()` 判斷是否能處理某個 Event。

建議維持以下架構契約：

> **一個 Event 應該對應一個 Handler。**

也就是：

- 0 個 Handler：代表系統沒有能力處理此 Event，應明確記錄或拋出錯誤。
- 1 個 Handler：正常。
- 超過 1 個 Handler：代表 DI 設定或架構出現歧義，應明確失敗，而不是默默取第一個。

---

## 5. Handler

Handler 是真正執行「權限異動後續處理」的元件。

### 5.1 UserRoleChangedEventHandler

使用者角色變更時，Event 已經包含受影響的 UserId。

因此不需要額外計算：

```text
UserRoleChangedEvent
        │
        ▼
     UserIds
        │
        ▼
NotifyPermissionCacheInvalidated
```

---

### 5.2 RolePermissionChangedEventHandler

角色權限變更時，只知道哪些 Role 發生變更。

因此需要透過 `IPermissionImpactService`：

```text
RolePermissionChangedEvent
        │
        ▼
     RoleIds
        │
        ▼
PermissionImpactService
        │
        ▼
     Role → User
        │
        ▼
    UserIds
        │
        ▼
Notification
```

---

### 5.3 ActionRequirementChangedEventHandler

API 的 `REQUIRED_BITS` 發生變更時，影響範圍更廣。

需要計算：

```text
ActionRequirementChangedEvent
        │
        ▼
      Actions
        │
        ▼
PermissionImpactService
        │
        ▼
   Action → Role
        │
        ▼
      Users
        │
        ▼
    UserIds
        │
        ▼
   Notification
```

---

## 6. PermissionImpactService

`PermissionImpactService` 專門負責：

> **計算一個權限異動究竟會影響哪些 User。**

它不負責：

- 發 SignalR
- 清 L1 Cache
- 清 L2 Cache
- 修改 Oracle
- 處理 HTTP Request

目前的責任可以整理成：

```text
UserRoleChangedEvent
    └── 已知 UserIds
          ↓
       直接使用


RolePermissionChangedEvent
    └── RoleIds
          ↓
       Role → User


ActionRequirementChangedEvent
    └── Actions
          ↓
       Action → Role → User
```

這個設計可以避免 Controller 直接知道權限關聯模型。

---

## 7. Notification Service

當 Handler 得到最終的 `UserIds` 後，交給：

```csharp
IArmNotificationService
```

目前 Notification Service 的責任是：

> 將已計算完成的 Cache Invalidation 範圍透過 SignalR 發送出去。

因此 Handler 不需要知道 SignalR Hub 的細節。

```text
Handler
   │
   ▼
IArmNotificationService
   │
   ▼
SignalR
   │
   ▼
AP Servers
```

這樣未來如果通知機制從 SignalR 改成其他 Message Broker，Handler 理論上不需要修改。

---

## 8. AP Server 端的失效

SignalR 通知送達各 AP Server 後，AP Server 根據 `UserIds` 進行本機 L1 Cache Invalidation。

概念上：

```text
ARM Permission Change
        │
        ▼
PermissionChangeDispatcher
        │
        ▼
Handler
        │
        ▼
PermissionImpactService
        │
        ▼
Affected UserIds
        │
        ▼
SignalR Broadcast
        │
        ├──────────────┬──────────────┐
        ▼              ▼              ▼
      AP #1           AP #2          AP #N
        │              │              │
        ▼              ▼              ▼
     L1 Cache        L1 Cache       L1 Cache
     Invalidate      Invalidate     Invalidate
```

因此單一 AP Server 上的權限異動，不需要依賴其他 AP Server 的本機記憶體狀態。

---

## 9. Transaction 與 Event Dispatch 的時序

權限異動 Controller 最重要的原則是：

> **Oracle Transaction 必須先成功 Commit，再 Dispatch 權限異動 Event。**

正確流程：

```text
HTTP Request
    │
    ▼
Validate Request
    │
    ▼
Oracle Transaction
    │
    ▼
Update Permission Data
    │
    ▼
COMMIT
    │
    ▼
Create IPermissionChangedEvent
    │
    ▼
PermissionChangeDispatcher
    │
    ▼
Handler
    │
    ▼
Impact Calculation
    │
    ▼
SignalR Notification
```

不能在 Commit 前通知：

```text
Oracle UPDATE
    │
    ▼
Dispatch Event
    │
    ▼
AP Servers 清 Cache
    │
    ▼
Transaction ROLLBACK  ← ❌
```

否則可能產生：

> Cache 已經失效，但 Oracle 實際上沒有完成權限變更。

因此 Event Dispatch 應該發生在資料庫異動成功之後。

---

## 10. Controller 的責任

Controller 應該維持薄層。

理想上只負責：

1. 驗證 Request
2. 執行 Oracle Transaction
3. Commit
4. 建立對應 Event
5. 呼叫 Dispatcher

例如：

```csharp
// 💡 Oracle Transaction 成功提交後，才發布權限異動事件
await _dispatcher.DispatchAsync(
    new RolePermissionChangedEvent
    {
        RoleIds = [request.RoleId]
    },
    cancellationToken);
```

Controller 不應該自行：

```text
❌ 計算 Role → User
❌ 計算 Action → Role → User
❌ 直接呼叫 SignalR
❌ 直接遍歷所有 AP Server
❌ 把權限異動邏輯寫死在 Controller
```

---

## 11. 架構責任分層

| 元件 | 責任 |
|---|---|
| Controller | 接收 Request、修改資料、Commit、建立 Event |
| Event | 描述發生哪一種權限異動 |
| Dispatcher | 找到正確 Handler |
| Handler | 執行該類型權限異動的處理流程 |
| PermissionImpactService | 計算受影響的 User |
| NotificationService | 發送 Cache Invalidation 通知 |
| SignalR | 跨 AP Server 廣播 |
| AP Server Cache | 執行本機 L1 Cache Invalidation |

核心原則：

> **每一層只處理自己知道的事情。**

---

## 12. 與舊式直接失效模式的差異

舊式做法容易變成：

```text
Controller
   │
   ├── Update Oracle
   ├── Delete KeyDB
   ├── Query affected Users
   ├── Clear L1 Cache
   ├── Publish Redis
   └── Send SignalR
```

這會讓 Controller 逐漸變成權限異動邏輯的巨大聚集點。

新的架構則是：

```text
Controller
   │
   ▼
Event
   │
   ▼
Dispatcher
   │
   ▼
Handler
   │
   ▼
Impact
   │
   ▼
Notification
```

因此 Controller 與後續權限影響處理解耦。

---

## 13. 三種異動的完整路徑

### User Role Change

```text
User Role Change
      │
      ▼
UserRoleChangedEvent
      │
      ▼
Dispatcher
      │
      ▼
UserRoleChangedEventHandler
      │
      ▼
UserIds
      │
      ▼
Notification
      │
      ▼
SignalR
      │
      ▼
L1 Cache Invalidation
```

### Role Permission Change

```text
Role Permission Change
      │
      ▼
RolePermissionChangedEvent
      │
      ▼
Dispatcher
      │
      ▼
RolePermissionChangedEventHandler
      │
      ▼
PermissionImpactService
      │
      ▼
Role → User
      │
      ▼
UserIds
      │
      ▼
Notification
      │
      ▼
SignalR
      │
      ▼
L1 Cache Invalidation
```

### Action Requirement Change

```text
Action Requirement Change
      │
      ▼
ActionRequirementChangedEvent
      │
      ▼
Dispatcher
      │
      ▼
ActionRequirementChangedEventHandler
      │
      ▼
PermissionImpactService
      │
      ▼
Action → Role → User
      │
      ▼
UserIds
      │
      ▼
Notification
      │
      ▼
SignalR
      │
      ▼
L1 Cache Invalidation
```

---

## 14. 設計上的主要優點

### 14.1 Controller 解耦

Controller 不需要知道權限異動會影響哪些 User。

### 14.2 Event 可擴充

未來增加新的權限異動類型時，可以新增：

```text
NewPermissionChangedEvent
NewPermissionChangedEventHandler
```

而不需要修改既有 Handler。

### 14.3 Impact 計算集中

所有「誰會受到影響」的判斷集中在 `PermissionImpactService`。

### 14.4 Notification 解耦

權限業務邏輯不直接依賴 SignalR。

### 14.5 支援分散式 AP Server

權限異動只需要在中央處理一次，之後透過 SignalR 將失效訊息廣播到各 AP Server。

---

## 15. 目前架構的核心契約

整個設計可以濃縮成四句話：

> **Event 說明發生什麼事。**

> **Dispatcher 決定誰來處理。**

> **ImpactService 決定誰受到影響。**

> **NotificationService 負責把失效訊息送出去。**

Controller 則只負責把「資料庫中的權限異動」轉換成「權限異動事件」。

---

## 16. 最終目標

完成所有權限異動 Controller 後，整體流程應該收斂成：

```text
                 Oracle
                   │
             Permission Change
                   │
                   ▼
               Controller
                   │
                COMMIT
                   │
                   ▼
                Event
                   │
                   ▼
              Dispatcher
                   │
                   ▼
                Handler
                   │
                   ▼
          Permission Impact
                   │
                   ▼
             Affected Users
                   │
                   ▼
             Notification
                   │
                   ▼
                SignalR
                   │
        ┌──────────┼──────────┐
        ▼          ▼          ▼
      AP #1       AP #2      AP #N
        │          │          │
        ▼          ▼          ▼
      L1 Cache   L1 Cache   L1 Cache
       Evict      Evict      Evict
```

這條鏈就是 ARM V2 的 **Permission Cache Invalidation Pipeline**。
