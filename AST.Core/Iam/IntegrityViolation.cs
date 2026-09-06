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

    // [R3] 2 active identities on the same day sharing a key column (username/code/function_key/(role_id,function_id)).
    // ⚠ RENAMED 2026-09-04 from DuplicateNaturalKey, and the check is unchanged -- only the word was wrong.
    // Four of the five keys ARE natural keys, but `org_unit_version.org_code` is NOT: the Thay the gesture
    // can give a corrected declaration a different code, so one real-world unit can span two codes across
    // two identities and a lineage must never be reconstructed by code. What this kind reports is P6 --
    // two ACTIVE identities sharing a key over an overlapping period -- which is true of all five and says
    // nothing about identity. Requester authorized the SharedKernel rename 2026-09-04; second reader Assurance Advisor.
    //
    // ⭐ THE PROJECTION SET IS SIX PRODUCERS, NOT FIVE. Corrected 2026-09-05 after Assurance Advisor withheld
    // second-reader approval (F-240-07, review round 4): the rename's measurement named only the five-key
    // array driving FindDuplicateActiveKeysAsync. FindDuplicateAdminFlagRolesAsync (N-14: at most one
    // role_version with is_admin_role = 1 active on any day) emits this SAME kind from OUTSIDE that array,
    // and its collision is a partial uniqueness predicate rather than a key column. A consumer that
    // groups solely by Kind will mix the two; the Detail string is what distinguishes them today.
    // The same correction fixed the renamed-test count: THREE test methods were renamed, not four -- the
    // fourth changed occurrence is an assertion inside the admin-flag test, whose name did not change.
    DuplicateActiveKey,
}

// `Table`/`IdentityId` anchor the violation to the exact identity record; `Detail` is a human-readable
// description (Vietnamese, for the future admin screen -- reports/screens for the requester use Vietnamese).
public sealed record IntegrityViolation(
    IntegrityViolationKind Kind,
    string Table,
    long IdentityId,
    string Detail);
