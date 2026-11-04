namespace HIS2_ARM_KeyDB.Models;

/// <summary>
/// 對應 Oracle ARM_PERMISSIONS
///
/// 兩層設計：
///   ACTION_NAME IS NULL  → Controller 層（天花板）
///   ACTION_NAME NOT NULL → Action 層（微調，BITAND 後不得超過天花板）
///
/// PERMS_BITS 欄位：
///   各操作位元 OR 組合的整數，由 CrudBit 常數定義
///   Oracle 查詢時用 BITAND(controller, action) 取交集
/// </summary>
public class Permission
{
    public string  PermissionId { get; set; } = string.Empty;
    public string  RoleId       { get; set; } = string.Empty;
    public string  Controller   { get; set; } = string.Empty;

    /// <summary>
    /// NULL  → Controller 預設層（套用整個 Controller 所有 Action）
    /// 有值  → 特定 Action 的微調層
    /// </summary>
    public string? ActionName   { get; set; }

    /// <summary>位元旗標，參考 CrudBit 常數</summary>
    public int     PermsBits    { get; set; }

    public bool    IsActive     { get; set; } = true;

    // ─── KeyDB HASH field 格式 ───────────────────────────────────
    // Controller 層：  "Record.*"
    // Action 層：      "Record.ManageRecord"
    public string HashField =>
        string.IsNullOrEmpty(ActionName)
            ? $"{Controller}.*"
            : $"{Controller}.{ActionName}";
}

/// <summary>Oracle 查詢後已算好有效位元的 DTO</summary>
public class EffectivePerm
{
    public string Controller   { get; set; } = string.Empty;
    public string? ActionName  { get; set; }

    /// <summary>BITAND(controller_bits, action_bits) 後的有效位元</summary>
    public int EffectiveBits   { get; set; }

    public string HashField =>
        string.IsNullOrEmpty(ActionName)
            ? $"{Controller}.*"
            : $"{Controller}.{ActionName}";
}
