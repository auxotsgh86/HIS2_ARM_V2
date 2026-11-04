# HIS2-ARM-KeyDB 已完成端點清單

> 💡 全站套用 `app.UsePathBase("/ArmV2")`，對外實際路徑需自行加上 `/ArmV2` 前綴。
> 例如 `POST /api/v1/Auth/login` 實際對外為 `POST /ArmV2/api/v1/Auth/login`。
>
> 除另有標註，所有端點皆受全域 `FallbackPolicy`（需登入）與
> `SelfArmAuthorizationMiddleware`（BITAND 權限檢查）保護；
> 標記 `[AllowAnonymous]` / `[BypassArmAuth]` 者會跳過對應檢查。

最後更新：2026-10-05（依現有程式碼反查，非手動維護；新增 RolesController.UpdateRoleStatus 打通 RoleStatusChanged 死代碼；移除繞過 Outbox 的 OracleDbService.UpsertPermissionAsync）

---

## 1. AuthController — `/api/v1/Auth`
中央身分認證與授權核心 API。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| POST | `/api/v1/Auth/login` | `Login` | 帳密登入，核發 Access/Refresh JWT Cookie，並同步寫入舊版 Oracle Session | `[AllowAnonymous]` `[BypassArmAuth]` |
| POST | `/api/v1/Auth/verify` | `VerifyAccess` | 驗證 Token 是否具備指定 System/Controller/Action 權限 | `[AllowAnonymous]` `[BypassArmAuth]` |
| POST | `/api/v1/Auth/refresh` | `Refresh` | 以 Refresh Token Cookie 換發新的 Access/Refresh Token | `[AllowAnonymous]` `[BypassArmAuth]` |
| POST | `/api/v1/Auth/logout` | `Logout` | 註銷 Token 並清除 Cookie | `[AllowAnonymous]` |
| GET  | `/api/v1/Auth/legacy-token` | `GetLegacyToken` | 取得供舊版 HIS 系統跨系統跳轉用的 Legacy Token | `[BypassArmAuth]`（需登入） |
| POST | `/api/v1/Auth/login-legacy` | `LoginWithLegacyToken` | 以 Legacy Token 換發 ArmV2 JWT Cookie（舊系統 SSO 跳轉用） | `[AllowAnonymous]` `[BypassArmAuth]` |

---

## 2. BootstrapController — `/api/v1/Bootstrap`
子系統 API 拓撲註冊與快取預熱。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| POST | `/api/v1/Bootstrap/register-endpoints/{systemId}` | `RegisterEndpoints` | 接收子系統自動上報的 API 拓撲，落地 Oracle 並預熱該系統 `sys:required` / `role:perm` 快取 | `[AllowAnonymous]` `[BypassArmAuth]` |
| POST | `/api/v1/Bootstrap/warmup-legacy-permissions/{systemId}` | `WarmupLegacyPermissions` | 維運用手動觸發，重新預熱指定系統的角色權限與 Action 門檻快取 | 需登入 + BITAND 權限 |

---

