namespace HIS2_ARM_KeyDB.Models;

/// <summary>
/// 反射掃描結果：單一 Controller 及其所有 Action
/// </summary>
public class ControllerActionInfo
{
    public string       ControllerName { get; set; } = string.Empty;
    public string       FullTypeName   { get; set; } = string.Empty;   // 含 namespace，供 Log 用
    public string       AssemblyName   { get; set; } = string.Empty;
    public List<string> Actions        { get; set; } = [];
}

/// <summary>
/// 前端查詢所有 Controller/Action 的回應 DTO
/// </summary>
public record DiscoveryResponse(
    int                       TotalControllers,
    int                       TotalActions,
    List<ControllerActionInfo> Controllers);

/// <summary>
/// 儲存/更新單一 Permission 的請求 DTO
/// </summary>
public record SavePermissionRequest(
    string  RoleId,
    string  Controller,
    string? ActionName,   // null = Controller 天花板
    int     PermsBits,
    bool    IsActive = true);

/// <summary>
/// 批次儲存 Permission 的請求 DTO
/// </summary>
public record BatchSavePermissionsRequest(
    string                   RoleId,
    List<SavePermissionRequest> Permissions);

/// <summary>
/// 取得某 Role 完整 Permission 配置的回應 DTO（含有效位元）
/// </summary>
public class RolePermDetail
{
    public string  PermissionId  { get; set; } = string.Empty;
    public string  SystemId      { get; set; } = string.Empty;
    public string  RoleId        { get; set; } = string.Empty;
    public string  Controller    { get; set; } = string.Empty;
    public string? ActionName    { get; set; }
    public int     PermsBits     { get; set; }
    public string  PermsDisplay  { get; set; } = string.Empty;   // e.g. "C,R,U,P"
    public bool    IsActive      { get; set; }

    /// <summary>是否為 Controller 天花板</summary>
    public bool IsControllerCeiling => ActionName is null;

    /// <summary>
    /// 【資料驅動，不寫死】供前端泛用渲染：
    ///   permissions.forEach(permission => renderSwitch(permission));
    /// 而非逐一寫死 renderCreate()/renderRead()/... 。
    /// 未來新增 Approve/Export/Sign 等操作時，只需要在 CrudBit.Definitions 多加一筆，
    /// 本屬性與前端渲染邏輯都不需要修改。
    /// </summary>
    public List<PermissionBitFlag> Flags => CrudBit.ToFlags(PermsBits);
}

// ─── Action 門檻（ARM_ACTION_DEF）查詢 DTO ───────────────────────────────────

/// <summary>
/// 取得 System/Controller/Action 完整門檻設定的回應 DTO。
/// 💡 對應 UI 需求：
///    HIS2_MRA
///     └─ MraC010
///         ├─ Index   C R U D P
///         ├─ Save    C R U D P
///         └─ Delete  C R U D P
/// 供 AdminController.UpdateActionRequirement / UpdateControllerActionRequirement 編輯前先行讀取。
/// </summary>
public class ActionDefDetail
{
    public string  ActionDefId     { get; set; } = string.Empty;
    public string  SystemId        { get; set; } = string.Empty;
    public string  Controller      { get; set; } = string.Empty;
    public string  ActionName      { get; set; } = string.Empty;
    public int     RequiredBits    { get; set; }
    public string  RequiredDisplay { get; set; } = string.Empty;   // e.g. "C,R,U,D,P"
    public string? Description     { get; set; }
    public bool    IsActive        { get; set; }

    /// <summary>
    /// 【資料驅動，不寫死】供前端泛用渲染，見 RolePermDetail.Flags 說明。
    /// </summary>
    public List<PermissionBitFlag> Flags => CrudBit.ToFlags(RequiredBits);
}

