namespace AST.Core.Iam;

// Menu group codes in SharedKernel — do NOT belong to any module (spec ⑥).
public static class MenuGroupCodes
{
    public const string ConfigSecurity = "Config.Security";
    public const string ConfigParams = "Config.Params";

    // L1 business menu groups (Stitch "AST - Fluent Design System"). Display name/icon/order live in the MenuGroup
    // catalog (Shell); these codes are the stable keys a module's FunctionDescriptor points to via MenuGroupCode.
    public const string TransactionAccounting = "Accounting.Transaction";
    public const string InternalAccounting = "Accounting.Internal";
    public const string Treasury = "Treasury";
    public const string ManagementReport = "Report.Management";
    public const string Inspection = "Inspection";
    // Add here when a new group is needed; modules only reference the constants here, no cross-referencing.
}
