namespace AST.Infrastructure;

// ============================================================================================
// P11 "aggregate auto-cut" declaration (spec §15.2 D-7 + §16.1 capability 2).
//
// A Close/Edit that narrows a parent MAY auto-cut a dependent ONLY when that dependent is
// EXCLUSIVELY OWNED by the parent — i.e. it is not shared with, and not independently addressable
// by, another aggregate. A role's own permission grants qualify (the grant exists only to express
// that role's permission); a `user` under an `org_unit` does NOT (the user is its own aggregate),
// and a grant seen from its `function` side does NOT either (the grant belongs to the role).
// For everything that does not qualify, P5's BLOCK-only reverse-FK behaviour is unchanged.
//
// A repository declares its exclusively-owned dependents by overriding
// VersionedRepository<T>.ExclusivelyOwnedDependents. Declaring the edge is what turns the auto-cut
// ON for that parent — the base never auto-cuts an undeclared dependent (RED test:
// AutoCutDependentTests.CloseFunction_...StillBlockedByReverseFk).
//
// The cut runs in the SAME transaction as the parent Close and goes through soft-deactivate +
// remnant INSERT…SELECT (same shape as InsertRemnantAsync), so it always leaves a normal audit
// row carrying the parent write's `recorded_by`/`reason` — never a silent DELETE and never an
// UPDATE over business columns (hard invariant #1).
// ============================================================================================

// One "this parent exclusively owns that dependent" edge.
//   DependentVersionTable  — e.g. "role_permission_version"
//   DependentParentColumn  — the column on the dependent pointing back at this parent identity, e.g. "role_id"
//   DependentIdentityColumn— the dependent's own identity column, e.g. "role_permission_id"
//   DependentSupportsCancellation — whether that table has a `cancelled` column, i.e. whether the
//       engine may cancel a dependent that can never take effect (B1, 2026-08-15) instead of failing
//       the parent's write. Declared per edge, not inferred: the parent repository cannot see the
//       dependent repository's SupportsCancellation, and guessing from INFORMATION_SCHEMA would make
//       a correctness rule depend on a schema probe. `false` keeps the pre-B1 behaviour exactly
//       (BaseVersionRequired), which is the right answer for a dependent that has no way to record
//       that it was cancelled.
public sealed record AutoCutDependent(
    string DependentVersionTable,
    string DependentParentColumn,
    string DependentIdentityColumn,
    bool DependentSupportsCancellation);