## 3. AdminController — `/api/Admin`
權限異動管理（寫 Oracle + Outbox 事件）與讀取端點（供編輯前查詢現況）。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| GET  | `/api/Admin/permission-bits` | `GetPermissionBits` | **【補齊缺口】** 查詢 Permission Bit 的 metadata 定義清單（單一真實來源），避免前端寫死 C/R/U/D/P | 回傳 `List<PermissionBitDefinition>`（`Bit`/`Code`/`Name`），前端應 `permissions.forEach(p => renderSwitch(p))` 泛用渲染，而非逐一寫死 `renderCreate()`/`renderRead()`/...；未來新增 `Approve`/`Export`/`Sign` 只需後端在 `CrudBit.Definitions` 多加一筆，UI 不用改 |
| GET  | `/api/Admin/systems` | `GetSystems` | **【補齊缺口 + Server-side search】** 查詢系統主檔清單（`ARM_SYSTEM`，分頁），供「Action 門檻」頁最上層的 System 搜尋欄位使用 | 支援 `?keyword=`（比對 `SystemId` 或 `SystemName`）/ `?includeInactive=` / `?page=` / `?pageSize=`；**不會一次回傳全部**（ARM_SYSTEM 可能有數百筆）。回傳 `PagedResult<ArmSystem>` |
| GET  | `/api/Admin/actions` | `GetActions` | **【補齊缺口】** 查詢 Action 門檻清單（`System`/`Controller`/`Action` × 動態 Permission Bits），回答「這個 Action 現在需要什麼門檻」，供 `UpdateActionRequirement`/`UpdateControllerActionRequirement` 編輯前先行讀取 | 可選 `?systemId=`/`?controller=`/`?includeInactive=`；回傳 `List<ActionDefDetail>`（含 `RequiredBits`/`RequiredDisplay`/`Flags`，`Flags` 為 `List<PermissionBitFlag>`，資料驅動不寫死） |
| POST | `/api/Admin/update-role-permission` | `UpdateRolePermission` | Upsert 角色於指定 System/Controller(/Action) 的 `PERMS_BITS`，並排入 `RolePermissionChanged` Outbox 事件 | |
| PUT  | `/api/Admin/actions/required-bits` | `UpdateActionRequirement` | Upsert 單一 Action 的 `REQUIRED_BITS` 門檻，排入 `ActionRequirementChanged` Outbox 事件 | |
| PUT  | `/api/Admin/actions/required-bits/controller` | `UpdateControllerActionRequirement` | 批次更新指定 Controller 底下所有既有 Action 的 `REQUIRED_BITS`（不會新增不存在的 Action） | 無資料時回 404 |
| POST | `/api/Admin/force-refresh-role/{roleId}` | `ForceRefreshRolePermissions` | 純快取刷新工具：不異動 Oracle，重用 `RolePermissionChanged` Pipeline 強制重載 L2/L1/L0 快取 | 維運工具 |

### 3.1 `GetPermissionBits` / `GetSystems` / `GetActions` 使用範例

```
# Permission Bit metadata（前端渲染 CRUDP 開關的唯一資料來源，不寫死）
GET /api/Admin/permission-bits

# System 搜尋（輸入框打字即查，分頁，不會一次撈全部）
GET /api/Admin/systems?keyword=MRA&page=1&pageSize=20

# 查看 HIS2_MRA 系統底下所有 Controller/Action 的門檻（可在前端依 Controller 分組成樹狀結構）
GET /api/Admin/actions?systemId=HIS2_MRA

# 只看 HIS2_MRA/MraC010 底下的 Action 門檻
GET /api/Admin/actions?systemId=HIS2_MRA&controller=MraC010
```

`GetPermissionBits` 回傳格式（`List<PermissionBitDefinition>`）—— 這是整個系統**唯一**的 Permission Bit 定義來源：

```json
[
  { "bit": 1,  "code": "C", "name": "新增" },
  { "bit": 2,  "code": "R", "name": "讀取" },
  { "bit": 4,  "code": "U", "name": "修改" },
  { "bit": 8,  "code": "D", "name": "刪除" },
  { "bit": 16, "code": "P", "name": "列印" }
]
```

`GetSystems` 回傳格式（`PagedResult<ArmSystem>`）：

```json
{
  "items": [
    { "systemId": "HIS2_MRA", "systemName": "病歷管理", "sortValue": 20, "isActive": true, ... }
  ],
  "totalCount": 240,
  "page": 1,
  "pageSize": 20
}
```

`GetActions` 回傳格式（`List<ActionDefDetail>`），`flags` 為動態展開的 Permission Bit 清單（對應 `GetPermissionBits` 的 code/name），前端可直接 `forEach` 渲染，不需要寫死欄位：

