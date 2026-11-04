using Dapper;
using HIS2_ARM_KeyDB.Models;
using Oracle.ManagedDataAccess.Client;
using HIS2_ARM_KeyDB_Client.Enums;
using HIS2_ARM_KeyDB.Models.Events;
using HIS2_ARM_KeyDB.Models.Outbox;
using HIS2_ARM_KeyDB.Services.Interfaces;
using System.Text.Json;

namespace HIS2_ARM_KeyDB.Data;

/// <summary>
/// Oracle 核心資料存取服務（Dapper）
/// Schema: HIS2USER2
/// [架構特徵]：不依賴外部注入 IDbConnection，而是在內部實現「即開即用、用完即關」的連線池管理模式，確保微服務高併發下的資源安全。
/// </summary>
public class OracleDbService
{
    private readonly string _connStr;
    private readonly ILogger<OracleDbService> _logger;
    private readonly IOutboxService _outbox;

    public OracleDbService(IConfiguration config, ILogger<OracleDbService> logger, IOutboxService outbox)
    {
        _connStr = config.GetConnectionString("Oracle")
                   ?? throw new InvalidOperationException("Oracle connection string 未設定");
        _logger = logger;
        _outbox = outbox;
    }

    /// <summary>
    /// 內部輔助方法：每次呼叫皆產生新連線物件，搭配 using 自動歸還連線池
    /// </summary>
    private OracleConnection Conn() => new(_connStr);

    // ═══════════════════════════════════════════════════════════
    // ARM_USERS (人員管理)
    // ═══════════════════════════════════════════════════════════
    public async Task<User?> GetUserByIdFromRmdbAsync(string userId)
    {
        var conn = Conn();

        // 1. 先查基本資料
        var userSql = """
                      SELECT USER_ID       AS UserId,
                             USERNAME      AS Username,
                             PASSWORD_HASH AS PasswordHash,
                             DEPARTMENT,
                             CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive,
                             CREATE_TIME   AS CreatedAt,
                             LAST_LOGIN_AT AS LastLoginAt,
                             LAST_LOGIN_IP AS LastLoginIp
                      FROM   HIS2USER2.ARM_USERS
                      WHERE  USER_ID = :UserId
                      """;

        var user = await conn.QueryFirstOrDefaultAsync<User>(userSql, new { UserId = userId });

        // 2. 如果有人，再查他的角色 (不用 QueryMultiple，直接發第二次)
        if (user != null)
        {
            var roleSql = """
                          SELECT ROLE_ID
                          FROM   HIS2USER2.ARM_USER_ROLES
                          WHERE  USER_ID = :UserId
                            AND  IS_DC = 'N'
                            AND  IS_ACTIVE = 'Y'
                          """;

            user.RoleIds = (await conn.QueryAsync<string>(roleSql, new { UserId = userId })).ToList();
        }

        return user;
    }

    /// <summary>
    /// 查詢使用者清單（分頁），支援以下條件組合：
    /// - RoleId：僅撈出「目前為該角色有效成員」的使用者（對應 Role → 目前成員 清單）
    /// - ExcludeRoleId：排除已是該角色成員的使用者（對應「新增成員」候選名單）
    /// - UserId：帳號前綴比對
    /// - Department：部門精確比對
    /// - Keyword：UserId 或 Username 模糊比對
    /// </summary>
    public async Task<(IEnumerable<User> Items, int TotalCount)> GetUsersAsync(
        string? userId, string? roleId, string? excludeRoleId, string? department, string? keyword,
        int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 500) pageSize = 20;

        var join = "";
        var conditions = new List<string> { "1=1" };

        if (!string.IsNullOrWhiteSpace(roleId))
        {
            // 💡 只算「有效」的角色指派，避免把已停用/已刪除的歷史指派算成目前成員
            join = """
                   JOIN HIS2USER2.ARM_USER_ROLES UR
                     ON U.USER_ID = UR.USER_ID
                    AND UR.ROLE_ID = :RoleId
                    AND UR.IS_ACTIVE = 'Y'
                    AND UR.IS_DC = 'N'
                   """;
        }

        if (!string.IsNullOrWhiteSpace(excludeRoleId))
        {
            conditions.Add("""
                            NOT EXISTS (
                                SELECT 1 FROM HIS2USER2.ARM_USER_ROLES EX
                                WHERE EX.USER_ID = U.USER_ID
                                  AND EX.ROLE_ID = :ExcludeRoleId
                                  AND EX.IS_ACTIVE = 'Y'
                                  AND EX.IS_DC = 'N'
                            )
                            """);
        }

        if (!string.IsNullOrWhiteSpace(userId))
            conditions.Add("U.USER_ID LIKE :UserId || '%'");

        if (!string.IsNullOrWhiteSpace(department))
            conditions.Add("U.DEPARTMENT = :Department");

        if (!string.IsNullOrWhiteSpace(keyword))
            conditions.Add("(U.USER_ID LIKE '%' || :Keyword || '%' OR U.USERNAME LIKE '%' || :Keyword || '%')");

        var where = string.Join(" AND ", conditions);
        var parameters = new
        {
            UserId = userId,
            RoleId = roleId,
            ExcludeRoleId = excludeRoleId,
            Department = department,
            Keyword = keyword,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };

        await using var conn = Conn();

        var totalCount = await conn.ExecuteScalarAsync<int>($"""
                                                              SELECT COUNT(DISTINCT U.USER_ID)
                                                              FROM   HIS2USER2.ARM_USERS U {join}
                                                              WHERE  {where}
                                                              """, parameters);

        var items = await conn.QueryAsync<User>($"""
                                                  SELECT DISTINCT U.USER_ID AS UserId, U.USERNAME AS Username,
                                                         U.DEPARTMENT,
                                                         CASE WHEN U.IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive,
                                                         U.CREATE_TIME AS CreatedAt, U.LAST_LOGIN_AT AS LastLoginAt
                                                  FROM   HIS2USER2.ARM_USERS U {join}
                                                  WHERE  {where}
                                                  ORDER BY U.USER_ID
                                                  OFFSET :Offset ROWS FETCH NEXT :PageSize ROWS ONLY
                                                  """, parameters);

