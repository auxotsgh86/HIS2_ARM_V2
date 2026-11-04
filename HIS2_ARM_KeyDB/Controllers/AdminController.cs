using Dapper;
using System.Text.Json;
using HIS2_ARM_KeyDB.Data;
using HIS2_ARM_KeyDB.Models;
using HIS2_ARM_KeyDB.Models.Dto;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Models.Outbox;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

namespace HIS2_ARM_KeyDB.Controllers;

[ApiController]
[Route("api/[controller]")]
// 🛡️ 這裡可以掛上我們寫好的攔截器，限定只有 "系統管理員" 才能呼叫這支 API
// [ArmAuthorize("HIS2_MRA", "Admin", "ManagePermissions")] 
public class AdminController : ControllerBase
{
    private readonly string _rmdbConnString;
    private readonly OracleDbService _oracleDb;
    private readonly IOutboxService _outbox;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IConfiguration config, OracleDbService oracleDb, IOutboxService outbox, ILogger<AdminController> logger)
    {
        _rmdbConnString = config.GetConnectionString("Oracle") ?? string.Empty;
        _oracleDb = oracleDb;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>
    /// 【補齊缺口】查詢 Permission Bit 的 metadata 定義清單（單一真實來源）。
    ///
    /// 💡 [資料驅動，不寫死]：前端不應該寫死 renderCreate()/renderRead()/renderUpdate()/
    ///    renderDelete()/renderPrint() 這種固定的 CRUDP 渲染函式，而是：
    ///      const permissions = await fetch('/api/Admin/permission-bits').then(r => r.json());
    ///      permissions.forEach(permission => renderSwitch(permission));
    ///    未來後端在 Models/CrudBit.cs 的 Definitions 新增 Approve/Export/Sign 等操作時，
    ///    前端完全不需要改一行程式碼。
    ///
    /// 回傳格式：[ { "bit": 1, "code": "C", "name": "新增" }, { "bit": 2, "code": "R", "name": "讀取" }, ... ]
    /// </summary>
    [HttpGet("permission-bits")]
    public IActionResult GetPermissionBits()
    {
        return Ok(CrudBit.Definitions);
    }

    /// <summary>
    /// 【補齊缺口 + 強化】查詢系統主檔清單（分頁 + Keyword 搜尋），供前端「Action 門檻」頁
    /// 最上層的 System 搜尋欄位使用。
    /// 💡 [Server-side search]：ARM_SYSTEM 可能有數百筆，不會一次把全部塞進瀏覽器，
    ///    前端應做成「[ 請輸入系統名稱 / ID 搜尋... ]」的即時查詢輸入框，
    ///    每次打字呼叫本端點帶 keyword + page/pageSize，而不是載入後在前端做過濾。
    /// </summary>
    [HttpGet("systems")]
    public async Task<IActionResult> GetSystems([FromQuery] SystemQueryRequest query)
    {
        var (items, total) = await _oracleDb.GetSystemsAsync(
            query.Keyword, query.IncludeInactive, query.Page, query.PageSize);

        return Ok(new PagedResult<ArmSystem>(items.ToList(), total, query.Page, query.PageSize));
    }

    /// <summary>
    /// 【補齊缺口】查詢 Action 門檻清單（System/Controller/Action × REQUIRED_BITS），
    /// 回答「這個 Action 現在需要什麼門檻」，供 UpdateActionRequirement /
    /// UpdateControllerActionRequirement 編輯前先行讀取。
    /// 對應 UI 需求：
    ///   HIS2_MRA
    ///    └─ MraC010
    ///        ├─ Index   C R U D P
    ///        ├─ Save    C R U D P
    ///        └─ Delete  C R U D P
    /// </summary>
    [HttpGet("actions")]
    public async Task<IActionResult> GetActions(
        [FromQuery] string? systemId, [FromQuery] string? controller, [FromQuery] bool includeInactive = false)
    {
        var actions = await _oracleDb.GetActionDefDetailsAsync(systemId, controller, includeInactive);
        return Ok(actions);
    }

    [HttpPost("update-role-permission")]
    public async Task<IActionResult> UpdateRolePermission([FromBody] UpdatePermissionRequest request)
    {
        if (string.IsNullOrEmpty(request.RoleId) || string.IsNullOrEmpty(request.SystemId))
            return BadRequest("參數不完整");

        using var connection = new OracleConnection(_rmdbConnString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 為了讓 Oracle 能正確判別 NULL，我們將前端可能傳來的空字串轉為實際的 null
            string? actualActionName = string.IsNullOrWhiteSpace(request.ActionName) ? null : request.ActionName;

            // 🎬 步驟 1：更新絕對真理 (Oracle Database)
            // 💡 完美支援 Controller 通配與 Action 精細配置的 Upsert 語法
            // 💡 [修正] 欄位名稱對齊實際 DDL（init_arm_tables.sql）：
            //    CREATE_TIME / MODIFIED_TIME（原本誤寫成不存在的 CREATED_AT / UPDATED_AT，
            //    會直接炸出 ORA-00904）。另外 PERMISSION_ID 是 NOT NULL 且無 DEFAULT，
            //    INSERT 分支必須自己補上 SYS_GUID()。
            string sql = """
                         MERGE INTO HIS2USER2.ARM_PERMISSIONS p
                         USING (
                             SELECT 
                                 :SystemId AS SYS_ID, 
                                 :RoleId AS R_ID, 
                                 :ControllerName AS CTRL, 
                                 :ActionName AS ACT 
                             FROM DUAL
                         ) src
                         ON (p.SYSTEM_ID = src.SYS_ID 
                             AND p.ROLE_ID = src.R_ID 
                             AND p.CONTROLLER = src.CTRL 
                             -- 確保 NULL 比對正確
                             AND NVL(p.ACTION_NAME, '###NULL###') = NVL(src.ACT, '###NULL###'))
                         WHEN MATCHED THEN
                             UPDATE SET 
                                 p.PERMS_BITS = :NewPermsBits,
                                 p.MODIFIED_TIME = SYSDATE
                         WHEN NOT MATCHED THEN
                             INSERT (PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE, CREATE_TIME)
                             VALUES (SYS_GUID(), :SystemId, :RoleId, :ControllerName, :ActionName, :NewPermsBits, 'Y', SYSDATE)
                         """;

            await connection.ExecuteAsync(sql, new
            {
                SystemId = request.SystemId,
                RoleId = request.RoleId,
                ControllerName = request.ControllerName,
                ActionName = actualActionName,
                NewPermsBits = request.NewPermsBits
            }, transaction);

            // ==========================================================
            // 🚀 Outbox：與 Business Mutation 同一 Transaction 寫入事件，
            //    Cache Reload / Impact 計算 / Pub-Sub / SignalR 交給 OutboxWorker 非同步處理。
            // ==========================================================
            var payload = JsonSerializer.Serialize(new RolePermissionChangedEvent
            {
                RoleIds = [request.RoleId]
            });

            await _outbox.EnqueueAsync(
                connection,
                transaction,
                OutboxEventTypes.RolePermissionChanged,
                aggregateKey: request.RoleId,
                payloadJson: payload,
                createUser: User.Identity?.Name);

            // 提交資料庫變更（業務資料 + Outbox 事件同時落地）
            transaction.Commit();
            _logger.LogInformation("Oracle 權限更新成功並寫入 Outbox: Role={Role}, Target={Ctrl}, NewBits={Bits}",
                request.RoleId, request.ControllerName, request.NewPermsBits);

            return Ok(new { Message = "權限更新成功，已排入 Outbox 佇列，稍後將由背景 Worker 同步全系統快取。" });
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "更新權限失敗: System={System}, Role={Role}", request.SystemId, request.RoleId);
            return StatusCode(500, "系統發生異常，權限更新失敗");
        }
    }

    [HttpPut("actions/required-bits")]
    public async Task<IActionResult> UpdateActionRequirement([FromBody] UpdateActionRequirementRequest request)
    {
        if (string.IsNullOrEmpty(request.SystemId) ||
            string.IsNullOrEmpty(request.ControllerName) ||
            string.IsNullOrEmpty(request.ActionName))
        {
            return BadRequest("參數不完整，必須指定 SystemId, ControllerName 與 ActionName");
        }

        using var connection = new OracleConnection(_rmdbConnString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 🎬 步驟 1：使用 MERGE INTO 更新 Oracle (確保若系統升級新增功能時能順利寫入)
            // 💡 [修正] 欄位名稱對齊實際 DDL（init_arm_tables.sql）：
            //    CREATED_TIME / MODIFIED_TIME（原本誤寫成不存在的 CREATED_AT / UPDATED_AT，
            //    會直接炸出 ORA-00904: "CREATED_AT": 無效的 ID）。另外 ACTION_DEF_ID 是
            //    NOT NULL 且無 DEFAULT，INSERT 分支必須自己補上 SYS_GUID()。
            string sql = @"
            MERGE INTO HIS2USER2.ARM_ACTION_DEF a
            USING (
                SELECT 
                    :SystemId AS SYS_ID, 
                    :ControllerName AS CTRL, 
                    :ActionName AS ACT 
                FROM DUAL
            ) src
            ON (a.SYSTEM_ID = src.SYS_ID 
                AND a.CONTROLLER = src.CTRL 
                AND a.ACTION_NAME = src.ACT)
            WHEN MATCHED THEN
                UPDATE SET 
                    a.REQUIRED_BITS = :RequiredBits,
                    a.MODIFIED_TIME = SYSDATE
            WHEN NOT MATCHED THEN
                INSERT (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE, CREATED_TIME)
                VALUES (SYS_GUID(), :SystemId, :ControllerName, :ActionName, :RequiredBits, 'Y', SYSDATE)";

            await connection.ExecuteAsync(sql, new
            {
                request.SystemId,
                request.ControllerName,
                request.ActionName,
                request.RequiredBits
            }, transaction);

            // ==========================================================
            // 🚀 Outbox：Controller 層級聚合，aggregateKey 用 "{SystemId}:{Controller}"
            //    對齊 sys:required:{systemId}:{controller} 的 Key 級隔離設計。
            // ==========================================================
            var payload = JsonSerializer.Serialize(new ActionRequirementChangedEvent
            {
                Actions =
                [
                    new ActionResourceChanged
                    {
                        System = request.SystemId,
                        Controller = request.ControllerName,
                        ActionName = request.ActionName
                    }
                ]
            });

            await _outbox.EnqueueAsync(
                connection,
                transaction,
                OutboxEventTypes.ActionRequirementChanged,
                aggregateKey: $"{request.SystemId}:{request.ControllerName}",
                payloadJson: payload,
                createUser: User.Identity?.Name);

            transaction.Commit();
            _logger.LogInformation("功能門檻更新成功並寫入 Outbox: Target={Sys}/{Ctrl}/{Act}, Bits={Bits}",
                request.SystemId, request.ControllerName, request.ActionName, request.RequiredBits);

            return Ok(new { Message = $"功能門檻更新成功，已排入 Outbox 佇列。目前需求：{request.RequiredPermissions}" });
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "更新功能門檻發生異常: Target={Ctrl}/{Act}", request.ControllerName, request.ActionName);
            return StatusCode(500, "系統發生異常，功能門檻更新失敗");
        }
    }

    /// <summary>
    /// Controller 層級批次更新：把指定 Controller 底下「所有既有」Action 的 REQUIRED_BITS
    /// 一次性改成同一個值（例如整批設成至少要 R）。
    /// 💡 只會更新 ARM_ACTION_DEF 裡已經存在的列，不會新增不存在的 Action。
    ///    如果該 Controller 底下根本沒有任何 Action 定義，會回 404 提醒你要先註冊拓撲。
    /// </summary>
    [HttpPut("actions/required-bits/controller")]
    public async Task<IActionResult> UpdateControllerActionRequirement(
        [FromBody] UpdateControllerActionRequirementRequest request)
    {
        if (string.IsNullOrEmpty(request.SystemId) || string.IsNullOrEmpty(request.ControllerName))
            return BadRequest("參數不完整，必須指定 SystemId 與 ControllerName");

        using var connection = new OracleConnection(_rmdbConnString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 🎬 步驟 1：一次 UPDATE 整個 Controller 底下所有既有 Action 的門檻
            const string updateSql = """
                                      UPDATE HIS2USER2.ARM_ACTION_DEF
                                      SET REQUIRED_BITS = :RequiredBits,
                                          MODIFIED_TIME = SYSDATE
                                      WHERE SYSTEM_ID = :SystemId
                                        AND CONTROLLER = :ControllerName
                                        AND IS_ACTIVE = 'Y'
                                      """;

            var affected = await connection.ExecuteAsync(updateSql, new
            {
                request.SystemId,
                request.ControllerName,
                request.RequiredBits
            }, transaction);

            if (affected == 0)
            {
                transaction.Rollback();
                return NotFound(new
                {
                    Message = $"{request.SystemId}/{request.ControllerName} 底下查無任何已註冊的 Action，" +
                              "請先透過拓撲探索（BootstrapController.RegisterEndpoints）或逐筆新增後再批次調整。"
                });
            }

            // 🎬 步驟 2：查回這個 Controller 底下所有 ActionName，組成單一事件的 Actions[]
            const string selectSql = """
                                      SELECT ACTION_NAME
                                      FROM HIS2USER2.ARM_ACTION_DEF
                                      WHERE SYSTEM_ID = :SystemId
                                        AND CONTROLLER = :ControllerName
                                        AND IS_ACTIVE = 'Y'
                                      """;

            var actionNames = (await connection.QueryAsync<string>(selectSql, new
            {
                request.SystemId,
                request.ControllerName
            }, transaction)).ToList();

            // ==========================================================
            // 🚀 Outbox：單一事件打包整個 Controller 的所有 Action，
            //    ActionRequirementChangedEventHandler 內部會依 (System, Controller) 去重分組，
            //    所以 L2 重載（LoadControllerActionDefinitionsToCacheAsync）只會執行一次，
            //    但 Impact 計算會涵蓋這個 Controller 下所有 Action 對應到的所有角色/使用者。
            // ==========================================================
            var payload = JsonSerializer.Serialize(new ActionRequirementChangedEvent
            {
                Actions = actionNames.Select(actionName => new ActionResourceChanged
                {
                    System = request.SystemId,
                    Controller = request.ControllerName,
                    ActionName = actionName
                }).ToArray()
            });

            await _outbox.EnqueueAsync(
                connection,
                transaction,
                OutboxEventTypes.ActionRequirementChanged,
                aggregateKey: $"{request.SystemId}:{request.ControllerName}",
                payloadJson: payload,
                createUser: User.Identity?.Name);

            transaction.Commit();
            _logger.LogInformation(
                "Controller 層級功能門檻批次更新成功並寫入 Outbox: Target={Sys}/{Ctrl}, AffectedActions={Count}, Bits={Bits}",
                request.SystemId, request.ControllerName, affected, request.RequiredBits);

            return Ok(new
            {
                Message = $"{request.ControllerName} 底下共 {affected} 個 Action 已批次設定為「{request.RequiredPermissions}」，" +
                          "已排入 Outbox 佇列，稍後將由背景 Worker 同步全系統快取。",
                AffectedActions = actionNames
            });
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Controller 層級功能門檻批次更新發生異常: Target={Sys}/{Ctrl}",
                request.SystemId, request.ControllerName);
            return StatusCode(500, "系統發生異常，功能門檻批次更新失敗");
        }
    }

    /// <summary>
    /// 純快取刷新（不異動 Oracle 任何權限資料）：強制讓指定角色的 L2/L1/L0 快取全部重新載入。
    ///
    /// 適用情境：
    ///   - 手動下 SQL（例如 DDL_SQL 底下的 Seed 腳本）直接改了 ARM_PERMISSIONS，繞過 Outbox，
    ///     導致 KeyDB 與各 AP Server 的本機快取跟 Oracle 不一致。
    ///   - 懷疑快取與資料庫不一致，需要人工排查時的維運工具。
    ///
    /// 💡 [架構重用] 本端點刻意不直接呼叫 ICacheSyncService / IArmNotificationService，
    ///    而是寫入一筆與「真的改了權限」完全相同的 RolePermissionChanged Outbox 事件，
    ///    藉此重用 RolePermissionChangedEventHandler 既有的完整 Pipeline：
    ///      1) L2：LoadRolePermissionsAcrossAllSystemsAsync(roleId)
    ///         → 依角色目前實際存在於哪些 SystemId（如 HIS2_ARM_V2、HIS2_MRA）逐一重載 role:perm 快取
    ///      2) PermissionImpactService：Role → 受影響的 UserIds
    ///      3) L1/L0：IArmNotificationService → SignalR 廣播 → 各 AP Server 的
    ///         ArmNotificationObserver.HandlePermissionCacheInvalidated → L0AuthCacheService.Invalidate(userId)
    /// </summary>
    [HttpPost("force-refresh-role/{roleId}")]
    public async Task<IActionResult> ForceRefreshRolePermissions(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return BadRequest("roleId 為必填");

        using var connection = new OracleConnection(_rmdbConnString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            var payload = JsonSerializer.Serialize(new RolePermissionChangedEvent
            {
                RoleIds = [roleId]
            });

            await _outbox.EnqueueAsync(
                connection,
                transaction,
                OutboxEventTypes.RolePermissionChanged,
                aggregateKey: roleId,
                payloadJson: payload,
                createUser: User.Identity?.Name);

            transaction.Commit();
            _logger.LogInformation("[Ops] 已排入純快取刷新事件（不異動 Oracle 權限資料）: RoleId={RoleId}", roleId);

            return Ok(new
            {
                Message = $"角色 {roleId} 的 L2/L1/L0 快取刷新事件已排入 Outbox 佇列，稍後將由背景 Worker 跨系統重載並廣播失效通知。"
            });
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "強制刷新角色快取失敗: RoleId={RoleId}", roleId);
            return StatusCode(500, "系統發生異常，快取刷新排程失敗");
        }
    }
}


