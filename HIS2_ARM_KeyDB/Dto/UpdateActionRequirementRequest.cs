using System.Text;
using System.Text.Json.Serialization;
using HIS2_ARM_KeyDB_Client.Enums;
using HIS2_ARM_KeyDB.Models.Auth.Enum;

namespace HIS2_ARM_KeyDB.Models.Dto;

public class UpdateActionRequirementRequest
    {
        public string SystemId { get; set; } = string.Empty;
        public string ControllerName { get; set; } = string.Empty;
        public string ActionName { get; set; } = string.Empty;

        private int _requiredBits = 0;

        /// <summary>
        /// 🚀 表面欄位：供前端呈現的基礎門檻要求 (如 "R" 代表至少要能讀取)
        /// </summary>
        [JsonPropertyName("requiredPermissions")]
        public string RequiredPermissions
        {
            get
            {
                if (_requiredBits == 0) return "None";
                var sb = new StringBuilder();
                if ((_requiredBits & (int)CrudpPermissions.Create) != 0) sb.Append('C');
                if ((_requiredBits & (int)CrudpPermissions.Read)   != 0) sb.Append('R');
                if ((_requiredBits & (int)CrudpPermissions.Update) != 0) sb.Append('U');
                if ((_requiredBits & (int)CrudpPermissions.Delete) != 0) sb.Append('D');
                if ((_requiredBits & (int)CrudpPermissions.Print)  != 0) sb.Append('P');
                return sb.ToString();
            }
            set
            {
                _requiredBits = 0;
                if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
                
                string upperValue = value.ToUpper();
                if (upperValue.Contains('C')) _requiredBits |= (int)CrudpPermissions.Create;
                if (upperValue.Contains('R')) _requiredBits |= (int)CrudpPermissions.Read;
                if (upperValue.Contains('U')) _requiredBits |= (int)CrudpPermissions.Update;
                if (upperValue.Contains('D')) _requiredBits |= (int)CrudpPermissions.Delete;
                if (upperValue.Contains('P')) _requiredBits |= (int)CrudpPermissions.Print;
            }
        }

        [JsonIgnore]
        public int RequiredBits
        {
            get => _requiredBits;
            set => _requiredBits = value;
        }
    }

/// <summary>
/// Controller 層級批次更新 API 門檻請求。
/// 💡 用於「這個 Controller 底下所有 Action 都要求同一個門檻」的常見場景
///    （例如整批設成至少要 R 才能呼叫），避免一個一個 Action 慢慢點。
///    只會影響 ARM_ACTION_DEF 裡「已經存在」的 Action 列，不會自動新增/發現新 Action
///    （新增 Action 仍應透過 BootstrapController.RegisterEndpoints 走自動拓撲探索）。
/// </summary>
public class UpdateControllerActionRequirementRequest
{
    public string SystemId { get; set; } = string.Empty;
    public string ControllerName { get; set; } = string.Empty;

    private int _requiredBits = 0;

    [JsonPropertyName("requiredPermissions")]
    public string RequiredPermissions
    {
        get
        {
            if (_requiredBits == 0) return "None";
            var sb = new StringBuilder();
            if ((_requiredBits & (int)CrudpPermissions.Create) != 0) sb.Append('C');
            if ((_requiredBits & (int)CrudpPermissions.Read)   != 0) sb.Append('R');
            if ((_requiredBits & (int)CrudpPermissions.Update) != 0) sb.Append('U');
            if ((_requiredBits & (int)CrudpPermissions.Delete) != 0) sb.Append('D');
            if ((_requiredBits & (int)CrudpPermissions.Print)  != 0) sb.Append('P');
            return sb.ToString();
        }
        set
        {
            _requiredBits = 0;
            if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)) return;

            string upperValue = value.ToUpper();
            if (upperValue.Contains('C')) _requiredBits |= (int)CrudpPermissions.Create;
            if (upperValue.Contains('R')) _requiredBits |= (int)CrudpPermissions.Read;
            if (upperValue.Contains('U')) _requiredBits |= (int)CrudpPermissions.Update;
            if (upperValue.Contains('D')) _requiredBits |= (int)CrudpPermissions.Delete;
            if (upperValue.Contains('P')) _requiredBits |= (int)CrudpPermissions.Print;
        }
    }

    [JsonIgnore]
    public int RequiredBits
    {
        get => _requiredBits;
        set => _requiredBits = value;
    }
}
