-- ============================================================
-- HIS2_ARM_KeyDB Schema DDL  (已修正版)
-- Target schema: HIS2USER2
-- ============================================================

-- ARM_SYSTEM ---------------------------------------------------
CREATE TABLE HIS2USER2.ARM_SYSTEM (
                                      SYSTEM_ID     VARCHAR2(20)  NOT NULL,
                                      SYSTEM_NAME   VARCHAR2(100) NOT NULL,
                                      SORT_VALUE    NUMBER        NOT NULL,
                                      SYSTEM_GROUP  VARCHAR2(20),
                                      SYSTEM_HOST   VARCHAR2(200),
                                      IS_DC         CHAR(1)       DEFAULT 'N',          -- [修] 補 DEFAULT 'N'
                                      DC_USER       VARCHAR2(20)  DEFAULT NULL,
                                      DC_TIME       DATE          DEFAULT NULL,
                                      IS_ACTIVE     CHAR(1)       DEFAULT 'Y' NOT NULL, -- [修] NUMBER→CHAR，1→'Y'
                                      CREATE_USER   VARCHAR2(20)  DEFAULT NULL,
                                      CREATE_TIME   DATE          DEFAULT SYSDATE NOT NULL,
                                      MODIFIED_USER VARCHAR2(20)  DEFAULT NULL,
                                      MODIFIED_TIME DATE          DEFAULT NULL,          -- [修] 移除 DEFAULT SYSDATE / NOT NULL
                                      DESCRIPTION   VARCHAR2(500),
                                      CONSTRAINT ARM_SYSTEM_PK PRIMARY KEY (SYSTEM_ID)
);

-- ARM_ROLES ----------------------------------------------------
CREATE TABLE HIS2USER2.ARM_ROLES (
                                     ROLE_ID       VARCHAR2(30)  NOT NULL,
                                     ROLE_NAME     VARCHAR2(60)  NOT NULL,
                                     DESCRIPTION   VARCHAR2(200),
                                     IS_DC         CHAR(1)       DEFAULT 'N',          -- [新增]
                                     DC_USER       VARCHAR2(20)  DEFAULT NULL,          -- [新增]
                                     DC_TIME       DATE          DEFAULT NULL,          -- [新增]
                                     IS_ACTIVE     CHAR(1)       DEFAULT 'Y' NOT NULL, -- [修] NUMBER→CHAR，1→'Y'
                                     CREATE_USER   VARCHAR2(20)  DEFAULT NULL,
                                     CREATE_TIME   DATE          DEFAULT SYSDATE NOT NULL,
                                     MODIFIED_USER VARCHAR2(20)  DEFAULT NULL,
                                     MODIFIED_TIME DATE          DEFAULT NULL,          -- [修] 移除 DEFAULT SYSDATE / NOT NULL
                                     CONSTRAINT PK_ARM_ROLES PRIMARY KEY (ROLE_ID)
);

-- ARM_USERS ----------------------------------------------------
CREATE TABLE HIS2USER2.ARM_USERS (
                                     USER_ID         VARCHAR2(30)  NOT NULL,
                                     USERNAME        VARCHAR2(60)  NOT NULL,
                                     PASSWORD_HASH   VARCHAR2(100) NOT NULL,
                                     DEPARTMENT      VARCHAR2(20),
                                     IS_DC           CHAR(1)       DEFAULT 'N',          -- [新增]
                                     DC_USER         VARCHAR2(20)  DEFAULT NULL,          -- [新增]
                                     DC_TIME         DATE          DEFAULT NULL,          -- [新增]
                                     IS_ACTIVE       CHAR(1)       DEFAULT 'Y' NOT NULL, -- [修] NUMBER→CHAR，1→'Y'
                                     MUST_CHANGE_PWD NUMBER(1)     DEFAULT 0  NOT NULL,
                                     LAST_LOGIN_AT   DATE,
                                     LAST_LOGIN_IP   VARCHAR2(50),
                                     CREATE_USER     VARCHAR2(20)  DEFAULT NULL,
                                     CREATE_TIME     DATE          DEFAULT SYSDATE NOT NULL,
                                     MODIFIED_USER   VARCHAR2(20)  DEFAULT NULL,
                                     MODIFIED_TIME   DATE          DEFAULT NULL,          -- [修] 移除 DEFAULT SYSDATE / NOT NULL
                                     CONSTRAINT PK_ARM_USERS PRIMARY KEY (USER_ID)
);

