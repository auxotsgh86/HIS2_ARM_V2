-- ============================================================
-- Seed: HIS2_ARM_V2 自身管理端點的 ARM_ACTION_DEF / ARM_PERMISSIONS
-- ============================================================
-- 背景：
--   ControllerDiscoveryService 刻意把 "User" / "UserMgr" / "PermMgr" 排除在
--   自動拓撲探索 (Discover) 之外，而 ARM 自身也從未對自己呼叫過
--   BootstrapController.RegisterEndpoints（那支端點是給「其他子系統」在自己
--   啟動時呼叫、註冊「自己」的 Controller/Action 用的）。
--
--   因此 UsersController / UserMgrController / AdminController 底下的所有 Action，
--   目前在 ARM_ACTION_DEF 裡完全沒有對應資料列。
--   💡 AdminController 雖然沒有被 ExcludeControllers 排除，但因為「排除清單」只影響
--      Discover() 掃描結果，實際上從來沒有人呼叫過 Discover() + RegisterEndpoints
--      來把 ARM 自己的拓撲寫回 ARM_ACTION_DEF，所以一樣是空的。
--
--   SelfArmAuthorizationMiddleware 呼叫的 AuthService.CheckPermissionAsync
--   在查無 REQUIRED_BITS 時一律回傳「拒絕」(Fail-Closed)，
--   所以在補上這份 Seed 之前，這三支 Controller 的所有端點都會被 401/403 擋下。
--
-- 使用方式：
--   1. 依實際環境調整 :SystemId（預設對齊 appsettings.json 的 Arm:DefaultSystemId = HIS2_ARM_V2）
--   2. 依實際環境調整要授權的 :RoleId（預設對齊 appsettings.json Bootstrap:DefaultAdminRoleId = SysAdmin）
--   3. 全程使用 MERGE，可重複執行不會產生重複資料
-- ============================================================

-- ─── 步驟 1：註冊 Action 定義 (ARM_ACTION_DEF) ──────────────────────────────
-- REQUIRED_BITS 對照 CrudBit：C=1, R=2, U=4, D=8, P=16

MERGE INTO HIS2USER2.ARM_ACTION_DEF t
USING (
    SELECT 'HIS2_ARM_V2' AS SYSTEM_ID, 'Users'    AS CONTROLLER, 'CreateUser'      AS ACTION_NAME,  1 AS REQUIRED_BITS, '新增使用者帳號'                 AS DESCRIPTION FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Users',                'GetUser',                          2,                 '查詢單一使用者資料'             FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Users',                'UpdateUser',                       4,                 '更新使用者基本資料/停權設定'     FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Users',                'DisableUser',                      8,                 '軟刪除(停用)使用者'             FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'AddOneUser',                       1,                 '新增單一使用者(含角色指派)'      FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'BatchAddUsers',                    1,                 '批次新增使用者'                 FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'GetUserData',                      2,                 '查詢使用者清單'                 FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'UpdOneUser',                       4,                 '更新使用者角色與啟用狀態'        FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'DeleteOneSession',                 8,                 '強制刪除單一 Session'           FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'GetUserSessions',                  2,                 '查詢使用者在線 Session 清單'     FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'UserMgr',              'GetOnlineUsers',                   2,                 '查詢所有在線使用者與其 Session 清單' FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Admin',                'UpdateRolePermission',             4,                 '更新角色權限位元 (ARM_PERMISSIONS)' FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Admin',                'UpdateActionRequirement',          4,                 '更新 API 門檻位元 (ARM_ACTION_DEF)' FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'Admin',                'ForceRefreshRolePermissions',      4,                 '強制刷新角色 L2/L1/L0 快取（不異動資料）' FROM DUAL
) s
ON (    t.SYSTEM_ID    = s.SYSTEM_ID
    AND t.CONTROLLER   = s.CONTROLLER
    AND t.ACTION_NAME  = s.ACTION_NAME)
