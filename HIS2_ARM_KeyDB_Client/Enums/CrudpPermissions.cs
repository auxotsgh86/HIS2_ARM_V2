namespace HIS2_ARM_KeyDB_Client.Enums;

[Flags]
public enum CrudpPermissions
{
    None = 0,
    Create = 1,       // Insert
    Read = 2,         // Query
    Update = 4,       // Edit
    Delete = 8,       // Delete
    Print = 16,       // Print
        
    // 💡 您甚至可以定義組合包，例如：
    // ReadWrite = Create | Read | Update (等於 7)
    // All = Create | Read | Update | Delete | Print (等於 31)
}