```json
[
  {
    "systemId": "HIS2_MRA", "controller": "MraC010", "actionName": "Index",
    "requiredBits": 2, "requiredDisplay": "R",
    "flags": [
      { "bit": 1,  "code": "C", "name": "新增", "value": false },
      { "bit": 2,  "code": "R", "name": "讀取", "value": true },
      { "bit": 4,  "code": "U", "name": "修改", "value": false },
      { "bit": 8,  "code": "D", "name": "刪除", "value": false },
      { "bit": 16, "code": "P", "name": "列印", "value": false }
    ]
  },
  {
    "systemId": "HIS2_MRA", "controller": "MraC010", "actionName": "Save",
    "requiredBits": 5, "requiredDisplay": "C,U",
    "flags": [
      { "bit": 1,  "code": "C", "name": "新增", "value": true },
      { "bit": 2,  "code": "R", "name": "讀取", "value": false },
      { "bit": 4,  "code": "U", "name": "修改", "value": true },
      { "bit": 8,  "code": "D", "name": "刪除", "value": false },
      { "bit": 16, "code": "P", "name": "列印", "value": false }
    ]
  }
]
```

> 💡 前端「Action 門檻」頁的建議串接流程：
> `GetPermissionBits`（取得目前所有 Permission Bit 定義，驅動表頭/開關渲染） →
> `GetSystems?keyword=...`（Server-side 搜尋選定 System，絕不一次全撈） → `GetActions?systemId=...`（讀取 Controller/Action 階層與目前門檻，`flags` 直接對應 `permission-bits` 動態渲染） →
> 編輯後依顆粒度呼叫 `UpdateActionRequirement`（單一 Action）或 `UpdateControllerActionRequirement`（整個 Controller 批次）寫回。
>
> 💡 **單一真實來源**：`CrudBit.Definitions`（`Models/CrudBit.cs`）是整個系統唯一的 Permission Bit 定義，
> `GetPermissionBits`、`RolePermDetail.Flags`、`ActionDefDetail.Flags` 全部從同一處衍生，
> 未來新增 `Approve`/`Export`/`Sign` 只需要維護這一個檔案。

---

## 4. UsersController — `/api/admin/Users`
全院使用者帳號生命週期管理（CRUD）。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| POST | `/api/admin/Users` | `CreateUser` | 建立新使用者帳號 | |
| GET  | `/api/admin/Users/{userId}` | `GetUser` | 查詢單一使用者基本資料 | |
| PUT  | `/api/admin/Users/{userId}` | `UpdateUser` | 更新使用者基本資料（含停權設定），排入 `UserStatusChanged` Outbox 事件 | |
| DELETE | `/api/admin/Users/{userId}` | `DisableUser` | 軟刪除使用者（停權），排入 `UserStatusChanged` Outbox 事件並強制作廢其 Session | |

---

## 5. RolesController — `/api/admin/Roles`
角色主檔（`ARM_ROLES`）查詢與狀態管理，供前端「角色權限」頁左側的「搜尋角色」欄位使用。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| GET | `/api/admin/Roles` | `GetRoles` | 查詢角色清單（分頁），支援 `Keyword`（比對 `RoleId` 或 `RoleName`）/ `IncludeInactive` / `Page` / `PageSize` | 預設只回傳 `IS_ACTIVE='Y'` 且 `IS_DC='N'` 的有效角色；回傳 `PagedResult<Role>` |
| GET | `/api/admin/Roles/{roleId}` | `GetRole` | 查詢單一角色主檔資料（`RoleId`/`RoleName`/`Description`/`IsActive`） | 查無資料回 404 |
| GET | `/api/admin/Roles/{roleId}/permissions` | `GetRolePermissions` | **【補齊缺口】** 查詢指定角色目前完整的權限矩陣（`System`/`Controller`/`Action` × 動態 Permission Bits），回答「這個 Role 現在到底有什麼權限」，供 `AdminController.UpdateRolePermission` 編輯前先行讀取 | 可選 `?systemId=` 篩選單一系統；不帶則自動彙整該角色目前有效權限分佈的所有系統。回傳 `List<RolePermDetail>`（含 `SystemId`/`Controller`/`ActionName`/`PermsBits`/`PermsDisplay`/`Flags`，`Flags` 資料驅動不寫死，對應 `GetPermissionBits`） |
| PUT | `/api/admin/Roles/{roleId}/status` | `UpdateRoleStatus` | **【補齊缺口】** 啟用/停用角色（`ARM_ROLES.IS_ACTIVE`），`{ "isActive": bool }` | 排入 `RoleStatusChanged` Outbox 事件；找不到角色回 404。💡 稽核發現：此端點補上之前，`RoleStatusChangedEvent`/`RoleStatusChangedEventHandler`/`PermissionImpactService` 的對應處理邏輯皆已完整實作，卻完全沒有任何端點會產生這個事件，是消費端全通、生產端缺席的死代碼，現已打通 |

