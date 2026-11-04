namespace HIS2_ARM_KeyDB.Models;

public class ArmSystem
{
    public string SystemId { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public int SortValue { get; set; }
    public string? SystemGroup { get; set; }
    public string? SystemHost { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
}

public record SaveSystemRequest(
    string SystemId,
    string SystemName,
    int SortValue = 999,
    string? SystemGroup = null,
    string? SystemHost = null,
    bool IsActive = true,
    string? Description = null);

public record SaveRoleRequest(
    string RoleId,
    string RoleName,
    string Description = "",
    bool IsActive = true);

/// <summary>
/// GetSystems 查詢條件：支援「System 搜尋」欄位 + 分頁。
/// 💡 ARM_SYSTEM 可能有數百筆，不應一次全撈回前端，比照 RoleQueryRequest 的 Server-side search 模式。
/// </summary>
public record SystemQueryRequest(
    string? Keyword = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 20);

/// <summary>
/// 啟用/停用角色（ARM_ROLES.IS_ACTIVE）的請求 DTO。
/// 💡 對應 RolesController.UpdateRoleStatus，正確產生 RoleStatusChanged Outbox 事件。
/// </summary>
public record UpdateRoleStatusRequest(bool IsActive);

