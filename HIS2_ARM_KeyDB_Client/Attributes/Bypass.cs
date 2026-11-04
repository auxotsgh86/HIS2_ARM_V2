namespace HIS2_ARM_KeyDB_Client.Attributes
{
    // ═══════════════════════════════════════════════════════════
    // 免死金牌屬性 (Bypass Attribute)
    // 💡 [架構特徵：宣告式路由繞過] 
    // 允許特定 Controller 或 Action (如登入、健康檢查) 透過掛載此 Attribute，
    // 在不修改核心邏輯的情況下，安全且優雅地繞過全域攔截驗證。
    // ═══════════════════════════════════════════════════════════
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class BypassArmAuthAttribute : Attribute { }
}
