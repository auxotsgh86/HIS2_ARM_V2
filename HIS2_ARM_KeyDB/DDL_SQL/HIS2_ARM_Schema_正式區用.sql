-- ============================================================
-- HIS2 ARM - Authorization Schema
-- Schema: HIS2USER2
--
-- 共六張核心表：
--   1. ARM_USERS         使用者主檔
--   2. ARM_ROLES         角色主檔
--   3. ARM_USER_ROLES    使用者角色關聯
--   4. ARM_SYSTEM        系統主檔
--   5. ARM_ACTION_DEF    Action 權限定義
--   6. ARM_PERMISSIONS   角色權限
--
-- 資料關係：
--
--   ARM_USERS
--       │
--       └── ARM_USER_ROLES ── ARM_ROLES
--                                  │
--                                  └── ARM_PERMISSIONS
--                                           │
--                                           └── ARM_SYSTEM
--
--   ARM_SYSTEM ── ARM_ACTION_DEF
--
-- ============================================================


-- ============================================================
-- 1. ARM_USERS
-- 使用者主檔
-- ============================================================

CREATE TABLE HIS2USER2.ARM_USERS
(
    -- 使用者唯一識別碼
    USER_ID             VARCHAR2(30)  NOT NULL,

    -- 使用者名稱
    USERNAME            VARCHAR2(60)  NOT NULL,

    -- 密碼雜湊值
    PASSWORD_HASH       VARCHAR2(100) NOT NULL,

    -- 所屬部門代碼
    DEPARTMENT          VARCHAR2(20),

    -- 是否為資料中心（Data Center）異動資料
    IS_DC               CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER             VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME             DATE,

    -- 是否啟用
    IS_ACTIVE           CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 是否必須變更密碼
    -- 0 = 否，1 = 是
    MUST_CHANGE_PWD     NUMBER(1, 0)  DEFAULT 0 NOT NULL,

    -- 最後登入時間
    LAST_LOGIN_AT       DATE,

    -- 最後登入 IP
    LAST_LOGIN_IP       VARCHAR2(50),

    -- 建立人員
    CREATE_USER         VARCHAR2(20),

    -- 建立時間
    CREATE_TIME         DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER       VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME       DATE,

    CONSTRAINT PK_ARM_USERS
        PRIMARY KEY (USER_ID)
        USING INDEX
        TABLESPACE USERTBS2025
);

COMMENT ON TABLE HIS2USER2.ARM_USERS IS
    'ARM 使用者主檔';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.USER_ID IS
    '使用者唯一識別碼';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.USERNAME IS
    '使用者名稱';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.PASSWORD_HASH IS
    '密碼雜湊值';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.DEPARTMENT IS
    '所屬部門代碼';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.IS_DC IS
    '是否為資料中心異動資料';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.IS_ACTIVE IS
    '是否啟用';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.MUST_CHANGE_PWD IS
    '是否必須變更密碼，0=否、1=是';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.LAST_LOGIN_AT IS
    '最後登入時間';

COMMENT ON COLUMN HIS2USER2.ARM_USERS.LAST_LOGIN_IP IS
    '最後登入 IP';


-- ============================================================
-- 2. ARM_ROLES
-- 角色主檔
-- ============================================================

CREATE TABLE HIS2USER2.ARM_ROLES
(
    -- 角色唯一識別碼
    ROLE_ID             VARCHAR2(30)  NOT NULL,

    -- 角色名稱
    ROLE_NAME           VARCHAR2(60)  NOT NULL,

    -- 角色說明
    DESCRIPTION         VARCHAR2(200),

    -- 是否為資料中心（Data Center）異動資料
    IS_DC               CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER             VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME             DATE,

    -- 是否啟用
    IS_ACTIVE           CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 建立人員
    CREATE_USER         VARCHAR2(20),

    -- 建立時間
    CREATE_TIME         DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER       VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME       DATE,

    CONSTRAINT PK_ARM_ROLES
        PRIMARY KEY (ROLE_ID)
        USING INDEX
        TABLESPACE USERTBS2025
);

COMMENT ON TABLE HIS2USER2.ARM_ROLES IS
    'ARM 角色主檔';

COMMENT ON COLUMN HIS2USER2.ARM_ROLES.ROLE_ID IS
    '角色唯一識別碼';

