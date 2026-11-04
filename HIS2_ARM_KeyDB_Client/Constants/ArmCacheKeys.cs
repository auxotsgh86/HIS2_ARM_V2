namespace HIS2_ARM_KeyDB_Client.Constants;

public static class ArmCacheKeys
{
    // 把 正向 cache 和 負向 cache 的 key 分開，方便日後統計或清除
    public const string AuthVerifyPositive = "ArmAuth:VerifyCache:Positive";
    public const string AuthVerifyNegative = "ArmAuth:VerifyCache:Negative";
    public const string MenuCache = "ArmMenu:Cache";
}