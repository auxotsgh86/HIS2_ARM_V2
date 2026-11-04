using System.Text;
using System.Text.Json.Serialization;
using HIS2_ARM_KeyDB_Client.Enums;
using HIS2_ARM_KeyDB.Models.Auth.Enum;

namespace HIS2_ARM_KeyDB.Models.Dto;

/// <summary>
/// 權限變更請求 DTO (對外展現 CRUDP 字串，對內封裝位元整數)
/// 💡 [優雅設計]：前端只需傳遞易讀的 "CRUD"、"CRP" 字串，後端利用屬性封裝自動完成位元運算，維持強固性。
/// </summary>
public class UpdatePermissionRequest
{
    public string SystemId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string ControllerName { get; set; } = string.Empty;
    public string? ActionName { get; set; }

    // 內部私有變數，用來存放真正的位元整數值
    private int _permsBits = 0;

    /// <summary>
    /// 🚀 表面欄位：供前端 UI 綁定與 JSON 序列化使用的 CRUDP 字串
    /// 範例："CRUD" -> 代表具有 Create, Read, Update, Delete 權限
    /// </summary>
    [JsonPropertyName("permissions")] // 讓前端看到的 JSON 屬性名稱是優雅的 permissions
    public string Permissions
    {
        get
        {
            // 當後端或資料庫要把整數倒給前端時，自動轉成 "CRUDP" 字串
            if (_permsBits == 0) return "None";

            var sb = new StringBuilder();
            if ((_permsBits & (int)CrudpPermissions.Create) != 0) sb.Append('C');
            if ((_permsBits & (int)CrudpPermissions.Read) != 0) sb.Append('R');
            if ((_permsBits & (int)CrudpPermissions.Update) != 0) sb.Append('U');
            if ((_permsBits & (int)CrudpPermissions.Delete) != 0) sb.Append('D');
            if ((_permsBits & (int)CrudpPermissions.Print) != 0) sb.Append('P');

            return sb.ToString();
        }
        set
        {
            // 当前端傳來字串時（例如 "CRUD" 或 "cr"），動態解析並計算出對應的十進位整數
            _permsBits = 0; // 重置

            if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 轉大寫以防禦前端大小寫不一致的問題
            string upperValue = value.ToUpper();

            if (upperValue.Contains('C')) _permsBits |= (int)CrudpPermissions.Create;
            if (upperValue.Contains('R')) _permsBits |= (int)CrudpPermissions.Read;
            if (upperValue.Contains('U')) _permsBits |= (int)CrudpPermissions.Update;
            if (upperValue.Contains('D')) _permsBits |= (int)CrudpPermissions.Delete;
            if (upperValue.Contains('P')) _permsBits |= (int)CrudpPermissions.Print;
        }
    }

    /// <summary>
    /// 🔒 骨子欄位：真正要寫入 Oracle 欄位與 KeyDB 的位元值
    /// 💡 [架構特徵]：加上 [JsonIgnore]，確保這個純數字的欄位絕對不會暴露給前端 API，達成徹底解耦。
    /// </summary>
    [JsonIgnore]
    public int NewPermsBits
    {
        get => _permsBits;
        set => _permsBits = value;
    }
}