### 5.1 使用範例

```
# 搜尋角色（輸入框打字即查）
GET /api/admin/Roles?keyword=管理員&page=1&pageSize=20

# 取得單一角色詳情
GET /api/admin/Roles/SysAdmin

# 查看該角色「目前」的完整權限矩陣（跨所有服務中的子系統）
GET /api/admin/Roles/SysAdmin/permissions

# 只看該角色在 HIS2_MRA 系統底下的權限矩陣
GET /api/admin/Roles/SysAdmin/permissions?systemId=HIS2_MRA

# 停用角色（該角色所屬使用者的授權快取會被立即標記失效並 SignalR 廣播）
PUT /api/admin/Roles/SysAdmin/status
{ "isActive": false }
```

`GetRoles` 回傳格式（`PagedResult<Role>`）：

```json
{
  "items": [
    { "roleId": "SysAdmin", "roleName": "系統管理員", "description": "...", "isActive": true }
  ],
  "totalCount": 12,
  "page": 1,
  "pageSize": 20
}
```

`GetRolePermissions` 回傳格式（`List<RolePermDetail>`），`flags` 為動態展開的 Permission Bit 清單（對應 `GetPermissionBits`，資料驅動不寫死）：

```json
[
  {
    "systemId": "HIS2_MRA", "controller": "MraC010", "actionName": "Index",
    "permsBits": 31, "permsDisplay": "C,R,U,D,P",
    "flags": [
      { "bit": 1,  "code": "C", "name": "新增", "value": true },
      { "bit": 2,  "code": "R", "name": "讀取", "value": true },
      { "bit": 4,  "code": "U", "name": "修改", "value": true },
      { "bit": 8,  "code": "D", "name": "刪除", "value": true },
      { "bit": 16, "code": "P", "name": "列印", "value": true }
    ]
  },
  {
    "systemId": "HIS2_MRA", "controller": "MraC010", "actionName": "Save",
    "permsBits": 15, "permsDisplay": "C,R,U,D",
    "flags": [
      { "bit": 1,  "code": "C", "name": "新增", "value": true },
      { "bit": 2,  "code": "R", "name": "讀取", "value": true },
      { "bit": 4,  "code": "U", "name": "修改", "value": true },
      { "bit": 8,  "code": "D", "name": "刪除", "value": true },
      { "bit": 16, "code": "P", "name": "列印", "value": false }
    ]
  }
]
```

> 💡 前端「角色權限」頁的建議串接流程：
> `AdminController.GetPermissionBits`（取得 Permission Bit 定義，驅動表頭渲染） →
> `GetRoles`（搜尋/選定 RoleId） → `GetRolePermissions`（讀取目前權限矩陣，`flags` 直接 forEach 渲染） →
> 編輯後呼叫 `AdminController.UpdateRolePermission` 寫回 → `UserMgrController.GetUserData?roleId=...`（目前成員） →
> 需要新增成員時改用 `?excludeRoleId=...`（候選名單） → `UserMgrController.AssignUsersToRole`（批次加入）。

---