        return (items, totalCount);
    }

    /// <summary>
    /// 批次將「既有」使用者加入指定角色（不建立新帳號，只異動 ARM_USER_ROLES）。
    /// 💡 MERGE：若曾經有過該指派（含已停用/軟刪除）則直接復活成 Active，避免違反唯一鍵；
    ///    不存在才新增一筆。單一 Transaction 內批次處理整批 UserIds，並以一個 Outbox 事件
    ///    打包通知，交由 UserRoleChangedEventHandler 統一處理快取失效。
    /// </summary>
    public async Task<int> AssignUsersToRoleAsync(
        string roleId, IEnumerable<string> userIds, string? operatorUserId = null)
    {
        var ids = userIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (ids.Length == 0) return 0;

        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            const string sql = """
                               MERGE INTO HIS2USER2.ARM_USER_ROLES t
                               USING (SELECT :UserId AS USER_ID, :RoleId AS ROLE_ID FROM DUAL) s
                               ON (t.USER_ID = s.USER_ID AND t.ROLE_ID = s.ROLE_ID)
                               WHEN MATCHED THEN
                                   UPDATE SET t.IS_ACTIVE = 'Y', t.IS_DC = 'N', t.MODIFIED_TIME = SYSDATE
                               WHEN NOT MATCHED THEN
                                   INSERT (USER_ID, ROLE_ID, IS_ACTIVE, IS_DC, CREATE_TIME)
                                   VALUES (s.USER_ID, s.ROLE_ID, 'Y', 'N', SYSDATE)
                               """;

            var affected = 0;
            foreach (var userId in ids)
                affected += await conn.ExecuteAsync(sql, new { UserId = userId, RoleId = roleId }, tx);

            // 💡 [Outbox] 一次性把這批 UserIds 打包成單一 UserRoleChanged 事件
            var payload = JsonSerializer.Serialize(new UserRoleChangedEvent { UserIds = ids });

            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.UserRoleChanged,
                aggregateKey: roleId,
                payloadJson: payload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return affected;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "AssignUsersToRole failed: RoleId={RoleId}", roleId);
            throw;
        }
    }

    public async Task<bool> CreateUserAsync(User user)
    {
        try
        {
            await using var conn = Conn();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.ExecuteAsync("""
                                    INSERT INTO HIS2USER2.ARM_USERS
                                      (USER_ID,USERNAME,PASSWORD_HASH,DEPARTMENT,IS_ACTIVE,CREATE_TIME)
                                    VALUES (:UserId,:Username,:PasswordHash,:Department,'Y',SYSDATE)
                                    """, user, tx);

            if (user.RoleIds != null)
            {
                foreach (var r in user.RoleIds)
                {
                    await conn.ExecuteAsync("""
                                            INSERT INTO HIS2USER2.ARM_USER_ROLES (USER_ID,ROLE_ID,IS_ACTIVE,CREATE_TIME)
                                            VALUES (:UserId,:RoleId,'Y',SYSDATE)
                                            """, new { UserId = user.UserId, RoleId = r }, tx);
                }
            }

            await tx.CommitAsync();
            return true;
        }
        catch (OracleException ex)
        {
            _logger.LogError(ex, "CreateUser failed: {UserId}", user.UserId);
            return false;
        }
    }

    public async Task<bool> BatchCreateUsersAsync(IEnumerable<User> users)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var u in users)
            {
                await conn.ExecuteAsync("""
                                        INSERT INTO HIS2USER2.ARM_USERS
                                          (USER_ID,USERNAME,PASSWORD_HASH,DEPARTMENT,IS_ACTIVE,CREATE_TIME)
                                        VALUES (:UserId,:Username,:PasswordHash,:Department,'Y',SYSDATE)
                                        """, u, tx);

                if (u.RoleIds != null)
                {
                    foreach (var r in u.RoleIds)
                    {
                        await conn.ExecuteAsync("""
                                                INSERT INTO HIS2USER2.ARM_USER_ROLES (USER_ID,ROLE_ID,IS_ACTIVE,CREATE_TIME)
                                                VALUES (:UserId,:RoleId,'Y',SYSDATE)
                                                """, new { UserId = u.UserId, RoleId = r }, tx);
                    }
                }
            }

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "BatchCreateUsers failed");
            return false;
        }
    }

    public async Task<bool> UpdateUserAsync(User user, string? operatorUserId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync("""
                                    UPDATE HIS2USER2.ARM_USERS
                                    SET USERNAME=:Username, DEPARTMENT=:Department, IS_ACTIVE=:IsActiveStr, MODIFIED_TIME=SYSDATE
                                    WHERE USER_ID=:UserId
                                    """, new
            {
                user.Username, user.Department,
                IsActiveStr = user.IsActive ? "Y" : "N", user.UserId
            }, tx);

            await conn.ExecuteAsync(
                "DELETE FROM HIS2USER2.ARM_USER_ROLES WHERE USER_ID=:UserId",
                new { user.UserId }, tx);

            if (user.RoleIds != null)
            {
                foreach (var r in user.RoleIds)
                {
                    await conn.ExecuteAsync("""
                                            INSERT INTO HIS2USER2.ARM_USER_ROLES (USER_ID,ROLE_ID,IS_ACTIVE,CREATE_TIME)
                                            VALUES (:UserId,:RoleId,'Y',SYSDATE)
                                            """, new { UserId = user.UserId, RoleId = r }, tx);
                }
            }

            // 💡 [Outbox] 這支方法同時異動「角色集合」與「啟用狀態」，
            //    依文件 29.4 節「如果一次操作同時改變角色與 User Status，可以產生兩個 Event」，
            //    不比對異動前的舊值，一律送出兩種事件；Handler 皆為冪等操作，重複觸發無害。
            var roleChangedPayload = JsonSerializer.Serialize(new UserRoleChangedEvent
            {
                UserIds = [user.UserId]
            });
            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.UserRoleChanged,
                aggregateKey: user.UserId,
                payloadJson: roleChangedPayload,
                createUser: operatorUserId);

            var statusChangedPayload = JsonSerializer.Serialize(new UserStatusChangedEvent
            {
                UserIds = [user.UserId]
            });
            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.UserStatusChanged,
                aggregateKey: user.UserId,
                payloadJson: statusChangedPayload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "UpdateUser failed: {UserId}", user.UserId);
            return false;
        }
    }

    public async Task<bool> DeactivateUserAsync(string userId, string? operatorUserId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var affected = await conn.ExecuteAsync(
                "UPDATE HIS2USER2.ARM_USERS SET IS_ACTIVE='N', MODIFIED_TIME=SYSDATE WHERE USER_ID=:UserId",
                new { UserId = userId }, tx);

            if (affected == 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            // 💡 [Outbox] 與 UPDATE 落在同一個 Transaction，交由 UserStatusChangedEventHandler
            //    負責撤銷 Session / 清除快取 / SignalR 通知（見文件第 9 節）。
            var payload = JsonSerializer.Serialize(new UserStatusChangedEvent
            {
                UserIds = [userId]
            });

            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.UserStatusChanged,
                aggregateKey: userId,
                payloadJson: payload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "DeactivateUser failed: {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// 💡 [輕量版 Update] 只更新使用者基本資料與啟用狀態，【不】異動 ARM_USER_ROLES。
    /// 對應 UsersController 的 PUT 端點——該端點的 UpdateUserDto 刻意不包含 RoleIds
    /// (Mass Assignment 防禦)，因此不能沿用會整批 DELETE+INSERT 角色綁定的 UpdateUserAsync，
    /// 否則會把使用者現有角色清空。角色異動應改走 UserMgr 的角色指派功能，各自產生 UserRoleChanged 事件。
    /// </summary>
    public async Task<bool> UpdateUserProfileAsync(
        string userId, string username, string department, bool isActive, string? operatorUserId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            const string sql = """
                               UPDATE HIS2USER2.ARM_USERS
                               SET USERNAME=:Username, DEPARTMENT=:Department, IS_ACTIVE=:IsActiveStr, MODIFIED_TIME=SYSDATE
                               WHERE USER_ID=:UserId
                               """;

            var affected = await conn.ExecuteAsync(sql, new
            {
                Username = username,
                Department = department,
                IsActiveStr = isActive ? "Y" : "N",
                UserId = userId
            }, tx);

            if (affected == 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            // 💡 [Outbox] IS_ACTIVE 由這裡統一異動，一律送出 UserStatusChanged 事件，
            //    交由 Handler 決定實際副作用（Handler 內的 RevokeAllSessionsByUserAsync 是冪等操作，
            //    即使是「重新啟用」也呼叫一次同樣安全，不需要額外比對異動前的舊狀態）。
            var payload = JsonSerializer.Serialize(new UserStatusChangedEvent
            {
                UserIds = [userId]
            });

            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.UserStatusChanged,
                aggregateKey: userId,
                payloadJson: payload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "UpdateUserProfile failed: {UserId}", userId);
            return false;
        }
    }

    public async Task UpdateLastLoginAsync(string userId, string ip)
    {
        await using var conn = Conn();
        await conn.ExecuteAsync("""
                                UPDATE HIS2USER2.ARM_USERS
                                SET LAST_LOGIN_AT=SYSDATE, LAST_LOGIN_IP=:Ip
                                WHERE USER_ID=:UserId
                                """, new { Ip = ip, UserId = userId });
    }

    // ═══════════════════════════════════════════════════════════
    // ARM_USER_ROLES (角色關聯)
    // ═══════════════════════════════════════════════════════════

    public async Task<IEnumerable<string>> GetRoleIdsByUserAsync(string userId)
    {
        await using var conn = Conn();
        return await conn.QueryAsync<string>(
            """
            SELECT
            	ROLE_ID
            FROM
            	HIS2USER2.ARM_USER_ROLES
            WHERE 1=1
            	AND USER_ID =:UserId
            	AND IS_DC = 'N'
            	AND IS_ACTIVE = 'Y'
            """,
            new { UserId = userId });
    }

    // ═══════════════════════════════════════════════════════════
    // ARM_ROLES (角色主檔查詢)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 查詢角色清單（分頁），供前端「角色權限」頁左側搜尋欄使用。
    /// 💡 Keyword 同時比對 ROLE_ID（代碼）與 ROLE_NAME（名稱），方便使用者用任一種方式搜尋。
    /// 預設只回傳 IS_DC='N' 且 IS_ACTIVE='Y' 的有效角色；includeInactive=true 時納入停用角色
    /// （供維運盤查用，UI 可用不同樣式標示）。
    /// </summary>
    public async Task<(IEnumerable<Role> Items, int TotalCount)> GetRolesAsync(
        string? keyword, bool includeInactive, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 500) pageSize = 20;

        var conditions = new List<string> { "IS_DC = 'N'" };
        if (!includeInactive)
            conditions.Add("IS_ACTIVE = 'Y'");

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add("""
                            (ROLE_ID LIKE '%' || :Keyword || '%' OR ROLE_NAME LIKE '%' || :Keyword || '%')
                            """);
        }

        var where = string.Join(" AND ", conditions);
        var parameters = new
        {
            Keyword = keyword,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };

        await using var conn = Conn();

        var totalCount = await conn.ExecuteScalarAsync<int>($"""
                                                              SELECT COUNT(*)
                                                              FROM   HIS2USER2.ARM_ROLES
                                                              WHERE  {where}
                                                              """, parameters);

        var items = await conn.QueryAsync<Role>($"""
                                                  SELECT ROLE_ID      AS RoleId,
                                                         ROLE_NAME    AS RoleName,
                                                         DESCRIPTION,
                                                         CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive
                                                  FROM   HIS2USER2.ARM_ROLES
                                                  WHERE  {where}
                                                  ORDER BY ROLE_ID
                                                  OFFSET :Offset ROWS FETCH NEXT :PageSize ROWS ONLY
                                                  """, parameters);

        return (items, totalCount);
    }

    /// <summary>
    /// 依 RoleId 查詢單一角色主檔資料。
    /// </summary>
    public async Task<Role?> GetRoleByIdAsync(string roleId)
    {
        await using var conn = Conn();
        return await conn.QueryFirstOrDefaultAsync<Role>("""
            SELECT ROLE_ID      AS RoleId,
                   ROLE_NAME    AS RoleName,
                   DESCRIPTION,
                   CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive
            FROM   HIS2USER2.ARM_ROLES
            WHERE  ROLE_ID = :RoleId AND IS_DC = 'N'
            """, new { RoleId = roleId });
    }

    /// <summary>
    /// 【補齊缺口】啟用/停用角色（ARM_ROLES.IS_ACTIVE）。
    /// 💡 [稽核發現] 在此方法補上之前，RoleStatusChangedEvent / RoleStatusChangedEventHandler /
    ///    PermissionImpactService 的對應 Overload 皆已完整實作，卻完全沒有任何生產端呼叫，
    ///    是全專案唯一「消費端全通、生產端缺席」的 Outbox 事件型別。現在補上這支方法，
    ///    讓整條鏈路真正被打通。
    /// 💡 依設計文件：RoleStatusChanged 不強制重載 role:perm（交給 TTL 自然到期），
    ///    只需要讓該角色所屬 Users 的 Authorization Cache 立即失效，因此 Handler 內部
    ///    不呼叫 ICacheSyncService，只透過 PermissionImpactService 找出受影響 User 並 SignalR 廣播。
    /// </summary>
    public async Task<bool> UpdateRoleStatusAsync(string roleId, bool isActive, string? operatorUserId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var affected = await conn.ExecuteAsync("""
                UPDATE HIS2USER2.ARM_ROLES
                SET IS_ACTIVE = :IsActiveStr, MODIFIED_TIME = SYSDATE, MODIFIED_USER = :OperatorUserId
                WHERE ROLE_ID = :RoleId AND IS_DC = 'N'
                """, new
            {
                IsActiveStr = isActive ? "Y" : "N",
                OperatorUserId = operatorUserId,
                RoleId = roleId
            }, tx);

            if (affected == 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            // 💡 [Outbox] 與 UPDATE 落在同一個 Transaction，交由 RoleStatusChangedEventHandler
            //    負責計算受影響使用者並廣播 SignalR 快取失效通知。
            var payload = JsonSerializer.Serialize(new RoleStatusChangedEvent
            {
                RoleIds = [roleId]
            });

            await _outbox.EnqueueAsync(
                conn, tx,
                OutboxEventTypes.RoleStatusChanged,
                aggregateKey: roleId,
                payloadJson: payload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "UpdateRoleStatus failed: RoleId={RoleId}", roleId);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ARM_PERMISSIONS (權限管理)
    // ═══════════════════════════════════════════════════════════

    public async Task<List<RolePermDetail>> GetPermissionsFullAsync(string systemId, string roleId)
    {
        await using var conn = Conn();
        var result = await conn.QueryAsync<RolePermDetail>("""
                                                           SELECT PERMISSION_ID AS PermissionId,
                                                                  SYSTEM_ID     AS SystemId,
                                                                  ROLE_ID       AS RoleId,
                                                                  CONTROLLER,
                                                                  ACTION_NAME   AS ActionName,
                                                                  PERMS_BITS    AS PermsBits,
                                                                  CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive
                                                           FROM   HIS2USER2.ARM_PERMISSIONS
                                                           WHERE  SYSTEM_ID = :SystemId AND ROLE_ID = :RoleId
                                                           ORDER BY CONTROLLER, ACTION_NAME NULLS FIRST
                                                           """, new { SystemId = systemId, RoleId = roleId });

        var list = result.ToList();
        foreach (var p in list)
            p.PermsDisplay = BitsToDisplay(p.PermsBits);
        return list;
    }

    // 💡 [已移除] UpsertPermissionAsync(單筆、無 Outbox 呼叫)：
    //    這支方法會直接 MERGE ARM_PERMISSIONS 卻完全不經過 Outbox，一旦被呼叫將導致
    //    Oracle 異動後 Redis/role:perm 快取與受影響使用者的 Session 授權快取永遠不會失效
    //    （稽核報告「端點與 Outbox 連動性檢查」第 2 點發現的地雷）。
    //    唯一的呼叫端 Controllers/PermMgrController_.cs_ 本身已停用（副檔名 `.cs_`，不參與編譯），
    //    因此直接移除本方法以杜絕未來重新啟用該 Controller 時誤用此方法繞過 Outbox 的風險。
    //    單筆 Upsert 需求請改用已正確串接 Outbox 的 AdminController.UpdateRolePermission
    //    （內部 MERGE 邏輯等價，見 update-role-permission 端點），
    //    批次需求則使用 BatchUpsertPermissionsAsync。

//     /// <summary>
//     /// 取得指定 Role 所屬的所有有效 User。
//     /// </summary>
//     public async Task<IEnumerable<string>> GetUserIdsByRolesAsync(
//         IEnumerable<string> roleIds)
//     {
//         var ids = roleIds
//             .Where(x => !string.IsNullOrWhiteSpace(x))
//             .Distinct()
//             .ToArray();
//
//         if (ids.Length == 0)
//         {
//             return [];
//         }
//
//         await using var conn = Conn();
//
//         return await conn.QueryAsync<string>(
//             """
//             SELECT DISTINCT USER_ID
//             FROM HIS2USER2.ARM_USER_ROLES
//             WHERE ROLE_ID IN :RoleIds
//               AND IS_DC = 'N'
//               AND IS_ACTIVE = 'Y'
//             """,
//             new
//             {
//                 RoleIds = ids
//             });
//     }
//
//     /// <summary>
//     /// 取得指定 Action Resource 所影響的所有 User。
//     ///
//     /// 權限來源包含：
//     /// 1. Action 層級權限：SYSTEM + CONTROLLER + ACTION_NAME
//     /// 2. Controller 層級權限：SYSTEM + CONTROLLER + ACTION_NAME IS NULL
//     ///
//     /// Action → Role → User
//     /// </summary>
//     public async Task<IEnumerable<string>> GetUserIdsByActionsAsync(
//         IEnumerable<ActionResourceChanged> actions)
//     {
//         var items = actions
//             .Where(x =>
//                 !string.IsNullOrWhiteSpace(x.System) &&
//                 !string.IsNullOrWhiteSpace(x.Controller) &&
//                 !string.IsNullOrWhiteSpace(x.ActionName))
//             .DistinctBy(x => new
//             {
//                 x.System,
//                 x.Controller,
//                 x.ActionName
//             })
//             .ToArray();
//
//         if (items.Length == 0)
//         {
//             return [];
//         }
//
//         await using var conn = Conn();
//
//         var sqlParts = new List<string>();
//         var parameters = new DynamicParameters();
//
//         for (var i = 0; i < items.Length; i++)
//         {
//             var action = items[i];
//
//             var systemParam = $"System{i}";
//             var controllerParam = $"Controller{i}";
//             var actionParam = $"Action{i}";
//
//             parameters.Add(systemParam, action.System);
//             parameters.Add(controllerParam, action.Controller);
//             parameters.Add(actionParam, action.ActionName);
//
//             sqlParts.Add($"""
//                           SELECT ur.USER_ID
//                           -- 什麼 role 有哪些 users
//                           FROM HIS2USER2.ARM_USER_ROLES ur
//                           -- action 需求權限的改變會影響那些 roles
//                           INNER JOIN HIS2USER2.ARM_PERMISSIONS p
//                               ON p.ROLE_ID = ur.ROLE_ID
//                           WHERE ur.IS_DC = 'N'
//                             AND ur.IS_ACTIVE = 'Y'
//                             AND p.IS_DC = 'N'
//                             AND p.IS_ACTIVE = 'Y'
//                             AND p.SYSTEM_ID = :{systemParam}
//                             AND p.CONTROLLER = :{controllerParam}
//                             AND (
//                                   p.ACTION_NAME = :{actionParam}
//                                   OR p.ACTION_NAME IS NULL
//                                 )
//                           """);
//         }
//
//         var sql = $"""
//                    SELECT DISTINCT USER_ID
//                    FROM (
//                        {string.Join("\nUNION ALL\n", sqlParts)}
//                    )
//                    """;
//
//         return await conn.QueryAsync<string>(
//             sql,
//             parameters);
//     }

    /// <summary>
/// 取得指定 Role 所屬的所有有效 User。
/// </summary>
public async Task<IEnumerable<string>> GetUserIdsByRolesAsync(
    IEnumerable<string> roleIds,
    CancellationToken cancellationToken = default)
{
    var ids = roleIds
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct()
        .ToArray();

    if (ids.Length == 0)
    {
        return [];
    }

    await using var conn = Conn();

    return await conn.QueryAsync<string>(
        new CommandDefinition(
            """
            SELECT DISTINCT USER_ID
            FROM HIS2USER2.ARM_USER_ROLES
            WHERE ROLE_ID IN :RoleIds
              AND IS_DC = 'N'
              AND IS_ACTIVE = 'Y'
            """,
            new
            {
                RoleIds = ids
            },
            cancellationToken: cancellationToken));
}

/// <summary>
/// 取得指定 Action Resource 所影響的所有 User。
///
/// 權限來源包含：
/// 1. Action 層級權限：SYSTEM + CONTROLLER + ACTION_NAME
/// 2. Controller 層級權限：SYSTEM + CONTROLLER + ACTION_NAME IS NULL
///
/// Action → Role → User
/// </summary>
public async Task<IEnumerable<string>> GetUserIdsByActionsAsync(
    IEnumerable<ActionResourceChanged> actions,
    CancellationToken cancellationToken = default)
{
    var items = actions
        .Where(x =>
            !string.IsNullOrWhiteSpace(x.System) &&
            !string.IsNullOrWhiteSpace(x.Controller) &&
            !string.IsNullOrWhiteSpace(x.ActionName))
        .DistinctBy(x => new
        {
            x.System,
            x.Controller,
            x.ActionName
        })
        .ToArray();

    if (items.Length == 0)
    {
        return [];
    }

    await using var conn = Conn();

    var sqlParts = new List<string>();
    var parameters = new DynamicParameters();

    for (var i = 0; i < items.Length; i++)
    {
        var action = items[i];

        var systemParam = $"System{i}";
        var controllerParam = $"Controller{i}";
        var actionParam = $"Action{i}";

        parameters.Add(
            systemParam,
            action.System);

        parameters.Add(
            controllerParam,
            action.Controller);

        parameters.Add(
            actionParam,
            action.ActionName);

        sqlParts.Add($"""
            SELECT ur.USER_ID
            FROM HIS2USER2.ARM_USER_ROLES ur
            INNER JOIN HIS2USER2.ARM_PERMISSIONS p
                ON p.ROLE_ID = ur.ROLE_ID
            WHERE ur.IS_DC = 'N'
              AND ur.IS_ACTIVE = 'Y'
              AND p.IS_DC = 'N'
              AND p.IS_ACTIVE = 'Y'
              AND p.SYSTEM_ID = :{systemParam}
              AND p.CONTROLLER = :{controllerParam}
              AND (
                    p.ACTION_NAME = :{actionParam}
                    OR p.ACTION_NAME IS NULL
                  )
            """);
    }

    var sql = $"""
        SELECT DISTINCT USER_ID
        FROM (
            {string.Join("\nUNION ALL\n", sqlParts)}
        )
        """;

    return await conn.QueryAsync<string>(
        new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken));
}

    public async Task<bool> BatchUpsertPermissionsAsync(
        string systemId, string roleId, IEnumerable<SavePermissionRequest> perms, string? operatorUserId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            const string sql = """
                               MERGE INTO HIS2USER2.ARM_PERMISSIONS p
                               USING (SELECT :SystemId   AS SYSTEM_ID,
                                             :RoleId     AS ROLE_ID,
                                             :Controller AS CONTROLLER,
                                             :ActionName AS ACTION_NAME
                                      FROM DUAL) src
                               ON (    p.SYSTEM_ID  = src.SYSTEM_ID
                                   AND p.ROLE_ID    = src.ROLE_ID
                                   AND p.CONTROLLER = src.CONTROLLER
                                   AND (   p.ACTION_NAME = src.ACTION_NAME
                                        OR (p.ACTION_NAME IS NULL AND src.ACTION_NAME IS NULL)))
                               WHEN MATCHED THEN
                                   UPDATE SET p.PERMS_BITS=:PermsBits, p.IS_ACTIVE=:IsActive, p.MODIFIED_TIME=SYSDATE
                               WHEN NOT MATCHED THEN
                                   INSERT (PERMISSION_ID,SYSTEM_ID,ROLE_ID,CONTROLLER,ACTION_NAME,PERMS_BITS,IS_ACTIVE,CREATE_TIME)
                                   VALUES (SYS_GUID(),:SystemId,:RoleId,:Controller,:ActionName,:PermsBits,:IsActive,SYSDATE)
                               """;
            foreach (var p in perms)
            {
                await conn.ExecuteAsync(sql, new
                {
                    SystemId = systemId,
                    RoleId = roleId,
                    p.Controller,
                    p.ActionName,
                    p.PermsBits,
                    IsActive = p.IsActive ? "Y" : "N"
                }, tx);
            }

            // 💡 [Outbox] 與批次 MERGE 落在同一個 Transaction，確保 Business Mutation 與事件不可分割。
            //    此批次操作僅影響單一 RoleId，AggregateKey 直接使用 RoleId。
            var payload = JsonSerializer.Serialize(new RolePermissionChangedEvent
            {
                RoleIds = [roleId]
            });

            await _outbox.EnqueueAsync(
                conn,
                tx,
                OutboxEventTypes.RolePermissionChanged,
                aggregateKey: roleId,
                payloadJson: payload,
                createUser: operatorUserId);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "BatchUpsertPermissions failed: sys={Sys}, role={Role}", systemId, roleId);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ARM_ACTION_DEF (API 拓撲註冊)
    // ═══════════════════════════════════════════════════════════

    public async Task<int> RegisterEndpointsAsync(string systemId, List<ControllerActionInfo> endpoints)
    {
        // 💡 定義為明確的常數，消滅 Magic Number
        const int defaultRequiredBits = (int)CrudpPermissions.Read;

        await using var conn = Conn();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // 💡 核心優化：將原本的雙層巢狀迴圈，利用 LINQ 攤平成一個單一維度的匿名參數集合
            // 這樣可以將資料處理邏輯與資料庫存取邏輯乾淨地切開 (Separation of Concerns)
            var parameters = endpoints
                .Where(ep => ep.Actions?.Count > 0)
                .SelectMany(ep => ep.Actions, (ep, action) => new
                {
                    SystemId = systemId,
                    Controller = ep.ControllerName,
                    ActionName = action,
                    RequiredBits = defaultRequiredBits
                })
                .ToList();

            // 防呆：如果沒有任何 Endpoint 需要註冊，直接提早返回 (Early Return)
            if (!parameters.Any())
            {
                return 0;
            }

            const string sql = """
                               MERGE INTO HIS2USER2.ARM_ACTION_DEF t
                               USING (
                                   SELECT
                                       :SystemId   AS SYSTEM_ID,
                                       :Controller AS CONTROLLER,
                                       :ActionName AS ACTION_NAME
                                   FROM DUAL
                               ) s
                               ON (
                                      t.SYSTEM_ID    = s.SYSTEM_ID
                                  AND t.CONTROLLER  = s.CONTROLLER
                                  AND t.ACTION_NAME = s.ACTION_NAME
                               )
                               WHEN NOT MATCHED THEN
                                   INSERT (
                                       ACTION_DEF_ID,
                                       SYSTEM_ID,
                                       CONTROLLER,
                                       ACTION_NAME,
                                       REQUIRED_BITS,
                                       IS_ACTIVE,
                                       CREATED_TIME
                                   )
                                   VALUES (
                                       SYS_GUID(),
                                       s.SYSTEM_ID,
                                       s.CONTROLLER,
                                       s.ACTION_NAME,
                                       :RequiredBits,
                                       'Y',
                                       SYSDATE
                                   )
                               """;

            // 💡 Dapper 支援直接傳入 IEnumerable<T> 作為參數。
            // Dapper 底層會自動將迴圈轉化為優化過的 Command 執行，大幅減少程式碼複雜度，
            // 同時 ExecuteAsync 也會直接回傳總共異動的資料列數 (newAddedCount)
            int newAddedCount = await conn.ExecuteAsync(sql, parameters, tx);

            await tx.CommitAsync();
            return newAddedCount;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "API 註冊寫入 Oracle 失敗。SystemId={Sys}", systemId);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ARM_ACTION_DEF / ARM_SYSTEM 查詢（供「Action 門檻」管理 UI 使用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 【補齊缺口】查詢 Action 門檻清單（System/Controller/Action × REQUIRED_BITS），
    /// 供前端「Action 門檻」頁在呼叫 AdminController.UpdateActionRequirement /
    /// UpdateControllerActionRequirement 編輯前，先行讀取目前設定。
    /// 💡 systemId / controller 皆為選填：
    ///    - 只帶 systemId → 回傳該系統底下所有 Controller 的 Action（可在前端依 Controller 分組，
    ///      對應 UI 需求的 System → Controller → Action 階層）
    ///    - 同時帶 systemId + controller → 只回傳該 Controller 底下的 Action（頁簽切換時用）
    /// </summary>
    public async Task<List<ActionDefDetail>> GetActionDefDetailsAsync(
        string? systemId, string? controller, bool includeInactive = false)
    {
        var conditions = new List<string> { "1=1" };
        if (!includeInactive)
            conditions.Add("IS_ACTIVE = 'Y'");
        if (!string.IsNullOrWhiteSpace(systemId))
            conditions.Add("SYSTEM_ID = :SystemId");
        if (!string.IsNullOrWhiteSpace(controller))
            conditions.Add("CONTROLLER = :Controller");

        var where = string.Join(" AND ", conditions);

        await using var conn = Conn();
        var result = await conn.QueryAsync<ActionDefDetail>($"""
            SELECT ACTION_DEF_ID AS ActionDefId,
                   SYSTEM_ID     AS SystemId,
                   CONTROLLER,
                   ACTION_NAME   AS ActionName,
                   REQUIRED_BITS AS RequiredBits,
                   DESCRIPTION,
                   CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive
            FROM   HIS2USER2.ARM_ACTION_DEF
            WHERE  {where}
            ORDER BY SYSTEM_ID, CONTROLLER, ACTION_NAME
            """, new { SystemId = systemId, Controller = controller });

        var list = result.ToList();
        foreach (var item in list)
            item.RequiredDisplay = BitsToDisplay(item.RequiredBits);

        return list;
    }

    /// <summary>
    /// 【補齊缺口 + 強化】查詢系統主檔（ARM_SYSTEM）清單，供前端「Action 門檻」頁最上層的
    /// System 下拉選單使用（HIS2_MRA / HIS2_ARM_V2 / HIS2_INP ... 階層的第一層）。
    /// 💡 [Server-side search]：ARM_SYSTEM 實際可能有數百筆，不能像早期版本一次全撈回前端，
    ///    因此改為分頁 + Keyword（比對 SystemId 或 SystemName）設計，比照 GetRolesAsync 的模式，
    ///    搭配前端「[ 請輸入系統名稱 / ID 搜尋... ]」的即時查詢輸入框使用。
    /// </summary>
    public async Task<(IEnumerable<ArmSystem> Items, int TotalCount)> GetSystemsAsync(
        string? keyword, bool includeInactive, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 500) pageSize = 20;

        var conditions = new List<string> { "IS_DC = 'N'" };
        if (!includeInactive)
            conditions.Add("IS_ACTIVE = 'Y'");

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add("""
                            (SYSTEM_ID LIKE '%' || :Keyword || '%' OR SYSTEM_NAME LIKE '%' || :Keyword || '%')
                            """);
        }

        var where = string.Join(" AND ", conditions);
        var parameters = new
        {
            Keyword = keyword,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };

        await using var conn = Conn();

        var totalCount = await conn.ExecuteScalarAsync<int>($"""
                                                              SELECT COUNT(*)
                                                              FROM   HIS2USER2.ARM_SYSTEM
                                                              WHERE  {where}
                                                              """, parameters);

        var items = await conn.QueryAsync<ArmSystem>($"""
                                                       SELECT SYSTEM_ID     AS SystemId,
                                                              SYSTEM_NAME   AS SystemName,
                                                              SORT_VALUE    AS SortValue,
                                                              SYSTEM_GROUP  AS SystemGroup,
                                                              SYSTEM_HOST   AS SystemHost,
                                                              CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END AS IsActive,
                                                              DESCRIPTION
                                                       FROM   HIS2USER2.ARM_SYSTEM
                                                       WHERE  {where}
                                                       ORDER BY SORT_VALUE, SYSTEM_ID
                                                       OFFSET :Offset ROWS FETCH NEXT :PageSize ROWS ONLY
                                                       """, parameters);

        return (items, totalCount);
    }

    // ═══════════════════════════════════════════════════════════
    // 供快取 (CacheSyncService) 專用的輕量級資料查詢方法
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 取得指定系統的 API 門檻設定 (供同步 KeyDB 使用)
    /// </summary>
    public async Task<IEnumerable<(string Controller, string ActionName, int RequiredBits)>> GetActionDefsForCacheAsync(
        string systemId)
    {
        await using var conn = Conn();
        return await conn.QueryAsync<(string Controller, string ActionName, int RequiredBits)>(@"
            SELECT CONTROLLER as Controller, ACTION_NAME as ActionName, REQUIRED_BITS as RequiredBits 
            FROM HIS2USER2.ARM_ACTION_DEF 
            WHERE SYSTEM_ID = :SystemId AND IS_ACTIVE = 'Y'",
            new { SystemId = systemId });
    }

    /// <summary>
    /// 💡 [Controller 級重載專用] 取得指定系統、指定 Controller 底下的 API 門檻設定。
    /// 搭配 sys:required:{systemId}:{controller} 的 Key 級快取結構使用，
    /// 讓 Controller 重載時不需要撈整個系統的資料。
    /// </summary>
    public async Task<IEnumerable<(string Controller, string ActionName, int RequiredBits)>> GetActionDefsForCacheAsync(
        string systemId, string controller)
    {
        await using var conn = Conn();
        return await conn.QueryAsync<(string Controller, string ActionName, int RequiredBits)>(@"
            SELECT CONTROLLER as Controller, ACTION_NAME as ActionName, REQUIRED_BITS as RequiredBits 
            FROM HIS2USER2.ARM_ACTION_DEF 
            WHERE SYSTEM_ID = :SystemId AND CONTROLLER = :Controller AND IS_ACTIVE = 'Y'",
            new { SystemId = systemId, Controller = controller });
    }

    /// <summary>
    /// 💡 [跨系統 Role 重載專用] 取得指定角色目前有效權限設定分佈在哪些子系統下。
    /// 由於 ARM_ROLES.ROLE_ID 為全域唯一（未綁定 SystemId），單一角色可能同時服務多個子系統，
    /// 因此角色權限異動時需查出所有受影響的 SystemId，逐一重載對應的 role:perm:{systemId}:{roleId} 快取。
    /// </summary>
    public async Task<IEnumerable<string>> GetDistinctSystemIdsByRoleAsync(string roleId)
    {
        await using var conn = Conn();
        return await conn.QueryAsync<string>(@"
            SELECT DISTINCT SYSTEM_ID
            FROM HIS2USER2.ARM_PERMISSIONS
            WHERE ROLE_ID = :RoleId AND IS_ACTIVE = 'Y'",
            new { RoleId = roleId });
    }

    /// <summary>
    /// 取得指定系統、指定角色的權限設定 (供同步 KeyDB 使用)
    /// </summary>
    public async Task<IEnumerable<(string Controller, string ActionName, int PermsBits)>> GetRolePermsForCacheAsync(
        string systemId, string roleId)
    {
        await using var conn = Conn();
        return await conn.QueryAsync<(string Controller, string ActionName, int PermsBits)>(@"
            SELECT CONTROLLER as Controller, ACTION_NAME as ActionName, PERMS_BITS as PermsBits 
            FROM HIS2USER2.ARM_PERMISSIONS 
            WHERE SYSTEM_ID = :SystemId AND ROLE_ID = :RoleId AND IS_ACTIVE = 'Y'",
            new { SystemId = systemId, RoleId = roleId });
    }

    /// <summary>
    /// 💡 取得指定系統下所有有設定權限的獨立角色 ID (供全系統快取預熱使用)
    /// </summary>
    public async Task<IEnumerable<string>> GetDistinctRolesBySystemAsync(string systemId)
    {
        await using var conn = Conn();

        // 使用 DISTINCT 確保不會撈出重複的 ROLE_ID
        // 僅針對 IS_ACTIVE = 'Y' 的有效歷史權限進行預熱
        return await conn.QueryAsync<string>(@"
                SELECT DISTINCT ROLE_ID 
                FROM HIS2USER2.ARM_PERMISSIONS 
                WHERE SYSTEM_ID = :SystemId AND IS_ACTIVE = 'Y'",
            new { SystemId = systemId });
    }

    /// <summary>
    /// 檢查角色清單是否具備指定 API/畫面的存取權限
    /// 💡 [架構特徵]：完美支援「Controller 層級泛用授權 (ACTION_NAME IS NULL)」與「Action 層級精細授權」。
    /// 💡 [效能與資安]：利用 Oracle 的 BITAND 進行位元比對，並透過 Dapper 陣列展開防禦 SQL Injection。
    /// </summary>
    public async Task<bool> HasPermissionAsync(
        List<string> roleIds,
        string systemId,
        string controllerName,
        string actionName)
    {
        // 1. 防禦性設計：若使用者未具備任何角色，直接阻擋，節省 DB 查詢開銷
        if (roleIds == null || !roleIds.Any()) return false;

        try
        {
            // =====================================================================
            // 💡 SQL 語意解析：
            // 1. 從 ARM_ACTION_DEF 取得該 Action 所需的 REQUIRED_BITS。
            // 2. 從 ARM_PERMISSIONS 取得使用者角色對應的權限 (PERMS_BITS)。
            // 3. 【關鍵彈性】：透過 (p.ACTION_NAME IS NULL OR p.ACTION_NAME = a.ACTION_NAME)，
            //    讓角色可以直接繼承 Controller 層級的基礎權限，也能向下精細配置單一 Action。
            // 4. 【位元驗證】：只要找到「任何一個」角色的權限滿足 Action 的要求，
            //    即 BITAND(p.PERMS_BITS, a.REQUIRED_BITS) = a.REQUIRED_BITS，就判定為有權限。
            // =====================================================================
            string sql = @"
                SELECT 1
                FROM HIS2USER2.ARM_PERMISSIONS p
                INNER JOIN HIS2USER2.ARM_ACTION_DEF a 
                    ON p.SYSTEM_ID = a.SYSTEM_ID 
                   AND p.CONTROLLER = a.CONTROLLER
                   -- 💡 支援 Controller 層級的通用權限與 Action 特化權限
                   AND (p.ACTION_NAME IS NULL OR p.ACTION_NAME = a.ACTION_NAME)
                WHERE a.SYSTEM_ID = :SystemId
                  AND a.CONTROLLER = :ControllerName
                  AND a.ACTION_NAME = :ActionName
                  AND a.IS_ACTIVE = 'Y'
                  AND p.ROLE_ID IN :RoleIds
                  AND p.IS_ACTIVE = 'Y'
                  -- 核心位元驗證：使用 Oracle 的 BITAND 函數
                  AND BITAND(p.PERMS_BITS, a.REQUIRED_BITS) = a.REQUIRED_BITS
                FETCH FIRST 1 ROWS ONLY";

            // 如果您的 Oracle 版本較舊不支援 FETCH FIRST，請改用以下寫法：
            // string sql = @"
            //     SELECT 1 FROM (
            //         SELECT 1
            //         FROM HIS2USER2.ARM_PERMISSIONS p
            //         INNER JOIN HIS2USER2.ARM_ACTION_DEF a 
            //             ON p.SYSTEM_ID = a.SYSTEM_ID 
            //            AND p.CONTROLLER = a.CONTROLLER
            //            AND (p.ACTION_NAME IS NULL OR p.ACTION_NAME = a.ACTION_NAME)
            //         WHERE a.SYSTEM_ID = :SystemId
            //           AND a.CONTROLLER = :ControllerName
            //           AND a.ACTION_NAME = :ActionName
            //           AND a.IS_ACTIVE = 'Y'
            //           AND p.ROLE_ID IN :RoleIds
            //           AND p.IS_ACTIVE = 'Y'
            //           AND BITAND(p.PERMS_BITS, a.REQUIRED_BITS) = a.REQUIRED_BITS
            //     ) WHERE ROWNUM <= 1";

            // 呼叫原本建立連線的方法
            using var connection = Conn();

            // 💡 Dapper 的魔法：自動將 List<string> 展開成安全的 SQL 陣列參數
            var result = await connection.QueryFirstOrDefaultAsync<int?>(sql, new
            {
                SystemId = systemId,
                ControllerName = controllerName,
                ActionName = actionName,
                RoleIds = roleIds
            });

            // 若回傳有值 (1)，代表通過權限驗證
            return result.HasValue;
        }
        catch (Exception ex)
        {
            // 將錯誤攔截並寫入 Log，避免底層 SQL 例外直接拋給前端
            // 發生資料庫異常時，基於資安原則 (Fail-Closed)，預設阻擋放行
            _logger.LogError(ex, "查詢 Oracle 權限矩陣時發生異常。System: {SystemId}, Target: {Controller}/{Action}",
                systemId, controllerName, actionName);

            return false;
        }
    }

    // ─── Helper ─────────────────────────────────────────────────
    /// <summary>
    /// 💡 [單一真實來源] 改為委派給 CrudBit.ToDisplay，不再使用獨立的 CrudpPermissions enum
    ///    重複定義同一套位元規則。新增 Approve/Export/Sign 等操作時，
    ///    只需要維護 Models/CrudBit.cs 的 Definitions，這裡不需要任何修改。
    /// </summary>
    private static string BitsToDisplay(int bits) => CrudBit.ToDisplay(bits);

    public async Task<string?> GetUserIdFromLegacyTokenAsync(string legacyToken)
    {
        using var connection = new OracleConnection(_connStr);

        // 💡 確保該憑證尚未過期 (LOGOUT_TIME > sysdate)
        string sql = """
                     SELECT USER_ID 
                     FROM HIS2USER2.HIS2_LOGIN_TOKEN 
                     WHERE LOGIN_TOKEN = :LegacyToken 
                       AND LOGOUT_TIME > :Now 
                     FETCH FIRST 1 ROWS ONLY
                     """;

        var realUserId = await connection.QueryFirstOrDefaultAsync<string>(
            sql, new { LegacyToken = legacyToken, Now = DateTime.UtcNow }
        );

        return realUserId;
    }
}