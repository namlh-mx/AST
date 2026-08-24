namespace AST.Core.Iam;

// Display metadata for one L1 sidebar group. Spec ⑥ keeps the group CODE in the shared kernel (MenuGroupCodes) but
// not its display name/icon/order; this record supplies that so the Shell can render the L1 groups. BCL-only.
// IconKey = a Fluent SymbolRegular name (string); the exe maps it to an icon (keeps AST.Core WPF-free).
public sealed record MenuGroup(string Code, string DisplayName, string? IconKey, int Order);
