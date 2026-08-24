namespace AST.Core.Iam;

// Integrity-check grid (§12 docs/design-effective-period.md, C1) — D6/D8 can only be enforced at the
// app layer (MySQL has no WITHOUT OVERLAPS/temporal-FK) => a diagnostic query set to detect EARLY if an
// app bug silently corrupts data. READ-ONLY, does NOT fix data itself (minimal,
// scope B3: "Implement as a read-only service/method ... does NOT fix data itself").
public enum IntegrityViolationKind
{
    // 2 versions with isactive=1 for the same identity with overlapping periods (violates D6/§4).
    OverlappingActivePeriods,

    // Parent has a gap in continuous coverage over the child period (violates D8/§5, STRICT temporal-FK).
    ParentCoverageGap,

    // Child record points to a parent identity that does NOT exist (orphan). In theory the DB FK blocks
    // this situation, but the grid still checks it to catch cases of direct data tampering/faulty migrations.
    OrphanedChild,

    // [R3] 2 active identities on the same day sharing a natural key (username/code/function_key/(role_id,function_id)).
    DuplicateNaturalKey,
}

// `Table`/`IdentityId` anchor the violation to the exact identity record; `Detail` is a human-readable
// description (Vietnamese, for the future admin screen -- reports/screens for the requester use Vietnamese).
public sealed record IntegrityViolation(
    IntegrityViolationKind Kind,
    string Table,
    long IdentityId,
    string Detail);