## 6. UserMgrController — `/api/admin/UserMgr`
使用者角色指派與 Session 管理。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| POST | `/api/admin/UserMgr/AddOneUser` | `AddOneUser` | 新增單一使用者（含初始角色指派） | |
| POST | `/api/admin/UserMgr/BatchAddUsers` | `BatchAddUsers` | 批次**建立全新**使用者帳號（需密碼） | 對象是不存在的使用者 |
| GET  | `/api/admin/UserMgr/GetUserData` | `GetUserData` | 查詢使用者清單（分頁），支援 `UserId`/`RoleId`/`ExcludeRoleId`/`Department`/`Keyword`/`Page`/`PageSize` 篩選 | 帶 `RoleId` → Role 目前成員清單；帶 `ExcludeRoleId` → 新增成員候選名單（排除已是成員者）。回傳 `PagedResult<T>`（`Items`/`TotalCount`/`Page`/`PageSize`） |
| POST | `/api/admin/UserMgr/AssignUsersToRole` | `AssignUsersToRole` | 批次將**既有**使用者加入指定角色（`{ roleId, userIds[] }`），對應「選取多人 → 一次送出 → 加入 Role」情境 | 只異動 `ARM_USER_ROLES`（MERGE 冪等），排入 `UserRoleChanged` Outbox 事件；不會建立新帳號 |
| POST | `/api/admin/UserMgr/UpdOneUser` | `UpdOneUser` | 更新使用者（角色重新指派 + 啟用狀態），排入 `UserRoleChanged` + `UserStatusChanged` Outbox 事件 | |
| POST | `/api/admin/UserMgr/DeleteOneSession` | `DeleteOneSession` | 強制刪除單一 Session（依 Jti） | 即時操作，不走 Outbox |
| GET  | `/api/admin/UserMgr/GetUserSessions` | `GetUserSessions` | 查詢指定使用者目前所有在線 Session | |
| GET  | `/api/admin/UserMgr/GetOnlineUsers` | `GetOnlineUsers` | 查詢目前所有在線使用者及各自 Session 清單（掃描 `user_sessions:*`） | |

### 6.1 `GetUserData` / `AssignUsersToRole` 使用範例

```
# Role → 目前成員（分頁 + 關鍵字搜尋）
GET /api/admin/UserMgr/GetUserData?roleId=SysAdmin&page=1&pageSize=20&keyword=王

# 新增成員候選名單（排除已是 SysAdmin 成員者）
GET /api/admin/UserMgr/GetUserData?excludeRoleId=SysAdmin&page=1&pageSize=20&keyword=王

# 選取多人 → 一次送出 → 加入 Role
POST /api/admin/UserMgr/AssignUsersToRole
{
  "roleId": "SysAdmin",
  "userIds": ["U001", "U002", "U003"]
}
```

`GetUserData` 回傳格式（`PagedResult<T>`）：

```json
{
  "items": [ { "userId": "U001", "username": "王小明", "roleIds": [], "department": "資訊室", "isActive": true, ... } ],
  "totalCount": 42,
  "page": 1,
  "pageSize": 20
}
```

---

## 7. MenuController — `/api/system`
前端選單樹狀結構。

| Method | Route | Action | 說明 | 備註 |
|---|---|---|---|---|
| GET | `/api/system/His2Menu` | `GetMyMenus` | 依登入者目前角色，回傳可見的系統/選單/功能樹 | `[Authorize]` + `[BypassArmAuth]`（跳過 BITAND 檢查，但仍需登入） |

---

## 8. Razor Pages（後台管理介面，非 JSON API）

| Route | 頁面 | 說明 |
|---|---|---|
| `/Account/Login` | `Pages/Account/Login.cshtml` | 後台登入頁 |
| `/Account/Logout` | `Pages/Account/Logout.cshtml` | 後台登出頁 |
| `/Admin/Users` | `Pages/Admin/Users.cshtml` | 使用者管理頁（對應快取 Key `sys:required:HIS2_ARM_V2:Pages.Admin.Users`） |
| `/Admin/PermMatrix` | `Pages/Admin/PermMatrix.cshtml` | 權限矩陣管理頁（對應快取 Key `sys:required:HIS2_ARM_V2:Pages.Admin.PermMatrix`） |

---

## 9. 其他基礎設施端點

| Method | Route | 說明 | 備註 |
|---|---|---|---|
| GET | `/health` | 健康檢查，回報 ValkeyDB 連線狀態 | `.AllowAnonymous()`，Minimal API（非 Controller） |

