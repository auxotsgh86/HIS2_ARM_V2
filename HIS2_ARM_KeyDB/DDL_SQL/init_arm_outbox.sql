-- ============================================================
-- HIS2_ARM_KeyDB Outbox Pattern DDL
-- Target schema: HIS2USER2
-- 用途：承載 ActionRequirementChangedEvent / RolePermissionChangedEvent / UserRoleChangedEvent
--       三種權限異動事件，確保「Oracle 業務異動」與「Outbox 事件寫入」在同一個 Transaction 內完成，
--       再由背景 Worker 非同步輪詢、重送，驅動 KeyDB(Valkey) 快取重載。
-- ============================================================

-- ARM_OUTBOX_EVENTS ----------------------------------------------
CREATE TABLE HIS2USER2.ARM_OUTBOX_EVENTS (
    EVENT_ID         VARCHAR2(36)   NOT NULL,               -- SYS_GUID()，事件唯一識別碼

    -- 💡 對應 IPermissionChangedEvent 的三種實作型別，Worker 依此反序列化成對應的 C# 事件類別
    --    'ActionRequirementChanged' | 'RolePermissionChanged' | 'UserRoleChanged'
    EVENT_TYPE       VARCHAR2(50)   NOT NULL,

    -- 💡 [輔助去重/追蹤用] 依事件類型而異：
    --    ActionRequirementChanged → "{SystemId}:{Controller}"（可能有多筆 Action 合併在同一 Payload 內）
    --    RolePermissionChanged    → "{RoleId}"
    --    UserRoleChanged          → "{UserId}"
    -- 純輔助欄位，方便維運查詢與監控儀表板依 Key 篩選，不做為唯一性約束
    AGGREGATE_KEY    VARCHAR2(200)  NULL,

    -- 💡 事件完整內容，序列化為 JSON。例如：
    --    {"Actions":[{"System":"HIS2_MRA","Controller":"Mram030","ActionName":"GetDrgSummay"}]}
    --    {"RoleIds":["MRA_ANALYST"]}
    --    {"UserIds":["A123456"]}
    PAYLOAD_JSON     CLOB           NOT NULL,

    -- 💡 狀態機：PENDING → PROCESSING → PROCESSED
    --                              └──→ FAILED（達到 MAX_RETRY 上限後終止重試，需人工介入）
    STATUS           VARCHAR2(20)   DEFAULT 'PENDING' NOT NULL,

    RETRY_COUNT      NUMBER(5)      DEFAULT 0 NOT NULL,
    MAX_RETRY        NUMBER(5)      DEFAULT 5 NOT NULL,

    -- 💡 指數退避（Exponential Backoff）排程：失敗後計算下次可重試時間，避免無限狂打
    NEXT_RETRY_TIME  DATE           DEFAULT SYSDATE NOT NULL,

    LAST_ERROR       VARCHAR2(2000) NULL,

    -- 💡 [多 Worker 併發安全] 搭配 `SELECT ... FOR UPDATE SKIP LOCKED` 使用。
    --    多個 ArmV2 instance 若同時跑 Outbox Worker，Oracle 原生的 SKIP LOCKED
    --    即可確保同一筆事件不會被兩個 Worker 同時撈到處理，不需要額外的分散式鎖。
    --    LOCKED_BY / LOCKED_TIME 僅作為輔助觀察與「Worker 掛掉導致鎖未釋放」的逾時偵測用途。
    LOCKED_BY        VARCHAR2(100)  NULL,                   -- 通常填 MachineName + ProcessId
    LOCKED_TIME      DATE           NULL,

    CREATE_USER      VARCHAR2(20)   DEFAULT NULL,
    CREATE_TIME      DATE           DEFAULT SYSDATE NOT NULL,
    PROCESSED_TIME   DATE           NULL,

    CONSTRAINT PK_ARM_OUTBOX_EVENTS PRIMARY KEY (EVENT_ID),
    CONSTRAINT CK_OUTBOX_STATUS CHECK (STATUS IN ('PENDING', 'PROCESSING', 'PROCESSED', 'FAILED'))
);

-- 💡 Worker 輪詢主索引：撈取「待處理 + 已到重試時間」的事件，依建立時間排序（先進先出）
CREATE INDEX IDX_OUTBOX_POLL
    ON HIS2USER2.ARM_OUTBOX_EVENTS (STATUS, NEXT_RETRY_TIME, CREATE_TIME);

-- 💡 輔助查詢索引：維運人員依 AGGREGATE_KEY 追蹤特定 Role/User/Controller 的事件歷程
CREATE INDEX IDX_OUTBOX_AGGREGATE_KEY
    ON HIS2USER2.ARM_OUTBOX_EVENTS (AGGREGATE_KEY);

-- ============================================================
-- Worker 輪詢 SQL 範例（多 Instance 併發安全）
-- ============================================================
-- 每個 ArmV2 instance 的 Outbox Worker 皆可安全執行以下查詢，
-- Oracle 的 SKIP LOCKED 確保同一筆事件不會被兩個 Worker 同時撈走：
--
-- SELECT EVENT_ID, EVENT_TYPE, PAYLOAD_JSON, RETRY_COUNT, MAX_RETRY
-- FROM   HIS2USER2.ARM_OUTBOX_EVENTS
-- WHERE  STATUS = 'PENDING'
--   AND  NEXT_RETRY_TIME <= SYSDATE
-- ORDER BY CREATE_TIME
-- FETCH FIRST 20 ROWS ONLY
-- FOR UPDATE SKIP LOCKED;
--
-- 取得後立即在同一個 Transaction 內更新為 PROCESSING + LOCKED_BY + LOCKED_TIME 後 COMMIT，
-- 讓鎖定範圍最小化，之後再非同步呼叫 IPermissionChangeDispatcher.DispatchAsync 處理實際的
-- KeyDB 重載邏輯；成功後更新 STATUS='PROCESSED' + PROCESSED_TIME，
-- 失敗則 RETRY_COUNT+1、計算 NEXT_RETRY_TIME（指數退避）、寫入 LAST_ERROR，
-- 若 RETRY_COUNT >= MAX_RETRY 則標記 STATUS='FAILED'，交由人工排查（可搭配告警/監控儀表板）。
--
-- ============================================================
-- 寫入端範例（與業務異動同一 Transaction）
-- ============================================================
-- 以 AdminController.UpdateActionRequirement 為例，MERGE INTO ARM_ACTION_DEF 之後，
-- 在【同一個 OracleTransaction】內接著執行：
--
-- INSERT INTO HIS2USER2.ARM_OUTBOX_EVENTS
--   (EVENT_ID, EVENT_TYPE, AGGREGATE_KEY, PAYLOAD_JSON, CREATE_USER)
-- VALUES
--   (SYS_GUID(), 'ActionRequirementChanged',
--    :SystemId || ':' || :ControllerName,
--    '{"Actions":[{"System":"' || :SystemId || '","Controller":"' || :ControllerName ||
--      '","ActionName":"' || :ActionName || '"}]}',
--    :OperatorUserId);
--
-- transaction.Commit() 之後，才算是這次異動「已可靠落地」，
-- 不再需要像現行寫法那樣，於 Commit 之後才同步直呼 ValkeyDbService（那段呼叫若失敗，
-- 目前完全沒有補償機制，這正是導入 Outbox 要解決的問題）。

