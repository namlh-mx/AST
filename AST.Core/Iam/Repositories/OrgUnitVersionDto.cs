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
    VersionLifecycleStatus Status,
    // Nullable: pre-4d rows in a real DB would have NULL (today's DB is pre-release/empty, so this is
    // theoretical) -- also null on every read path except GetHistoryInScopeAsync (see OrgUnitVersionEntity).
    VersionOperationKind? OperationKind = null,
    // Parent labels are populated by GetByIdentityAsync at its requested as-of date and by GetHistoryInScopeAsync
    // at each row's EffectiveFrom. Both paths require an active parent version whose closed period contains that date.
    // Null means the row is a root, the parent has no covering active version, or overlapping active parent versions
    // make the parent resolution ambiguous.
    string? ParentOrgCodeAsOf = null,
    string? ParentOrgNameFullVnAsOf = null,
    string? ParentOrgNameShortVnAsOf = null);
