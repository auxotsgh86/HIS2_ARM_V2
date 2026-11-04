namespace HIS2_ARM_KeyDB_Client.Helpers;

public static class TokenHasher
{
    /// <summary>
    /// 將 Token 進行 SHA-256 雜湊，用於快取 Key 或日誌記錄，避免直接暴露原始 Token。
    /// </summary>
    public static string Hash(string token)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}