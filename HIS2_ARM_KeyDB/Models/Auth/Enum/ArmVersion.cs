namespace HIS2_ARM_KeyDB.Models.Auth.Enum;

/// <summary>
/// 系統架構版本列舉 (對應 ARM_SYSTEM.ARM_VERSION)
/// </summary>
public enum ArmVersion
{
    /// <summary>
    /// 傳統舊架構 (依賴 32 碼 Token 與傳統 Filter/Session)
    /// </summary>
    V1_Legacy = 1,

    /// <summary>
    /// 全新 ARMV2 架構 (依賴 JWT 與 HttpOnly Cookie 無狀態驗證)
    /// </summary>
    V2_Arm = 2
}