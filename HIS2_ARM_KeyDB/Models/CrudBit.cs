namespace HIS2_ARM_KeyDB.Models;

/// <summary>
/// 單一 Permission Bit 的 metadata 定義（供 UI 動態渲染用，避免前端寫死 C/R/U/D/P）。
/// 對應前端期望的資料形狀：
/// [
///   { "bit": 1,  "code": "C", "name": "新增" },
///   { "bit": 2,  "code": "R", "name": "讀取" },
///   ...
/// ]
/// </summary>
public record PermissionBitDefinition(int Bit, string Code, string Name, int SortOrder);

/// <summary>
/// 某個 PermsBits/RequiredBits 針對單一 Permission Bit 的「是否持有」結果。
/// 💡 這是 RolePermDetail.Flags / ActionDefDetail.Flags 的元素型別，
///    讓前端可以 `permissions.forEach(p => renderSwitch(p))` 泛用渲染，
///    未來新增 Approve/Export/Sign 等操作時，後端只需要在 CrudBit.Definitions 多加一筆，
///    不需要修改 UI、不需要新增 CanXxx 這種寫死的屬性。
/// </summary>
public record PermissionBitFlag(int Bit, string Code, string Name, bool Value);

/// <summary>
/// CRUD(P) 操作位元旗標 — 全系統唯一的 Permission Bit 定義來源。
///
/// 每個操作對應一個 bit，存入 Oracle ARM_PERMISSIONS.PERMS_BITS / ARM_ACTION_DEF.REQUIRED_BITS (NUMBER)
///
/// bit 0 = C = 1
/// bit 1 = R = 2
/// bit 2 = U = 4
/// bit 3 = D = 8
/// bit 4 = P = 16
///
/// 組合範例:
///   C,R,U,D,P = 31   (11111)
///   R,P       = 18   (10010)
///   R,U,P     = 22   (10110)
///   R,U       =  6   (00110)
///
/// 💡 [單一真實來源] 日後若要新增 Approve/Export/Sign 等操作，只需要：
///    1. 在這裡新增一個常數（例如 `Approve = 32`）
///    2. 在 Definitions 多加一筆對應的 PermissionBitDefinition
///    不需要修改任何 UI 元件、不需要新增 DTO 屬性 —— 前端一律透過
///    GET /api/Admin/permission-bits 或 RolePermDetail.Flags / ActionDefDetail.Flags 動態取得。
/// </summary>
public static class CrudBit
{
    public const int C   = 1;    // Create
    public const int R   = 2;    // Read
    public const int U   = 4;    // Update
    public const int D   = 8;    // Delete
    public const int P   = 16;   // Print
    public const int All = 31;   // C|R|U|D|P

    /// <summary>
    /// 💡 全系統唯一真實來源：所有 Permission Bit 的 metadata 清單，依 SortOrder 排序。
    /// 新增操作類型時只需要在這裡加一筆，ToDisplay/FromDisplay/ToFlags 皆會自動支援。
    /// </summary>
    public static readonly IReadOnlyList<PermissionBitDefinition> Definitions =
    [
        new(C, "C", "新增", 1),
        new(R, "R", "讀取", 2),
        new(U, "U", "修改", 3),
        new(D, "D", "刪除", 4),
        new(P, "P", "列印", 5),
    ];

    /// <summary>位元值是否包含指定操作</summary>
    public static bool Has(int bits, int requiredBit) => (bits & requiredBit) != 0;

    /// <summary>多角色 Union（OR 運算）</summary>
    public static int Union(IEnumerable<int> bitsList) =>
        bitsList.Aggregate(0, (acc, b) => acc | b);

    /// <summary>
    /// 位元值轉人類可讀字串，方便 Log（例如 "C,R,U,D,P"）。
    /// 💡 改為迭代 Definitions，而非逐一寫死 if 判斷式，新增操作類型不需要修改本方法。
    /// </summary>
    public static string ToDisplay(int bits)
    {
        var ops = Definitions
            .OrderBy(d => d.SortOrder)
            .Where(d => Has(bits, d.Bit))
            .Select(d => d.Code)
            .ToList();
        return ops.Count > 0 ? string.Join(",", ops) : "none";
    }

    /// <summary>人類可讀字串轉位元值（供管理 API 使用）</summary>
    public static int FromDisplay(string ops)
    {
        var codeToBit = Definitions.ToDictionary(d => d.Code, d => d.Bit, StringComparer.OrdinalIgnoreCase);

        var bits = 0;
        foreach (var op in ops.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (codeToBit.TryGetValue(op, out var bit))
                bits |= bit;
        }
        return bits;
    }

    /// <summary>
    /// 【供前端泛用渲染】將指定的位元值（PermsBits 或 RequiredBits）展開成每個
    /// Permission Bit 的 {bit, code, name, value} 清單，讓前端可以：
    ///   permissions.forEach(permission => renderSwitch(permission));
    /// 而不需要為每個操作類型各寫一個 renderXxx()。
    /// </summary>
    public static List<PermissionBitFlag> ToFlags(int bits) =>
        Definitions
            .OrderBy(d => d.SortOrder)
            .Select(d => new PermissionBitFlag(d.Bit, d.Code, d.Name, Has(bits, d.Bit)))
            .ToList();
}