WHEN MATCHED THEN
    UPDATE SET t.REQUIRED_BITS = s.REQUIRED_BITS,
               t.DESCRIPTION   = s.DESCRIPTION,
               t.MODIFIED_TIME = SYSDATE
WHEN NOT MATCHED THEN
    INSERT (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, DESCRIPTION, IS_ACTIVE, CREATE_USER, CREATED_TIME)
    VALUES (SYS_GUID(), s.SYSTEM_ID, s.CONTROLLER, s.ACTION_NAME, s.REQUIRED_BITS, s.DESCRIPTION, 'Y', 'MIS00040', SYSDATE);

-- ─── 步驟 2：授予預設管理角色 Controller 層級的完整權限 (ARM_PERMISSIONS) ───
-- 💡 ACTION_NAME 留 NULL 代表 Controller 通配（天花板），該角色對這兩個 Controller 底下
--    所有現在/未來新增的 Action 都自動具備完整 CRUDP (31)，符合「系統管理員」的語意。
--    若要更細緻的最小權限控管，可改成逐一 Action 授權（比照上面 ARM_ACTION_DEF 的組合）。

MERGE INTO HIS2USER2.ARM_PERMISSIONS p
USING (
    SELECT 'HIS2_ARM_V2' AS SYSTEM_ID, 'SysAdmin' AS ROLE_ID, 'Users'   AS CONTROLLER, CAST(NULL AS VARCHAR2(120)) AS ACTION_NAME FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'SysAdmin',            'UserMgr',              CAST(NULL AS VARCHAR2(120))                FROM DUAL UNION ALL
    SELECT 'HIS2_ARM_V2',               'SysAdmin',            'Admin',                CAST(NULL AS VARCHAR2(120))                FROM DUAL
) s
ON (    p.SYSTEM_ID   = s.SYSTEM_ID
    AND p.ROLE_ID     = s.ROLE_ID
    AND p.CONTROLLER  = s.CONTROLLER
    AND (   p.ACTION_NAME = s.ACTION_NAME
         OR (p.ACTION_NAME IS NULL AND s.ACTION_NAME IS NULL)))
WHEN MATCHED THEN
    UPDATE SET p.PERMS_BITS = 31, p.IS_ACTIVE = 'Y', p.MODIFIED_TIME = SYSDATE
WHEN NOT MATCHED THEN
    INSERT (PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_DC, IS_ACTIVE, CREATE_USER, CREATE_TIME)
    VALUES (SYS_GUID(), s.SYSTEM_ID, s.ROLE_ID, s.CONTROLLER, s.ACTION_NAME, 31, 'N', 'Y', 'MIS00040', SYSDATE);

COMMIT;

-- ============================================================
-- 執行完畢後的驗證方式
-- ============================================================
-- 1. 確認 Action 定義已存在：
--    SELECT * FROM HIS2USER2.ARM_ACTION_DEF
--    WHERE SYSTEM_ID = 'HIS2_ARM_V2' AND CONTROLLER IN ('Users', 'UserMgr', 'Admin');
--
-- 2. 確認 SysAdmin 角色已有 Controller 通配權限：
--    SELECT * FROM HIS2USER2.ARM_PERMISSIONS
--    WHERE SYSTEM_ID = 'HIS2_ARM_V2' AND ROLE_ID = 'SysAdmin'
--      AND CONTROLLER IN ('Users', 'UserMgr', 'Admin');
--
-- 3. 重要：新增/修改 ARM_ACTION_DEF 或 ARM_PERMISSIONS 後，
--    L2 Cache (Valkey) 不會自動感知（本次 Seed 是直接下 SQL，不經過 AdminController/Outbox），
--    需要手動呼叫以下端點重新預熱快取，否則要等現有 TTL 到期才會生效：
--      POST /api/v1/bootstrap/warmup-legacy-permissions/HIS2_ARM_V2
-- ============================================================

