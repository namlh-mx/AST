namespace AST.Core.Data;

// The DURABLE lifecycle state of a single version row, persisted in the `status` column of every version
// table that has one (org_unit_version, role_version, role_permission_version -- V010). Distinct from
// AST.Core.Presentation.VersionStatus, which is the READ-TIME label a screen shows and is computed from
// this value plus isactive plus the dates.
//
// Replaced the `cancelled` TINYINT(1) in V010. The reason it is one column and not two booleans: a
// boolean beside an enum that has the same value admits rows that are both and rows that are neither,
// with no rule saying which wins (spec §14.3).
//
// Persisted LOWERCASE and matched case-insensitively on read, because the column's CHECK constraint
// spells the values in lowercase and SQL written by hand against this table must read naturally.
public enum VersionLifecycleStatus
{
    Normal,
    Cancelled,
    Replaced,
}