COMMENT ON COLUMN HIS2USER2.ARM_ROLES.ROLE_NAME IS
    '角色名稱';

COMMENT ON COLUMN HIS2USER2.ARM_ROLES.DESCRIPTION IS
    '角色說明';

COMMENT ON COLUMN HIS2USER2.ARM_ROLES.IS_ACTIVE IS
    '是否啟用';


-- ============================================================
-- 3. ARM_USER_ROLES
-- 使用者角色關聯表
--
-- 使用者與角色為多對多關係。
-- ============================================================

CREATE TABLE HIS2USER2.ARM_USER_ROLES
(
    -- 使用者 ID
    USER_ID             VARCHAR2(30)  NOT NULL,

    -- 角色 ID
    ROLE_ID             VARCHAR2(30)  NOT NULL,

    -- 是否為資料中心（Data Center）異動資料
    IS_DC               CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER             VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME             DATE,

    -- 是否啟用此使用者角色關聯
    IS_ACTIVE           CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 建立人員
    CREATE_USER         VARCHAR2(20),

    -- 建立時間
    CREATE_TIME         DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER       VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME       DATE,

    CONSTRAINT PK_ARM_USER_ROLES
        PRIMARY KEY (USER_ID, ROLE_ID)
        USING INDEX
        TABLESPACE USERTBS2025
);

-- 使用者 FK
ALTER TABLE HIS2USER2.ARM_USER_ROLES
    ADD CONSTRAINT FK_UR_USER
    FOREIGN KEY (USER_ID)
    REFERENCES HIS2USER2.ARM_USERS (USER_ID);

-- 角色 FK
ALTER TABLE HIS2USER2.ARM_USER_ROLES
    ADD CONSTRAINT FK_UR_ROLE
    FOREIGN KEY (ROLE_ID)
    REFERENCES HIS2USER2.ARM_ROLES (ROLE_ID);

-- Role → User 反向查詢用
--
-- 用於 Role 權限異動時，快速找出所有具有該 Role 的 User。
-- 此 Index 對 Cache Invalidation 流程相當重要。
CREATE INDEX HIS2USER2.IDX_ARM_UR_ROLE_ID
    ON HIS2USER2.ARM_USER_ROLES (ROLE_ID)
    TABLESPACE USERTBS2025;

COMMENT ON TABLE HIS2USER2.ARM_USER_ROLES IS
    'ARM 使用者與角色關聯表';

COMMENT ON COLUMN HIS2USER2.ARM_USER_ROLES.USER_ID IS
    '使用者 ID';

COMMENT ON COLUMN HIS2USER2.ARM_USER_ROLES.ROLE_ID IS
    '角色 ID';

COMMENT ON COLUMN HIS2USER2.ARM_USER_ROLES.IS_ACTIVE IS
    '此使用者角色關聯是否啟用';


-- ============================================================
-- 4. ARM_SYSTEM
-- 系統主檔
-- ============================================================

CREATE TABLE HIS2USER2.ARM_SYSTEM
(
    -- 系統唯一識別碼
    SYSTEM_ID           VARCHAR2(20)  NOT NULL,

    -- 系統名稱
    SYSTEM_NAME         VARCHAR2(100) NOT NULL,

    -- 系統排序值
    SORT_VALUE          NUMBER        NOT NULL,

    -- 系統群組
    SYSTEM_GROUP        VARCHAR2(20),

    -- 系統 Host / URL
    SYSTEM_HOST         VARCHAR2(200),

    -- 是否為資料中心（Data Center）異動資料
    IS_DC               CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER             VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME             DATE,

    -- 是否啟用
    IS_ACTIVE           CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 建立人員
    CREATE_USER         VARCHAR2(20),

    -- 建立時間
    CREATE_TIME         DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER       VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME       DATE,

    -- 系統說明
    DESCRIPTION         VARCHAR2(500),

    -- ARM 權限版本
    ARM_VERSION         NUMBER(*, 0)  DEFAULT 1 NOT NULL,

    CONSTRAINT ARM_SYSTEM_PK
        PRIMARY KEY (SYSTEM_ID)
        USING INDEX
        TABLESPACE USERTBS2025
);