-- ARM_USER_ROLES -----------------------------------------------
CREATE TABLE HIS2USER2.ARM_USER_ROLES (
                                          USER_ID       VARCHAR2(30) NOT NULL,
                                          ROLE_ID       VARCHAR2(30) NOT NULL,
                                          IS_DC         CHAR(1)      DEFAULT 'N',          -- [新增]
                                          DC_USER       VARCHAR2(20) DEFAULT NULL,          -- [新增]
                                          DC_TIME       DATE         DEFAULT NULL,          -- [新增]
                                          IS_ACTIVE     CHAR(1)      DEFAULT 'Y' NOT NULL, -- [新增]
                                          CREATE_USER   VARCHAR2(20) DEFAULT NULL,
                                          CREATE_TIME   DATE         DEFAULT SYSDATE NOT NULL,
                                          MODIFIED_USER VARCHAR2(20) DEFAULT NULL,          -- [新增]
                                          MODIFIED_TIME DATE         DEFAULT NULL,          -- [新增]
                                          CONSTRAINT PK_ARM_USER_ROLES PRIMARY KEY (USER_ID, ROLE_ID),
                                          CONSTRAINT FK_UR_USER FOREIGN KEY (USER_ID) REFERENCES HIS2USER2.ARM_USERS(USER_ID),
                                          CONSTRAINT FK_UR_ROLE FOREIGN KEY (ROLE_ID) REFERENCES HIS2USER2.ARM_ROLES(ROLE_ID)
);

CREATE INDEX IDX_ARM_UR_ROLE_ID
    ON HIS2USER2.ARM_USER_ROLES(ROLE_ID);

-- ARM_PERMISSIONS ----------------------------------------------
CREATE TABLE HIS2USER2.ARM_PERMISSIONS (
                                           PERMISSION_ID VARCHAR2(36)  NOT NULL,
                                           SYSTEM_ID     VARCHAR2(20)  NOT NULL,
                                           ROLE_ID       VARCHAR2(30)  NOT NULL,
                                           CONTROLLER    VARCHAR2(120) NOT NULL,
                                           ACTION_NAME   VARCHAR2(60)  NULL,
                                           PERMS_BITS    NUMBER(3)     NOT NULL,
                                           IS_DC         CHAR(1)       DEFAULT 'N',          -- [新增]
                                           DC_USER       VARCHAR2(20)  DEFAULT NULL,          -- [新增]
                                           DC_TIME       DATE          DEFAULT NULL,          -- [新增]
                                           IS_ACTIVE     CHAR(1)       DEFAULT 'Y' NOT NULL, -- [修] NUMBER→CHAR，1→'Y'
                                           CREATE_USER   VARCHAR2(20)  DEFAULT NULL,
                                           CREATE_TIME   DATE          DEFAULT SYSDATE NOT NULL,
                                           MODIFIED_USER VARCHAR2(20)  DEFAULT NULL,
                                           MODIFIED_TIME DATE          DEFAULT NULL,          -- [修] 移除 DEFAULT SYSDATE / NOT NULL
                                           CONSTRAINT PK_ARM_PERMISSIONS PRIMARY KEY (PERMISSION_ID),
                                           CONSTRAINT FK_PERM_SYSTEM FOREIGN KEY (SYSTEM_ID) REFERENCES HIS2USER2.ARM_SYSTEM(SYSTEM_ID),
                                           CONSTRAINT FK_PERM_ROLE   FOREIGN KEY (ROLE_ID)   REFERENCES HIS2USER2.ARM_ROLES(ROLE_ID),
                                           CONSTRAINT UQ_ROLE_SYS_CTRL_ACT UNIQUE (SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME)
);

