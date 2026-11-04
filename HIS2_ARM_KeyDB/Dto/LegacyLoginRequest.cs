namespace HIS2_ARM_KeyDB.Dto;

// 供 Body 繫結用的輕量 Model
public class LegacyLoginRequest
{
    public string LegacyToken { get; set; } = string.Empty;
}