COMMENT ON TABLE HIS2USER2.ARM_SYSTEM IS
    'ARM 系統主檔';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.SYSTEM_ID IS
    '系統唯一識別碼';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.SYSTEM_NAME IS
    '系統名稱';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.SORT_VALUE IS
    '系統排序值';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.SYSTEM_GROUP IS
    '系統群組';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.SYSTEM_HOST IS
    '系統 Host 或 URL';

COMMENT ON COLUMN HIS2USER2.ARM_SYSTEM.ARM_VERSION IS
    'ARM 權限版本';


-- ============================================================
-- 5. ARM_ACTION_DEF
-- Action 權限定義
--
-- 定義系統中的 Controller / Action，
-- 以及執行該 Action 所需要的權限 Bits。
--
-- 權限需求：
--   SYSTEM + CONTROLLER + ACTION
--       ↓
--   REQUIRED_BITS
-- ============================================================

CREATE TABLE HIS2USER2.ARM_ACTION_DEF
(
    -- Action 定義唯一識別碼
    ACTION_DEF_ID      VARCHAR2(36)  NOT NULL,

    -- 所屬系統
    SYSTEM_ID          VARCHAR2(20)  NOT NULL,

    -- Controller 名稱
    CONTROLLER         VARCHAR2(120) NOT NULL,

    -- Action 名稱
    ACTION_NAME        VARCHAR2(120) NOT NULL,

    -- 執行此 Action 所需要的權限 Bits
    REQUIRED_BITS      NUMBER(3, 0)  NOT NULL,

    -- Action 說明
    DESCRIPTION        VARCHAR2(200),

    -- 是否為資料中心（Data Center）異動資料
    IS_DC              CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER            VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME            DATE,

    -- 是否啟用
    IS_ACTIVE          CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 建立人員
    CREATE_USER        VARCHAR2(20),

    -- 建立時間
    CREATED_TIME       DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER      VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME      DATE,

    CONSTRAINT PK_ARM_ACTION_DEF
        PRIMARY KEY (ACTION_DEF_ID)
        USING INDEX
        TABLESPACE USERTBS2025,

    -- 同一系統下 Controller + Action 不可重複
    CONSTRAINT UQ_ACTION_DEF_CTRL
        UNIQUE (SYSTEM_ID, CONTROLLER, ACTION_NAME)
        USING INDEX
        TABLESPACE USERTBS2025
);

-- 所屬系統 FK
ALTER TABLE HIS2USER2.ARM_ACTION_DEF
    ADD CONSTRAINT FK_ACTDEF_SYSTEM
    FOREIGN KEY (SYSTEM_ID)
    REFERENCES HIS2USER2.ARM_SYSTEM (SYSTEM_ID);

-- 系統 + Controller + Active Action 查詢用
CREATE INDEX HIS2USER2.IDX_ACTDEF_SYS_CTRL
    ON HIS2USER2.ARM_ACTION_DEF
       (SYSTEM_ID, CONTROLLER, IS_ACTIVE)
    TABLESPACE USERTBS2025;

COMMENT ON TABLE HIS2USER2.ARM_ACTION_DEF IS
    'ARM Action 權限需求定義';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.ACTION_DEF_ID IS
    'Action 定義唯一識別碼';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.SYSTEM_ID IS
    '所屬系統 ID';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.CONTROLLER IS
    'Controller 名稱';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.ACTION_NAME IS
    'Action 名稱';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.REQUIRED_BITS IS
    '執行 Action 所需的權限 Bits';

COMMENT ON COLUMN HIS2USER2.ARM_ACTION_DEF.IS_ACTIVE IS
    '是否啟用';


-- ============================================================
-- 6. ARM_PERMISSIONS
-- 角色權限表
--
-- 定義：
--   某 Role 在某 System / Controller / Action
--   實際擁有的權限。
--
-- 權限模型：
--   USER
--     ↓
--   USER_ROLE
--     ↓
--   ROLE
--     ↓
--   PERMISSION
--     ↓
--   SYSTEM + CONTROLLER + ACTION
--     ↓
--   PERMS_BITS
-- ============================================================