CREATE INDEX IDX_PERM_SYS_ROLE_CTRL
    ON HIS2USER2.ARM_PERMISSIONS(SYSTEM_ID, ROLE_ID, CONTROLLER, IS_ACTIVE);
CREATE INDEX IDX_PERM_ROLE_ID
    ON HIS2USER2.ARM_PERMISSIONS(ROLE_ID);

-- ARM_ACTION_DEF -----------------------------------------------
CREATE TABLE HIS2USER2.ARM_ACTION_DEF (
                                          ACTION_DEF_ID VARCHAR2(36)  NOT NULL,
                                          SYSTEM_ID     VARCHAR2(20)  NOT NULL,          -- ★ 新增
                                          CONTROLLER    VARCHAR2(120) NOT NULL,
                                          ACTION_NAME   VARCHAR2(120) NOT NULL,
                                          REQUIRED_BITS NUMBER(3)     NOT NULL,
                                          DESCRIPTION   VARCHAR2(200),
                                          IS_DC         CHAR(1)       DEFAULT 'N',
                                          DC_USER       VARCHAR2(20)  DEFAULT NULL,
                                          DC_TIME       DATE          DEFAULT NULL,
                                          IS_ACTIVE     CHAR(1)       DEFAULT 'Y' NOT NULL,
                                          CREATE_USER   VARCHAR2(20)  DEFAULT NULL,
                                          CREATED_TIME  DATE          DEFAULT SYSDATE NOT NULL,
                                          MODIFIED_USER VARCHAR2(20)  DEFAULT NULL,
                                          MODIFIED_TIME DATE          DEFAULT NULL,
                                          CONSTRAINT PK_ARM_ACTION_DEF  PRIMARY KEY (ACTION_DEF_ID),
                                          CONSTRAINT FK_ACTDEF_SYSTEM   FOREIGN KEY (SYSTEM_ID)              -- ★ 新增
                                              REFERENCES HIS2USER2.ARM_SYSTEM(SYSTEM_ID),
                                          CONSTRAINT UQ_ACTION_DEF_CTRL UNIQUE (SYSTEM_ID, CONTROLLER, ACTION_NAME)  -- ★ 修改
);

CREATE INDEX IDX_ACTDEF_SYS_CTRL                                       -- ★ 新增
    ON HIS2USER2.ARM_ACTION_DEF(SYSTEM_ID, CONTROLLER, IS_ACTIVE);
------------------
--搬遷系統資料
INSERT INTO HIS2USER2.ARM_SYSTEM (
    SYSTEM_ID, SYSTEM_NAME, SORT_VALUE, SYSTEM_GROUP, SYSTEM_HOST,
    IS_DC, DC_USER, DC_TIME, IS_ACTIVE, CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
)
SELECT
    SYSTEM_ID, SYSTEM_NAME, SORT_VALUE, SYSTEM_GROUP, SYSTEM_HOST,
    IS_DC, DC_USER, DC_TIME, 'Y', CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
FROM HIS2USER2.ARMSYSTEM;

-- 新增 HIS2_ARM_V2 系統代號（注意 VARCHAR2(20) 限制，HIS2_ARM_V2 = 11 字元 OK）
INSERT INTO HIS2USER2.ARM_SYSTEM
(SYSTEM_ID, SYSTEM_NAME, SORT_VALUE, ARM_VERSION, IS_ACTIVE, CREATE_TIME)
VALUES
    ('HIS2_ARM_V2', 'ARM 權限管控中心 (V2)', 2, 2, 'Y', SYSDATE);

