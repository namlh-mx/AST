using AST.Core.Data;
using AST.Core.EffectivePeriod;

namespace AST.Modules.IAM.Data.Entities;

// Direct mapping of table org_unit_version (docs/design-iam-schema.md §1.1).
// Does NOT get exposed outside the data layer — the repository maps it to OrgUnitVersionDto (AST.Core.Iam.Repositories).
// Named §2.4 supplemental columns only — org_reserve_1/2/3 stay unwired until they have a defined meaning.
internal sealed class OrgUnitVersionEntity : IVersionRow
{
    public long Id { get; init; }
    public long IdentityId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly EffectiveTo { get; init; }
    public bool IsActive { get; init; }
    public string OrgCode { get; init; } = string.Empty;
    public string OrgNameFullVn { get; init; } = string.Empty;
    public string OrgNameShortVn { get; init; } = string.Empty;
    public long? ParentId { get; init; }
    public string? BusinessNumber { get; init; }
    public string? AddrLineVn { get; init; }
    public string? AddrLineEn { get; init; }
    public string? AddrWardVn { get; init; }
    public string? AddrWardEn { get; init; }
    public string? AddrDistrictVn { get; init; }
    public string? AddrDistrictEn { get; init; }
    public string? AddrProvinceVn { get; init; }
    public string? AddrProvinceEn { get; init; }
    public byte AdminDivisionLevel { get; init; } = 2;
    public string? NameFullEn { get; init; }
    public string? NameShortEn { get; init; }
    public string? Phone { get; init; }
    public string? Fax { get; init; }
    public string? Email { get; init; }
    public bool Cancelled { get; init; }
    public VersionOperationKind? OperationKind { get; init; }
    public DateTime RecordedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
    public string? Reason { get; init; }

    // Phase 4d GetHistoryInScopeAsync only (parent-as-of JOIN, resolved for each history row's own EffectiveFrom) —
    // null on every other read path (QueryInScopeAsync/QueryApplicableAsync/LoadActiveVersionsAsync do not join it).
    public string? ParentOrgCodeAsOf { get; init; }
    public string? ParentOrgNameFullVnAsOf { get; init; }
}