---

## 10. ⚠️ 已停用（未編譯，不計入「已完成」）的 Controller

以下檔案副檔名被改為 `_.cs_`（不符合 `*.cs` 編譯規則），目前**不會被組譯進執行檔**，僅供歷史參考：

- `Controllers/UserController_.cs_`
- `Controllers/UserMgrController_.cs_`（已由現行 `UserMgrController.cs` 取代重建）
- `Controllers/PermMgrController_.cs_`
- `Controllers/PermissionImpactController_._cs_`

> 若之後要重新啟用，需確認其依賴的類別（如 `KeyDbService` / `PermissionService` / `AuthorizeActionAttribute` / `ApiResult`）是否仍存在，目前這些類別多半已被 `ValkeyDbService` / `AuthService` / `SelfArmAuthorizationMiddleware` 取代。
>
> ⚠️ **特別提醒**：`PermMgrController_.cs_` 原本唯一呼叫的 `OracleDbService.UpsertPermissionAsync`（單筆 Permission MERGE，**沒有**寫入 Outbox）已於稽核後移除。若要重新啟用本 Controller，請改呼叫 `AdminController.UpdateRolePermission` 的邏輯（或 `BatchUpsertPermissionsAsync`），確保 Permission 的異動一律會經過 Outbox 觸發快取失效，不要重新加回繞過 Outbox 的寫入路徑。

---

## 11. 📋 端點 ↔ Outbox 連動性稽核紀錄（2026-10-05）

逐一追蹤「Controller 端點 → `IOutboxService.EnqueueAsync`（同一 Transaction）→ `OutboxWorker` → `IPermissionChangeDispatcher` → Handler → 快取/Session 失效」整條鏈路後的結論：

- ✅ 所有會異動 `ARM_PERMISSIONS`/`ARM_ACTION_DEF`/`ARM_USER_ROLES`/`ARM_USERS.IS_ACTIVE` 的寫入端點，皆正確在**同一個 Oracle Transaction** 內呼叫 `_outbox.EnqueueAsync`，Business Mutation 與 Outbox INSERT 不可分割。
- ✅ `OutboxWorker.DeserializePayload` 的 5 種 `EventType`、`OutboxEventTypes` 常數、5 個 `IPermissionEventHandler` 實作、`IPermissionImpactService` 的對應 Overload，彼此對應完整，無漏接。
- ✅ `BootstrapController.RegisterEndpoints`/`WarmupLegacyPermissions`、`UserMgrController.DeleteOneSession`、`UserMgrController.AddOneUser`/`BatchAddUsers` 刻意不經 Outbox，皆為**設計上安全**的例外（新資源無既存快取可失效 / 即時單一 Session 操作 / L0 負向快取 TTL ≤3 秒自然收斂），非缺口。
- 🔴→✅ **已修復**：`RoleStatusChangedEvent` 原本消費端（Handler/Dispatcher/Worker/ImpactService）全部到位，卻沒有任何生產端呼叫，是死代碼。已新增 `RolesController.UpdateRoleStatus`（`PUT /api/admin/Roles/{roleId}/status`）補上生產端，打通整條鏈路。
- 🔴→✅ **已修復**：`OracleDbService.UpsertPermissionAsync`（單筆 Permission MERGE，無 Outbox 呼叫）原本只被已停用的 `PermMgrController_.cs_` 引用，是一個「若未來重新啟用該 Controller 就會繞過 Outbox」的地雷，已直接移除。
- ℹ️ **留意但未處理**：`Services/Cache/CacheTokenManager.cs`（L1 Evict/GetChangeToken 機制）已註冊為 Singleton，但目前**全專案沒有任何地方呼叫** `GetChangeToken`/`Evict`，屬於尚未接線的既有基礎設施，不影響現有快取失效流程（現行 L1/L0 失效皆透過 SignalR + Handler 直接操作），但代表文件描述的三層快取中，Server 端 L1 這塊目前沒有實際運作，供未來擴充參考。