-------------------------
--搬遷角色資料
INSERT INTO HIS2USER2.ARM_ROLES (
    ROLE_ID, ROLE_NAME, DESCRIPTION, IS_DC, DC_USER, DC_TIME, IS_ACTIVE,
    CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
)
SELECT
    ROLE_ID, ROLE_NAME,
    -- 把舊版的 IS_ADMIN 標記寫入 DESCRIPTION 備註
    CASE WHEN IS_ADMIN = 'Y' THEN 'Admin Role' ELSE NULL END AS DESCRIPTION,
    IS_DC, DC_USER, DC_TIME, 'Y',
    CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
FROM HIS2USER2.ARMROLE;

-- 補上系統管理員角色 (SysAdmin) ，給予全系統權限
INSERT INTO HIS2USER2.ARM_ROLES (ROLE_ID, ROLE_NAME, DESCRIPTION, IS_DC, DC_USER, DC_TIME, IS_ACTIVE, CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME) VALUES('SysAdmin', '住院全系統管理員', NULL, 'N', NULL, NULL, 'Y', 'SYSCOM', TIMESTAMP '2024-03-04 11:14:57.000000', 'MIS00040', TIMESTAMP '2026-07-17 16:12:04.000000');
-- 註冊 Robot 專用的 Role
INSERT
INTO
    HIS2USER2.ARM_ROLES (ROLE_ID,
                         ROLE_NAME,
                         DESCRIPTION,
                         IS_DC,
                         DC_USER,
                         DC_TIME,
                         IS_ACTIVE,
                         CREATE_USER,
                         CREATE_TIME,
                         MODIFIED_USER,
                         MODIFIED_TIME)
VALUES('SystemRegistrar', '程式端點註冊', NULL, 'N', NULL, NULL, 'Y', 'MIS00040', TIMESTAMP '2026-07-17 11:41:52.000000', 'MIS00040', TIMESTAMP '2026-07-17 11:41:52.000000');


--搬遷使用者資料 (SYSUSER + SYSUSERAUTHOR)
--TRUNCATE TABLE HIS2USER2.ARM_USERS CASCADE;

-- 注意 ND990002 ，她會造成系統重複 USERID，要把其中一筆 DCCODE 設定為 '2' 

INSERT INTO HIS2USER2.ARM_USERS (
    USER_ID, USERNAME, PASSWORD_HASH, DEPARTMENT, IS_DC, DC_USER, DC_TIME,
    IS_ACTIVE, MUST_CHANGE_PWD, LAST_LOGIN_AT, CREATE_USER, CREATE_TIME
)
WITH sysPass AS (
    SELECT
        *
    FROM
        (
            SELECT
                p.*,
                ROW_NUMBER() OVER (PARTITION BY USERID
	ORDER BY
		rowid DESC) AS rn
            FROM
                HIS2USER2.SYSUSERAUTHOR p)
    WHERE
        rn = 1
)
SELECT
    u.USERID,
    NVL(u.CHINNAME, u.USERID) AS USERNAME, -- 確保 USERNAME 不為空
    NVL(a.PASSWORDS, 'NO_PASSWORD_SET') AS PASSWORD_HASH, -- 確保 PASSWORD 不為空
    u.DEPTNO,
    CASE WHEN u.DCCODE = '1' THEN 'N' ELSE 'Y' END AS IS_DC,
    NULL AS DC_USER,
    NULL AS DC_TIME,
    CASE WHEN (u.ACTIVEFLAG = '1' OR u.ACTIVEFLAG is null or u.ACTIVEFLAG = 'N') THEN 'Y' ELSE 'N' end AS IS_ACTIVE,
    0 AS MUST_CHANGE_PWD,
    a.LASTLOGINDT AS LAST_LOGIN_AT,
    u.CREATEOPID AS CREATE_USER,
    NVL(u.CREATEDATETIME, SYSDATE) AS CREATE_TIME
FROM HIS2USER2.SYSUSER u
         LEFT JOIN sysPass a ON u.USERID = a.USERID
WHERE 1=1
  AND u.DCCODE = '1'
