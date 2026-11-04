namespace HIS2_ARM_KeyDB.Models;

/// <summary>
/// 供其他子系統 (如 MRA、INP) 查詢權限的請求模型
/// </summary>
public class VerifyRequest
{
    /// <summary>
    /// 使用者的 Access Token
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// 發起詢問的子系統 ID (例如: HIS2_MRA)
    /// </summary>
    public string SystemId { get; set; } = string.Empty;

    /// <summary>
    /// 欲存取的 Controller 名稱
    /// </summary>
    public string ControllerName { get; set; } = string.Empty;

    /// <summary>
    /// 欲存取的 Action 名稱
    /// </summary>
    public string ActionName { get; set; } = string.Empty;
}