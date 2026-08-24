using AST.Core.Data;

namespace AST.Core.Iam.Repositories;

// Public DTO for the business/UI layer -- NOT an Entity (rule-module-boundary item 2).
public sealed record OrgUnitVersionDto(
    long Id,
    long OrgUnitId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive,
    string OrgCode,
    string OrgNameFullVn,
    string OrgNameShortVn,
    long? ParentId,
    DateTime RecordedAt,
    string RecordedBy,
    string? Reason,
    OrgUnitSupplementalDto Supplemental,
    bool Cancelled,
    // Nullable: pre-4d rows in a real DB would have NULL (today's DB is pre-release/empty, so this is
    // theoretical) -- also null on every read path except GetHistoryInScopeAsync (see OrgUnitVersionEntity).
    VersionOperationKind? OperationKind = null,
    // Phase 4d GetHistoryInScopeAsync only -- the parent identity's org_code/org_name_full_vn AS OF this row's own
    // EffectiveFrom. Resolved via a JOIN that filters BOTH isactive=1 AND closed-closed period containment
    // (effective_from <= asOf <= effective_to) together -- the same two conditions GetByIdentityAsync's
    // point-in-time resolution requires (hard invariant #2, rule-effective-period); omitting either one
    // duplicates/misses rows. Null = no parent (root) or the parent identity has no version covering that date.
    string? ParentOrgCodeAsOf = null,
    string? ParentOrgNameFullVnAsOf = null);