;
-- 註冊程式使用者(Robot)
INSERT
INTO
    HIS2USER2.ARM_USERS (USER_ID,
                         USERNAME,
                         PASSWORD_HASH,
                         DEPARTMENT,
                         IS_DC,
                         DC_USER,
                         DC_TIME,
                         IS_ACTIVE,
                         MUST_CHANGE_PWD,
                         LAST_LOGIN_AT,
                         LAST_LOGIN_IP,
                         CREATE_USER,
                         CREATE_TIME,
                         MODIFIED_USER,
                         MODIFIED_TIME)
VALUES('HIS2_MRA', '住院病歷管理作業', '$2a$11$4hBi0J.IaGaUTWDUlh3F.uO6DwJlsv.sl2asN9nZqNBWQ3TyTNchi', '520200', 'N', NULL, NULL, 'Y', 0, TIMESTAMP '2026-07-16 09:13:40.000000', '10.160.18.211', 'MIS00040', TIMESTAMP '2026-06-23 03:38:11.000000', NULL, NULL);

-------------------------
--TRUNCATE TABLE HIS2USER2.ARM_USER_ROLES CASCADE;

--搬遷使用者與角色關聯
INSERT INTO HIS2USER2.ARM_USER_ROLES (
    USER_ID, ROLE_ID, IS_DC, DC_USER, DC_TIME, IS_ACTIVE,
    CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
)
SELECT
    USERID, ROLE_ID, IS_DC, DC_USER, DC_TIME, 'Y',
    CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
FROM HIS2USER2.ARMROLES ur
WHERE EXISTS (SELECT 1 FROM HIS2USER2.ARM_USERS u WHERE u.USER_ID = ur.USERID)
  AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_ROLES r WHERE r.ROLE_ID = ur.ROLE_ID);
------------------
-- 底下的查詢是因為 PHY2 在 ARM_SYSTEM 沒有定義
-- SELECT a.* FROM HIS2USER2.ARMFUNCTION a
-- WHERE 1=1
--   AND NOT EXISTS (SELECT 1 FROM HIS2USER2.ARM_SYSTEM s WHERE s.SYSTEM_ID = a.SYSTEM_ID);

-- 幫 Robot 註冊 Role
INSERT
INTO
    HIS2USER2.ARM_USER_ROLES (USER_ID,
                              ROLE_ID,
                              IS_DC,
                              DC_USER,
                              DC_TIME,
                              IS_ACTIVE,
                              CREATE_USER,
                              CREATE_TIME,
                              MODIFIED_USER,
                              MODIFIED_TIME)
VALUES('HIS2_MRA', 'SystemRegistrar', 'N', NULL, NULL, 'Y', 'MIS00040', TIMESTAMP '2026-07-17 08:48:25.000000', 'MIS00040', TIMESTAMP '2026-07-17 08:48:25.000000');


-- 搬遷功能定義
INSERT INTO HIS2USER2.ARM_ACTION_DEF (
    ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, DESCRIPTION,
    IS_ACTIVE, CREATE_USER, CREATED_TIME
)
SELECT
    SYS_GUID(), -- 自動產生 32 字元的 UUID
    SYSTEM_ID,
    CONTROLLER_NAME,
    NVL(ACTION_NAME, 'Index') AS ACTION_NAME, -- 避免 ACTION_NAME 為空
    15 AS REQUIRED_BITS, -- 預設給予 15 (二進位 1111，代表 CRUD 皆需)
    MIN(FUNCTION_NAME) AS DESCRIPTION, -- 舊的功能名稱當作備註
    'Y' AS IS_ACTIVE,
    MIN(CREATE_USER) AS CREATE_USER,
    MIN(CREATE_TIME) AS CREATED_TIME
FROM HIS2USER2.ARMFUNCTION
WHERE CONTROLLER_NAME IS NOT NULL
GROUP BY SYSTEM_ID, CONTROLLER_NAME, NVL(ACTION_NAME, 'Index');

-- 註冊端點相關權限設定
INSERT
INTO
    HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID,
                              SYSTEM_ID,
                              CONTROLLER,
                              ACTION_NAME,
                              REQUIRED_BITS,
                              DESCRIPTION,
                              IS_DC,
                              DC_USER,
                              DC_TIME,
                              IS_ACTIVE,
                              CREATE_USER,
                              CREATED_TIME,
                              MODIFIED_USER,
                              MODIFIED_TIME)
