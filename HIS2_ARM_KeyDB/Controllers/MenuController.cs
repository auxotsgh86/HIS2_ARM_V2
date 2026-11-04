// using System.Security.Claims;
// using Dapper;
// using HIS2_ARM_KeyDB.Models.Auth;
// using HIS2_ARM_KeyDB.Models.Auth.Enum;
// using Microsoft.AspNetCore.Authorization;
// using Microsoft.AspNetCore.Mvc;
// using Oracle.ManagedDataAccess.Client;
//
// namespace HIS2_ARM_KeyDB.Controllers;
//
// [ApiController]
// [Route("api/system")]
// [Authorize]
// public class MenuController : ControllerBase
// {
//     private readonly string _connectionString;
//
//     public MenuController(IConfiguration config)
//     {
//         _connectionString = config.GetConnectionString("Oracle") ?? string.Empty;
//     }
//
//     [HttpGet("His2Menu")]
//     public async Task<IActionResult> GetMyMenus()
//     {
//         var roleIds = User.Claims
//             .Where(c => c.Type == ClaimTypes.Role)
//             .Select(c => c.Value)
//             .ToList();
//
//         if (!roleIds.Any())
//         {
//             return Ok(new List<object>());
//         }
//
//         // 💡 SQL 更新：加入 s.ARM_VERSION
//         string sql = $"""
//                       SELECT
//                           -- ==========================================
//                           -- 系統層級對應 (System Level)
//                           -- ==========================================
//                           s.SYSTEM_ID,
//                           s.SYSTEM_NAME,
//                           
//                           -- ==========================================
//                           -- 選單層級對應 (Menu Level)
//                           -- ==========================================
//                           m.MENU_ID,
//                           m.MENU_NAME,
//                           NULL AS MENU_ICON,       -- 💡 全場唯一必須保留 AS 的地方：為 NULL 賦予欄位名稱
//                           
//                           -- ==========================================
//                           -- 功能與路由對應 (Function & Route Level)
//                           -- ==========================================
//                           f.CONTROLLER_NAME,
//                           f.FUNCTION_NAME,
//                           f.ACTION_NAME,
//                           
//                           -- ==========================================
//                           -- 行為與結構控制對應 (UI Behavior & Hierarchy)
//                           -- ==========================================
//                           f.IS_SHOW,
//                           f.CAN_OINW,
//                           f.FUNCTION_LEVEL,
//                           f.PARENT_CODE
//                       FROM
//                           ARM_PERMISSIONS p
//                       INNER JOIN ARM_SYSTEM s ON
//                           p.SYSTEM_ID = s.SYSTEM_ID
//                       INNER JOIN ARMFUNCTION f ON
//                           p.SYSTEM_ID = f.SYSTEM_ID
//                           AND p.CONTROLLER = f.CONTROLLER_NAME
//                           AND f.SYSTEM_ID = s.SYSTEM_ID
//                       INNER JOIN ARMMENU m ON
//                           f.MENU_ID = m.MENU_ID
//                           AND m.SYSTEM_ID = s.SYSTEM_ID
//                       WHERE
//                           p.ROLE_ID IN :RoleIds                 
//                           AND s.IS_DC = 'N'                     
//                           AND m.IS_DC = 'N'                     
//                           AND f.IS_DC = 'N'                     
//                           AND p.IS_ACTIVE = 'Y'                 
//                           AND s.IS_ACTIVE = 'Y'                
//                           AND bitand(p.PERMS_BITS, 2) > 0       
//                       ORDER BY
//                           s.SYSTEM_ID,
//                           s.SORT_VALUE,                         
//                           m.SORT_VALUE,                         
//                           f.SORT_VALUE
//                       """;
//
//         using var connection = new OracleConnection(_connectionString);
//         var flatMenus = await connection.QueryAsync<MenuQueryResult>(sql, new { RoleIds = roleIds });
//
//         // 💡 GroupBy 條件加入 ArmVersion，並以此作為 IsLegacy 的判斷依據
//         var responseTree = flatMenus
//             .GroupBy(s => new { s.SystemId, s.SystemName, s.SystemHost, s.SystemGroup, s.ArmVersion })
//             .Select(sysGroup => new
//             {
//                 sysGroup.Key.SystemId,
//                 sysGroup.Key.SystemName,
//                 sysGroup.Key.SystemHost,
//                 sysGroup.Key.SystemGroup,
//                 IsLegacy = sysGroup.Key.ArmVersion == (int)ArmVersion.V1_Legacy,
//                 Menus = sysGroup
//                     .GroupBy(m => new { m.MenuId, m.MenuName, m.MenuSort })
//                     .Select(menuGroup => new
//                     {
//                         menuGroup.Key.MenuId,
//                         menuGroup.Key.MenuName,
//                         menuGroup.Key.MenuSort,
//                         Functions = menuGroup.Select(f => new
//                         {
//                             f.FunctionName,
//                             f.RoutePath,
//                             f.ControllerName,
//                             f.FunctionSort,
//
//                             // 🚀 終極優雅寫法：完全使用 Enum 與 HasFlag，告別魔法數字！
//                             Permissions = new
//                             {
//                                 CanInsert = f.PermsBits.HasFlag(CrudpPermissions.Create),
//                                 CanQuery = f.PermsBits.HasFlag(CrudpPermissions.Read),
//                                 CanEdit = f.PermsBits.HasFlag(CrudpPermissions.Update),
//                                 CanDelete = f.PermsBits.HasFlag(CrudpPermissions.Delete),
//                                 CanPrint = f.PermsBits.HasFlag(CrudpPermissions.Print)
//                             }
//                         }).OrderBy(f => f.FunctionSort).ToList()
//                     }).OrderBy(m => m.MenuSort).ToList()
//             }).ToList();
//
//         return Ok(responseTree);
//     }
// }

using System.Security.Claims;
using HIS2_ARM_KeyDB.Attributes;
using HIS2_ARM_KeyDB.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HIS2_ARM_KeyDB.Controllers;

[ApiController]
[Route("api/system")]
[Authorize]
public class MenuController(IMenuService menuService) : ControllerBase
{
    [HttpGet("His2Menu")]
    [BypassArmAuth]
    public async Task<IActionResult> GetMyMenus()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await menuService.GetUserMenuTreeAsync(userId);

        return Ok(result);
    }
}