using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Entities;
using Dapper;
using ErrorOr;

namespace AST.Modules.IAM.Tests.TestSupport;

// TEST-ONLY double for Seam-2 gap F3 (Cancel + P11 auto-cut). Originally written because no PRODUCTION
// entity had both `SupportsCancellation` AND a declared `ExclusivelyOwnedDependents` edge; Role has had
// BOTH since brief 058 (a cancellable lifecycle state — migrations/V002__role.sql added it as a `cancelled`
// column, V010 replaced that with `status`; `role_permission_version`
// exclusively owned by role — RoleRepository.cs), and Cancel + P11 together is now also proven end-to-end
// against the PRODUCTION `RoleRepository` (AST.Modules.IAM.Tests/Integration/AutoCutDependentTests.cs,
// `CancelRole_*` tests). This subclass reuses org_unit_version's REAL schema/columns (no new migration,
// `OrgUnitRepository` is `internal sealed` so it cannot be subclassed) and declares org_unit_version's own
// REAL `parent_id` self-edge (IamTemporalFkEdges.All — already a registered temporal-FK edge) as
// "exclusively owned" FOR THIS TEST ONLY: a deliberate test-only relaxation of org-tree semantics (a
// sub-org is NOT exclusively owned by its parent in production — OrgUnitRepository never opts into P11 for
// it), purely so the SHARED VersionedRepository engine has a second, differently-shaped (self-referencing)
// table+edge to auto-cut against without inventing schema.
internal sealed class SelfOwningOrgUnitRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates)
    : VersionedRepository<OrgUnitVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates)
{
    protected override string VersionTable => "org_unit_version";
    protected override string IdentityColumn => "org_unit_id";
    protected override bool SupportsCancellation => true;

    protected override IReadOnlyList<AutoCutDependent> ExclusivelyOwnedDependents =>
    [
        // DependentSupportsCancellation: false — deliberate, even though org_unit_version CAN record a
        // cancelled state (its `status` column, V010). This double exists to give the shared engine a
        // second, differently-shaped edge to auto-cut against, and its two Cancel tests pin the
        // shrink-only outcome. Keeping it opted OUT (a) preserves exactly what those tests were
        // written to prove, and (b) makes this the suite's only live proof that the new flag GATES
        // at all — with every edge opted in, `DependentSupportsCancellation` could be hard-coded
        // true and nothing would go red. A future PRODUCTION edge must choose consciously.
        new AutoCutDependent("org_unit_version", "parent_id", "org_unit_id", DependentSupportsCancellation: false),
    ];

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("org_code", nameof(OrgUnitVersionEntity.OrgCode)),
        ("org_name_full_vn", nameof(OrgUnitVersionEntity.OrgNameFullVn)),
        ("org_name_short_vn", nameof(OrgUnitVersionEntity.OrgNameShortVn)),
        ("parent_id", nameof(OrgUnitVersionEntity.ParentId)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(OrgUnitVersionEntity newValues) =>
        newValues.ParentId.HasValue
            ? new Dictionary<string, long> { ["parent_id"] = newValues.ParentId.Value }
            : new Dictionary<string, long>();

    public async Task<long> CreateIdentityAsync()
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");
    }

    public Task<ErrorOr<UpsertResult>> UpsertAsync(
        long orgUnitId, EffectivePeriod period, string orgCode, long? parentId, string recordedBy, string? reason) =>
        UpsertVersionAsync(
            orgUnitId, period,
            new OrgUnitVersionEntity
            {
                IdentityId = orgUnitId,
                EffectiveFrom = period.From,
                EffectiveTo = period.To,
                IsActive = true,
                OrgCode = orgCode,
                OrgNameFullVn = $"Đơn vị {orgCode}",
                OrgNameShortVn = orgCode,
                ParentId = parentId,
                RecordedBy = recordedBy,
                Reason = reason,
            },
            recordedBy, reason);

    // ICompositeWriteContext sibling — mirrors the production UpsertAsync(context, ...) wrappers other
    // repositories already have (e.g. RoleRepository), used by the F1 composite-Close/Cancel parity tests.
    public Task<ErrorOr<UpsertResult>> UpsertAsync(
        ICompositeWriteContext context, long orgUnitId, EffectivePeriod period, string orgCode, long? parentId,
        string recordedBy, string? reason) =>
        UpsertVersionAsync(
            context, orgUnitId, period,
            new OrgUnitVersionEntity
            {
                IdentityId = orgUnitId,
                EffectiveFrom = period.From,
                EffectiveTo = period.To,
                IsActive = true,
                OrgCode = orgCode,
                OrgNameFullVn = $"Đơn vị {orgCode}",
                OrgNameShortVn = orgCode,
                ParentId = parentId,
                RecordedBy = recordedBy,
                Reason = reason,
            },
            recordedBy, reason);

    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        long orgUnitId, long versionId, DateOnly operationDate, string recordedBy, string reason) =>
        CancelVersionAsync(orgUnitId, versionId, operationDate, recordedBy, reason);

    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        ICompositeWriteContext context, long orgUnitId, long versionId, DateOnly operationDate, string recordedBy,
        string reason) =>
        CancelVersionAsync(context, orgUnitId, versionId, operationDate, recordedBy, reason);

    // Seeds a dependent org_unit_version row DIRECTLY (bypassing UpsertVersionAsync's STRICT temporal-FK
    // coverage check) so a test can arrange a dependent whose period pre-dates its parent's OWN future
    // plan — a state the normal validated write path can never reach (the parent cannot cover a period
    // before it exists), but which is exactly the shape needed to exercise
    // AutoCutExclusivelyOwnedAsync's remnant-cut arithmetic on the Cancel no-predecessor branch.
    public async Task<long> SeedDependentVersionAsync(
        long dependentOrgUnitId, long parentOrgUnitId, DateOnly from, DateOnly to, string recordedBy)
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, parent_id,
                 effective_from, effective_to, isactive, recorded_by, reason)
            VALUES
                (@dependentOrgUnitId, @orgCode, @nameVn, @nameVn, @parentOrgUnitId,
                 @from, @to, 1, @recordedBy, 'seeded-for-test');
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                dependentOrgUnitId,
                orgCode = $"SD{dependentOrgUnitId}",
                nameVn = $"Đơn vị con {dependentOrgUnitId}",
                parentOrgUnitId,
                from,
                to,
                recordedBy,
            });
    }
}