CREATE TABLE HIS2USER2.ARM_PERMISSIONS
(
    -- 權限定義唯一識別碼
    PERMISSION_ID      VARCHAR2(36)  NOT NULL,

    -- 所屬系統
    SYSTEM_ID          VARCHAR2(20)  NOT NULL,

    -- 所屬角色
    ROLE_ID            VARCHAR2(30)  NOT NULL,

    -- Controller 名稱
    CONTROLLER         VARCHAR2(120) NOT NULL,

    -- Action 名稱
    ACTION_NAME        VARCHAR2(60),

    -- 角色實際擁有的權限 Bits
    PERMS_BITS         NUMBER(3, 0)  NOT NULL,

    -- 是否為資料中心（Data Center）異動資料
    IS_DC              CHAR(1)       DEFAULT 'N',

    -- 資料中心異動人員
    DC_USER            VARCHAR2(20),

    -- 資料中心異動時間
    DC_TIME            DATE,

    -- 是否啟用
    IS_ACTIVE          CHAR(1)       DEFAULT 'Y' NOT NULL,

    -- 建立人員
    CREATE_USER        VARCHAR2(20),

    -- 建立時間
    CREATE_TIME        DATE          DEFAULT SYSDATE NOT NULL,

    -- 最後修改人員
    MODIFIED_USER      VARCHAR2(20),

    -- 最後修改時間
    MODIFIED_TIME      DATE,

    CONSTRAINT PK_ARM_PERMISSIONS
        PRIMARY KEY (PERMISSION_ID)
        USING INDEX
        TABLESPACE USERTBS2025,

    -- 同一角色在同一系統、Controller、Action
    -- 只能存在一筆權限定義
    CONSTRAINT UQ_ROLE_SYS_CTRL_ACT
        UNIQUE
        (
            SYSTEM_ID,
            ROLE_ID,
            CONTROLLER,
            ACTION_NAME
        )
        USING INDEX
        TABLESPACE USERTBS2025
);

-- 所屬系統 FK
ALTER TABLE HIS2USER2.ARM_PERMISSIONS
    ADD CONSTRAINT FK_PERM_SYSTEM
    FOREIGN KEY (SYSTEM_ID)
    REFERENCES HIS2USER2.ARM_SYSTEM (SYSTEM_ID);

-- 所屬角色 FK
ALTER TABLE HIS2USER2.ARM_PERMISSIONS
    ADD CONSTRAINT FK_PERM_ROLE
    FOREIGN KEY (ROLE_ID)
    REFERENCES HIS2USER2.ARM_ROLES (ROLE_ID);

-- System + Role + Controller 查詢用
CREATE INDEX HIS2USER2.IDX_PERM_SYS_ROLE_CTRL
    ON HIS2USER2.ARM_PERMISSIONS
       (
           SYSTEM_ID,
           ROLE_ID,
           CONTROLLER,
           IS_ACTIVE
       )
    TABLESPACE USERTBS2025;

-- Role → Permission 反向查詢
--
-- 用於 Role 權限異動時，
-- 快速取得該 Role 所涉及的 Permission。
CREATE INDEX HIS2USER2.IDX_PERM_ROLE_ID
    ON HIS2USER2.ARM_PERMISSIONS (ROLE_ID)
    TABLESPACE USERTBS2025;

COMMENT ON TABLE HIS2USER2.ARM_PERMISSIONS IS
    'ARM 角色權限表';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.PERMISSION_ID IS
    '權限定義唯一識別碼';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.SYSTEM_ID IS
    '所屬系統 ID';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.ROLE_ID IS
    '所屬角色 ID';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.CONTROLLER IS
    'Controller 名稱';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.ACTION_NAME IS
    'Action 名稱';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.PERMS_BITS IS
    '角色實際擁有的權限 Bits';

COMMENT ON COLUMN HIS2USER2.ARM_PERMISSIONS.IS_ACTIVE IS
    '是否啟用';


-- ============================================================
-- Schema 關係總覽
--
-- ARM_USERS
--      │
--      │ 1:N
--      ▼
-- ARM_USER_ROLES
--      │
--      │ N:1
--      ▼
-- ARM_ROLES
--      │
--      │ 1:N
--      ▼
-- ARM_PERMISSIONS ───── N:1 ───── ARM_SYSTEM
--                                      │
--                                      │ 1:N
--                                      ▼
--                                ARM_ACTION_DEF
--
-- ============================================================