VALUES(SYS_GUID(), 'HIS2_ARM_V2', 'Bootstrap', 'RegisterEndpoints', 1, '註冊  Endpoints 專用', 'N', NULL, NULL, 'Y', 'MIS00040', TIMESTAMP '2026-06-10 09:32:33.000000', NULL, NULL);

INSERT
INTO
    HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID,
                              SYSTEM_ID,
                              CONTROLLER,
                              ACTION_NAME,
                              REQUIRED_BITS,
                              DESCRIPTION,
                              IS_DC,
                              DC_USER,
                              DC_TIME,
                              IS_ACTIVE,
                              CREATE_USER,
                              CREATED_TIME,
                              MODIFIED_USER,
                              MODIFIED_TIME)
VALUES(SYS_GUID(), 'HIS2_ARM_V2', 'Bootstrap', 'WarmupLegacyPermissions', 1, '更新 Endpoings 權限專用', 'N', NULL, NULL, 'Y', 'MIS00040', TIMESTAMP '2026-06-10 09:32:33.000000', NULL, NULL);

-- 註冊自家關鍵端點門檻（示例：Admin 與 Users 控制器，R=2 為門檻下限可自行調整）
INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Admin', 'UpdateRolePermission', 4, 'Y');

INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Users', 'CreateUser', 1, 'Y');

INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Users', 'GetUser', 2, 'Y');

INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Users', 'UpdateUser', 4, 'Y');

INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Users', 'DisableUser', 8, 'Y');

-- Razor Pages 門檻定義（CONTROLLER 欄位填映射後的名稱）
INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, DESCRIPTION, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Pages.Admin.Users', 'View', 2, '人員管理頁面', 'Y');

INSERT INTO HIS2USER2.ARM_ACTION_DEF (ACTION_DEF_ID, SYSTEM_ID, CONTROLLER, ACTION_NAME, REQUIRED_BITS, DESCRIPTION, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'Pages.Admin.PermMatrix', 'View', 2, '角色權限矩陣頁面', 'Y');

---------------------
--TRUNCATE table HIS2USER2.ARM_PERMISSIONS;
--
--SELECT * FROM (
--SELECT
--    ID,
--    SYSTEM_ID,
--    ROLE_ID,
--    CONTROLLER_NAME,
--    ACTION_NAME,
--    ROW_NUMBER() OVER (PARTITION BY ROLE_ID, SYSTEM_ID, CONTROLLER_NAME, ACTION_NAME
--    					   ORDER BY CASE WHEN CREATE_USER = 'THIS' THEN 1 ELSE 2 END) AS rn
--FROM HIS2USER2.ARMROLERIGHT rr
--WHERE CONTROLLER_NAME IS NOT NULL
--  -- 確保關聯的 SYSTEM 與 ROLE 存在於新表中
--  AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_SYSTEM s WHERE s.SYSTEM_ID = rr.SYSTEM_ID)
--  AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_ROLES r WHERE r.ROLE_ID = rr.ROLE_ID)
--  AND rr.IS_DC = 'N'
--  -- 權限配置混亂，一種角色多種權限配置
----  AND rr.SYSTEM_ID NOT in ('DSC', 'Statistics', 'BLD')
--)
--WHERE 1=1
--AND rn = 1
--;


