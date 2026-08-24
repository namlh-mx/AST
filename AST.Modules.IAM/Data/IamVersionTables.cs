namespace AST.Modules.IAM.Data;

// Small metadata shared internally within the data layer: the identity-column name for each IAM version table.
// [R5 2026-07-03] Do not confuse `isactive` (effective-period flag, version tables) with `is_active`
// (command flag, app_control table) -- the 5 tables below all use `isactive`.
internal static class IamVersionTables
{
    public static string IdentityColumnFor(string versionTable) => versionTable switch
    {
        "org_unit_version" => "org_unit_id",
        "role_version" => "role_id",
        "function_version" => "function_id",
        "user_version" => "user_id",
        "role_permission_version" => "role_permission_id",
        _ => throw new ArgumentOutOfRangeException(nameof(versionTable), versionTable, "Bảng version IAM không xác định."),
    };
}
