namespace AST.Core.Iam;

// Metadata for 1 function — feeds permissions + menu + dashboard (spec ⑦).
public sealed record FunctionDescriptor(
    string FunctionKey,        // Module.Entity.Action (technical, internal)
    string BusinessCode,       // FX002 (displayed)
    string DisplayName,
    string MenuGroupCode,      // points to MenuGroupCodes
    string NavTarget,          // navigation target
    string RequiredPermission, // permission required to show the leaf
    int Order,
    string? Icon = null);
