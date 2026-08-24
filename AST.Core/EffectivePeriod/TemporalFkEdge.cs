namespace AST.Core.EffectivePeriod;

// Declares a parent-child edge via METADATA (not hardcoded scattered around — §5).
public sealed record TemporalFkEdge(
    string ChildVersionTable,     // "user_version"
    string ChildParentColumn,     // "org_unit_id"     (FK -> parent identity)
    string ParentVersionTable,    // "org_unit_version"
    string ParentIdentityColumn); // "org_unit_id"      (identity column in the parent version table)