--搬遷角色權限設定
INSERT INTO HIS2USER2.ARM_PERMISSIONS (
    PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS,
    IS_ACTIVE, CREATE_USER, CREATE_TIME, MODIFIED_USER, MODIFIED_TIME
)
WITH roleMeta AS (
    SELECT * FROM (
                      SELECT
                          ID,
                          SYSTEM_ID,
                          ROLE_ID,
                          CONTROLLER_NAME,
                          ACTION_NAME,
                          ROW_NUMBER() OVER (PARTITION BY ROLE_ID, SYSTEM_ID, CONTROLLER_NAME, ACTION_NAME
    					   ORDER BY CASE WHEN CREATE_USER = 'THIS' THEN 1 ELSE 2 END) AS rn
                      FROM HIS2USER2.ARMROLERIGHT rr
                      WHERE CONTROLLER_NAME IS NOT NULL
                        -- 確保關聯的 SYSTEM 與 ROLE 存在於新表中
                        AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_SYSTEM s WHERE s.SYSTEM_ID = rr.SYSTEM_ID)
                        AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_ROLES r WHERE r.ROLE_ID = rr.ROLE_ID)
                        AND rr.IS_DC = 'N'
                      -- 權限配置混亂，一種角色多種權限配置
--  AND rr.SYSTEM_ID NOT in ('DSC', 'Statistics', 'BLD')
                  )
    WHERE 1=1
      AND rn = 1
)
SELECT
    SYS_GUID() AS PERMISSION_ID,
    rr.SYSTEM_ID,
    rr.ROLE_ID,
    rr.CONTROLLER_NAME,
    rr.ACTION_NAME,
    -- 將 Y/N 轉換為 權限 Bits 相加
    -- 注意定義：INSERT=1, QUERY=2, EDIT=4, DELETE=8
    (CASE WHEN rr.QUERY_FLAG = 'Y' THEN 2 ELSE 0 END) +
    (CASE WHEN rr.INSERT_FLAG = 'Y' THEN 1 ELSE 0 END) +
    (CASE WHEN rr.EDIT_FLAG = 'Y' THEN 4 ELSE 0 END) +
    (CASE WHEN rr.DELETE_FLAG = 'Y' THEN 8 ELSE 0 END) AS PERMS_BITS,
    'Y' AS IS_ACTIVE,
    rr.CREATE_USER,
    rr.CREATE_TIME,
    rr.MODIFIED_USER,
    rr.MODIFIED_TIME
FROM HIS2USER2.ARMROLERIGHT rr INNER JOIN roleMeta rm ON rr.id = rm.id
WHERE rr.CONTROLLER_NAME IS NOT NULL
  -- 確保關聯的 SYSTEM 與 ROLE 存在於新表中
  AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_SYSTEM s WHERE s.SYSTEM_ID = rr.SYSTEM_ID)
  AND EXISTS (SELECT 1 FROM HIS2USER2.ARM_ROLES r WHERE r.ROLE_ID = rr.ROLE_ID)
  AND rr.IS_DC = 'N'
-- 權限配置混亂，一種角色多種權限配置
--  AND rr.SYSTEM_ID NOT in ('DSC', 'Statistics', 'BLD')
;

-- 幫系統註冊專用瘩 Role 配置權限
INSERT INTO HIS2USER2.ARM_PERMISSIONS
(PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE, CREATE_USER)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'SystemRegistrar', 'Bootstrap', 'RegisterEndpoints', 1, 'Y', 'MIS00040');

INSERT INTO HIS2USER2.ARM_PERMISSIONS
(PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE, CREATE_USER)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'SysAdmin', 'Bootstrap', 'WarmupLegacyPermissions', 8, 'Y', 'MIS00040');

-- 給 SysAdmin 角色開 Controller 天花板（31 = 全權限）
INSERT INTO HIS2USER2.ARM_PERMISSIONS (PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'SysAdmin', 'Admin', NULL, 31, 'Y');
INSERT INTO HIS2USER2.ARM_PERMISSIONS (PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'SysAdmin', 'Users', NULL, 31, 'Y');
INSERT INTO HIS2USER2.ARM_PERMISSIONS (PERMISSION_ID, SYSTEM_ID, ROLE_ID, CONTROLLER, ACTION_NAME, PERMS_BITS, IS_ACTIVE)
VALUES (SYS_GUID(), 'HIS2_ARM_V2', 'SysAdmin', 'Pages.Admin.Users', NULL, 31, 'Y');