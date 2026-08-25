using System.Data;
using AST.Core.Data;
using AST.Infrastructure;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Modules.IAM.Data.Entities;
using Dapper;
using ErrorOr;

namespace AST.Modules.IAM.Data.Repositories;

internal sealed class OrgUnitRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates,
    IParentCoverageProvider parentCoverage)
    : VersionedRepository<OrgUnitVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates),
        IOrgUnitRepository
{
    protected override string VersionTable => "org_unit_version";
    protected override string IdentityColumn => "org_unit_id";

    // P4: org-unit day gaps BLOCK on upsert (base default is warn-only).
    protected override bool GapIsBlocking => true;
    protected override string GapBlockErrorCode => "OrgUnit.GapNotAllowed";

    // N6: future-plan cancellation ("Bị hủy") — SELECT includes `status` only when opted in.
    protected override bool SupportsCancellation => true;

    // Phase 4d: org-unit history grid needs to know WHICH action (Add/Edit/Close/Cancel) produced each row.
    protected override bool RecordsOperationKind => true;

    protected override string OrgUnitColumn => $"{Alias}.org_unit_id";

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("org_code", nameof(OrgUnitVersionEntity.OrgCode)),
        ("org_name_full_vn", nameof(OrgUnitVersionEntity.OrgNameFullVn)),
        ("org_name_short_vn", nameof(OrgUnitVersionEntity.OrgNameShortVn)),
        ("parent_id", nameof(OrgUnitVersionEntity.ParentId)),
        ("org_business_number", nameof(OrgUnitVersionEntity.BusinessNumber)),
        ("org_addr_line_vn", nameof(OrgUnitVersionEntity.AddrLineVn)),
        ("org_addr_line_en", nameof(OrgUnitVersionEntity.AddrLineEn)),
        ("org_addr_ward_vn", nameof(OrgUnitVersionEntity.AddrWardVn)),
        ("org_addr_ward_en", nameof(OrgUnitVersionEntity.AddrWardEn)),
        ("org_addr_district_vn", nameof(OrgUnitVersionEntity.AddrDistrictVn)),
        ("org_addr_district_en", nameof(OrgUnitVersionEntity.AddrDistrictEn)),
        ("org_addr_province_vn", nameof(OrgUnitVersionEntity.AddrProvinceVn)),
        ("org_addr_province_en", nameof(OrgUnitVersionEntity.AddrProvinceEn)),
        ("org_admin_division_level", nameof(OrgUnitVersionEntity.AdminDivisionLevel)),
        ("org_name_full_en", nameof(OrgUnitVersionEntity.NameFullEn)),
        ("org_name_short_en", nameof(OrgUnitVersionEntity.NameShortEn)),
        ("org_phone", nameof(OrgUnitVersionEntity.Phone)),
        ("org_fax", nameof(OrgUnitVersionEntity.Fax)),
        ("org_email", nameof(OrgUnitVersionEntity.Email)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(OrgUnitVersionEntity newValues) =>
        newValues.ParentId.HasValue
            ? new Dictionary<string, long> { ["parent_id"] = newValues.ParentId.Value }
            : new Dictionary<string, long>();

    // Own connection, so the header does NOT commit with its first version. NO production caller remains
    // (backlog 0.4b closed 2026-08-17): the Add path mints through the context overload below. Kept for
    // test fixtures only, which seed headers directly. AST.Meta.Tests/OrgUnitWritePathAbsenceTests guards
    // that production does not reach for it again.
    public async Task<long> CreateIdentityAsync()
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");
    }

    // §7: mints the org-unit identity inside the caller's composite transaction — header and first version
    // commit or roll back together, so a failed first version leaves no row to compensate. Concrete-only,
    // like every other composite overload (rule-module-boundary §3).
    internal Task<long> CreateIdentityAsync(ICompositeWriteContext context) => InsertIdentityAsync(context);

    // N1 root-uniqueness probe: the periods of every ACTIVE root version (parent_id IS NULL), for the
    // caller to test against its own candidate period. Returns periods rather than a yes/no so the overlap
    // decision uses the one shared EffectivePeriod.Overlaps home (rule-prefer-existing) instead of a second,
    // hand-written SQL overlap predicate.
    //
    // Reads on context.Connection/Transaction so it sees the composite's own in-flight writes. NO scope
    // filter, deliberately: an operator who cannot SEE the existing root must still not be able to create a
    // second one, so this probe reads the whole tree regardless of who is asking. (Until 2026-08-17 the
    // caller expressed that intent by passing a synthetic Global DataScope to GetInScopeAsync — which also
    // tied the probe to a single as-of DATE, and so could not see a future-dated root at all. Reading the
    // table directly is what removes that coupling; do not reintroduce a scope-filtered read here.)
    //
    // isactive = 1 is the whole candidate set: a cancelled or superseded version is isactive = 0, while an
    // active remnant left by a split is a real root for the days it covers and must still count.
    internal async Task<IReadOnlyList<EffectivePeriod>> GetActiveRootPeriodsAsync(ICompositeWriteContext context)
    {
        var rows = await context.Connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            """
            SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo
            FROM org_unit_version
            WHERE isactive = 1 AND parent_id IS NULL
            """,
            transaction: context.Transaction);

        return rows.Select(r => new EffectivePeriod(r.EffectiveFrom, r.EffectiveTo)).ToList();
    }

    // The DISTINCT parent_id values across an identity's ACTIVE versions, read under the composite's own
    // lock so the answer is authoritative rather than merely recent. Returns the SET, not a single value,
    // because the pre-0.7 writer put parent_id on every version and so a mixed-parent history is
    // representable: for such an identity "the stored parent" has no single answer, and a caller that
    // needs one must be able to see that rather than be handed an arbitrary row's value.
    //
    // NULL (root) is a real element of that set, so it is projected to a nullable and counted like any
    // other value -- a unit that is root on one active version and attached on another yields TWO
    // elements, which is exactly the case this read exists to expose.
    internal async Task<IReadOnlyList<long?>> GetActiveParentIdsAsync(ICompositeWriteContext context, long orgUnitId)
    {
        var rows = await context.Connection.QueryAsync<long?>(
            """
            SELECT DISTINCT parent_id
            FROM org_unit_version
            WHERE org_unit_id = @orgUnitId AND isactive = 1
            """,
            new { orgUnitId },
            transaction: context.Transaction);

        return rows.ToList();
    }

    // ONE version's parent_id, read under the composite's own lock. Null RESULT and null ELEMENT mean
    // different things and the caller must be able to tell them apart: no such active version (the row
    // vanished or was deactivated after a pre-lock read) versus an active version whose parent is null,
    // i.e. a ROOT. A plain `long?` would collapse the two into the same value.
    internal async Task<(bool Found, long? ParentId)> GetVersionParentIdAsync(
        ICompositeWriteContext context, long orgUnitId, long versionId)
    {
        var rows = await context.Connection.QueryAsync<long?>(
            """
            SELECT parent_id
            FROM org_unit_version
            WHERE id = @versionId AND org_unit_id = @orgUnitId AND isactive = 1
            """,
            new { orgUnitId, versionId },
            transaction: context.Transaction);

        var list = rows.ToList();
        return list.Count == 0 ? (false, null) : (true, list[0]);
    }

    // Compensating action for a failed first-version UpsertAsync right after the OWN-CONNECTION
    // CreateIdentityAsync above. NO production caller remains — the composite path rolls back instead of
    // compensating (§7). Kept for test fixtures only.
    public async Task DeleteEmptyIdentityAsync(long orgUnitId)
    {
        using var connection = Connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            DELETE FROM org_unit
            WHERE id = @orgUnitId
              AND NOT EXISTS (SELECT 1 FROM org_unit_version WHERE org_unit_id = @orgUnitId)
            """,
            new { orgUnitId });
    }

    public async Task<IReadOnlyList<OrgUnitVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
    {
        var rows = await QueryInScopeAsync(scope, asOf);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<OrgUnitVersionDto>> GetByIdentityAsync(long orgUnitId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(orgUnitId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    public async Task<IReadOnlyList<OrgUnitPickerItem>> GetEligibleParentsAsync(DataScope scope, EffectivePeriod childPeriod)
    {
        // Candidate universe: any identity with SOME version effective at the period's START -- if an identity's
        // continuous coverage begins exactly at childPeriod.From (the only way to have no leading gap), a version
        // effective at that date must exist. Anything absent here cannot possibly cover the whole period.
        var candidates = await QueryInScopeAsync(scope, childPeriod.From);
        var eligible = new List<OrgUnitPickerItem>();

        foreach (var candidate in candidates)
        {
            var coverage = parentCoverage.GetActiveCoverage(VersionTable, candidate.IdentityId, null);
            if (!CoverageGap.TryFind(coverage, childPeriod, out _))
            {
                eligible.Add(new OrgUnitPickerItem(candidate.IdentityId, $"{candidate.OrgCode} — {candidate.OrgNameShortVn}"));
            }
        }

        return eligible;
    }

    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        long orgUnitId, EffectivePeriod period, string orgCode, string orgNameFullVn, string orgNameShortVn, long? parentId,
        VersionOperationKind operationKind, string recordedBy, string? reason, OrgUnitSupplementalDto? supplemental = null)
    {
        using (var connection = Connections.CreateConnection())
        {
            if (await FindCodeInUseAsync(connection, null, orgUnitId, period, orgCode) is { } codeError)
            {
                return codeError;
            }
        }

        var newValues = BuildVersionEntity(
            orgUnitId, period, orgCode, orgNameFullVn, orgNameShortVn, parentId, recordedBy, reason, supplemental);

        return await UpsertVersionAsync(orgUnitId, period, newValues, recordedBy, reason, operationKind);
    }

    // Composite-write overload: identical inputs, enlisted in the caller's transaction. Delegates to the
    // base seam, exactly like RoleRepository.UpsertAsync(ICompositeWriteContext, …). Concrete-only, NOT on
    // IOrgUnitRepository (rule-module-boundary §3).
    //
    // The P6 pre-check MUST run on context.Connection/Transaction, not a fresh connection: under READ
    // COMMITTED a separate connection cannot see the composite's own in-flight writes, so a code conflict
    // created earlier in this same transaction would go unseen.
    internal async Task<ErrorOr<UpsertResult>> UpsertAsync(
        ICompositeWriteContext context, long orgUnitId, EffectivePeriod period, string orgCode,
        string orgNameFullVn, string orgNameShortVn, long? parentId, VersionOperationKind operationKind,
        string recordedBy, string? reason, OrgUnitSupplementalDto? supplemental = null)
    {
        if (await FindCodeInUseAsync(context.Connection, context.Transaction, orgUnitId, period, orgCode) is { } codeError)
        {
            return codeError;
        }

        var newValues = BuildVersionEntity(
            orgUnitId, period, orgCode, orgNameFullVn, orgNameShortVn, parentId, recordedBy, reason, supplemental);

        return await UpsertVersionAsync(context, orgUnitId, period, newValues, recordedBy, reason, operationKind);
    }

    // P6: org_code unique among OTHER identities whose active periods overlap this upsert.
    // `org_unit_id <> @orgUnitId` excludes this identity's own rows, so extending/editing the same unit is
    // never a self-duplicate — and on the composite path it is also what stops the row this very
    // transaction is about to insert from colliding with itself.
    // Plain SELECT (no lock) — race-adequate under single-active-admin (E1).
    // ONE home for the predicate and its message; both overloads above call it with their own connection.
    private static async Task<Error?> FindCodeInUseAsync(
        IDbConnection connection, IDbTransaction? transaction, long orgUnitId, EffectivePeriod period, string orgCode)
    {
        var candidates = await connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            """
            SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo
            FROM org_unit_version
            WHERE isactive = 1
              AND org_code = @orgCode
              AND org_unit_id <> @orgUnitId
            """,
            new { orgCode, orgUnitId },
            transaction);

        foreach (var row in candidates)
        {
            if (new EffectivePeriod(row.EffectiveFrom, row.EffectiveTo).Overlaps(period))
            {
                return Error.Validation(
                    "OrgUnit.CodeInUse",
                    $"Org code '{orgCode}' is already in use by another org unit for an overlapping effective period.");
            }
        }

        return null;
    }

    private static OrgUnitVersionEntity BuildVersionEntity(
        long orgUnitId, EffectivePeriod period, string orgCode, string orgNameFullVn, string orgNameShortVn,
        long? parentId, string recordedBy, string? reason, OrgUnitSupplementalDto? supplemental)
    {
        var s = supplemental ?? new OrgUnitSupplementalDto();
        var newValues = new OrgUnitVersionEntity
        {
            IdentityId = orgUnitId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            OrgCode = orgCode,
            OrgNameFullVn = orgNameFullVn,
            OrgNameShortVn = orgNameShortVn,
            ParentId = parentId,
            BusinessNumber = s.BusinessNumber,
            AddrLineVn = s.AddrLineVn,
            AddrLineEn = s.AddrLineEn,
            AddrWardVn = s.AddrWardVn,
            AddrWardEn = s.AddrWardEn,
            AddrDistrictVn = s.AddrDistrictVn,
            AddrDistrictEn = s.AddrDistrictEn,
            AddrProvinceVn = s.AddrProvinceVn,
            AddrProvinceEn = s.AddrProvinceEn,
            AdminDivisionLevel = s.AdminDivisionLevel,
            NameFullEn = s.NameFullEn,
            NameShortEn = s.NameShortEn,
            Phone = s.Phone,
            Fax = s.Fax,
            Email = s.Email,
            RecordedBy = recordedBy,
            Reason = reason,
        };

        return newValues;
    }

    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        long orgUnitId, long versionId, DateOnly operationDate, string recordedBy, string reason) =>
        CancelVersionAsync(orgUnitId, versionId, operationDate, recordedBy, reason);

    // Composite-context sibling of CancelPlanAsync above (business-layer atomic audit_log write, same
    // rationale as RoleRepository.CancelPlanAsync(ICompositeWriteContext, …)): unblocks
    // OrgUnitDeclarationService, which is in a different assembly than VersionedRepository and is not
    // itself a subclass, so it cannot reach the base's `protected internal CancelVersionAsync
    // (ICompositeWriteContext, …)` (AST.Infrastructure/VersionedRepository.cs) directly. No admin-flag
    // gate here — org units carry no such concept; a straight passthrough to the base seam.
    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        ICompositeWriteContext context, long orgUnitId, long versionId, DateOnly operationDate, string recordedBy,
        string reason) =>
        CancelVersionAsync(context, orgUnitId, versionId, operationDate, recordedBy, reason);

    // Composite-write wrapper for Close (business-layer atomic audit_log write): same accessibility
    // rationale as CancelPlanAsync(ICompositeWriteContext, …) above — the base's composite overload
    // (VersionedRepository.CloseVersionAsync(ICompositeWriteContext, …)) is `protected internal`,
    // unreachable from OrgUnitDeclarationService's assembly. The base's PLAIN (non-composite)
    // CloseVersionAsync is already `public`, so this composite sibling has the SAME name as that
    // inherited public member — `new` hides it deliberately (a distinct overload distinguished by the
    // leading `ICompositeWriteContext` parameter; no ambiguity), rather than picking a different name,
    // mirroring RoleRepository.CloseVersionAsync(ICompositeWriteContext, …).
    public new Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        ICompositeWriteContext context, long orgUnitId, long versionId, DateOnly newTo,
        OperationDate operationDate, string recordedBy, string? reason) =>
        base.CloseVersionAsync(context, orgUnitId, versionId, newTo, operationDate, recordedBy, reason);

    // H2 (N9): read-only preview of isactive=1 versions whose period overlaps the proposed upsert period.
    public async Task<IReadOnlyList<OrgUnitVersionDto>> PreviewUpsertAsync(long orgUnitId, EffectivePeriod period)
    {
        var businessSelect = string.Join(", ", BusinessColumns.Select(c => $"{c.Column} AS {c.Property}"));
        var sql =
            $"""
            SELECT id AS Id,
                   org_unit_id AS IdentityId,
                   effective_from AS EffectiveFrom,
                   effective_to AS EffectiveTo,
                   isactive AS IsActive,
                   recorded_at AS RecordedAt,
                   recorded_by AS RecordedBy,
                   reason AS Reason,
                   status AS Status,
                   {businessSelect}
            FROM org_unit_version
            WHERE isactive = 1 AND org_unit_id = @orgUnitId
            """;

        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<OrgUnitVersionEntity>(sql, new { orgUnitId });

        return rows
            .Where(r => new EffectivePeriod(r.EffectiveFrom, r.EffectiveTo).Overlaps(period))
            .Select(ToDto)
            .ToList();
    }

    // Full timeline of identities IN `scope` — every version ever recorded (active, inactive,
    // cancelled alike), no isactive/period filter. History-grid read (Phase 4d). `orgUnitId` null
    // means every identity in scope; the scope predicate is applied server-side (mirrors
    // StandardScopeFilterBuilder's per-level switch shape so semantics cannot drift from
    // GetInScopeAsync) so an out-of-scope id returns empty.
    public async Task<IReadOnlyList<OrgUnitVersionDto>> GetHistoryInScopeAsync(DataScope scope, long? orgUnitId = null)
    {
        EnsureScopeApplicable(scope.Level);

        var businessSelect = string.Join(", ", BusinessColumns.Select(c => $"h.{c.Column} AS {c.Property}"));
        var parameters = new DynamicParameters();
        parameters.Add("orgUnitId", orgUnitId);

        var clauses = new List<string>
        {
            "(@orgUnitId IS NULL OR h.org_unit_id = @orgUnitId)",
            BuildHistoryScopeClause(scope, parameters),
        };

        var sql =
            $"""
            SELECT h.id AS Id,
                   h.org_unit_id AS IdentityId,
                   h.effective_from AS EffectiveFrom,
                   h.effective_to AS EffectiveTo,
                   h.isactive AS IsActive,
                   h.recorded_at AS RecordedAt,
                   h.recorded_by AS RecordedBy,
                   h.reason AS Reason,
                   h.status AS Status,
                   h.operation_kind AS OperationKind,
                   p.org_code AS ParentOrgCodeAsOf,
                   p.org_name_full_vn AS ParentOrgNameFullVnAsOf,
                   {businessSelect}
            FROM org_unit_version h
            LEFT JOIN org_unit_version p
                   ON p.org_unit_id = h.parent_id
                  AND p.isactive = 1
                  AND h.effective_from >= p.effective_from
                  AND h.effective_from <= p.effective_to
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY h.recorded_at DESC, h.id DESC
            """;

        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<OrgUnitVersionEntity>(sql, parameters);

        return rows.Select(ToDto).ToList();
    }

    // Scope-checked-write membership primitive (2026-08-05 security fix): does `orgUnitId` fall
    // within `scope` at ANY point in its full version history? Reuses BuildHistoryScopeClause --
    // the SAME per-ScopeLevel predicate as GetHistoryInScopeAsync -- so "in scope" for a write
    // gate can never drift from "in scope" for the history-grid read (spec 2.7.6 already treats
    // history visibility this way). Deliberately an EXISTS/LIMIT-1 probe rather than delegating to
    // GetHistoryInScopeAsync and checking Count>0: that method also joins the parent-as-of table
    // and selects every business column for grid display, which would be wasted work here.
    public async Task<bool> IsWithinScopeAsync(DataScope scope, long orgUnitId)
    {
        EnsureScopeApplicable(scope.Level);

        var parameters = new DynamicParameters();
        parameters.Add("orgUnitId", orgUnitId);
        var scopeClause = BuildHistoryScopeClause(scope, parameters);

        var sql =
            $"""
            SELECT EXISTS (
                SELECT 1 FROM org_unit_version h
                WHERE h.org_unit_id = @orgUnitId AND {scopeClause}
            )
            """;

        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(sql, parameters);
    }

    // Shared scope predicate for the org_unit_version HISTORY table (alias `h`, no isactive/period
    // filter -- rule-effective-period invariant #2 deliberately does not apply here, same rationale
    // as GetHistoryInScopeAsync above). Factored out so GetHistoryInScopeAsync (list) and
    // IsWithinScopeAsync (existence probe) can never diverge on what "in scope" means for a unit's
    // history. NOT the same predicate as StandardScopeFilterBuilder.Build (that one is "as of
    // TODAY" for authorization/tree reads) -- this one is intentionally undated.
    private string BuildHistoryScopeClause(DataScope scope, DynamicParameters parameters)
    {
        switch (scope.Level)
        {
            case ScopeLevel.Global:
                // Always-true -- mirrors StandardScopeFilterBuilder's Global case (no restriction).
                return "1=1";

            case ScopeLevel.OwnOrgUnit:
                parameters.Add("rootOrgUnitId", scope.RootOrgUnitId);
                return "h.org_unit_id = @rootOrgUnitId";

            case ScopeLevel.OwnOrgUnitAndDescendants:
                parameters.Add("rootOrgUnitId", scope.RootOrgUnitId);
                return $"h.org_unit_id IN ({HistorySubtreeCte()})";

            case ScopeLevel.Self:
            default:
                // Unreachable: EnsureScopeApplicable already throws for Self on this entity
                // (no owner column) -- kept only for switch exhaustiveness.
                throw new InvalidOperationException(
                    $"Phạm vi {scope.Level} không áp dụng được cho lịch sử '{VersionTable}'.");
        }
    }

    // Historical subtree CTE for OwnOrgUnitAndDescendants -- deliberately DIFFERENT from
    // StandardScopeFilterBuilder.BuildSubtreeCte (which is "as-of TODAY" for authorization/tree
    // reads): (a) NO @today/period/isactive filter on the recursive join, so a cancelled-or-
    // future-only unit's descendants stay reachable for history purposes (filtering here would
    // reintroduce the exact bug this history feature exists to avoid); (b) UNION DISTINCT (not
    // UNION ALL) as the cycle guard -- the recursive SELECT projects EXACTLY the `id` column so
    // MySQL's column-set dedup actually applies (cte_max_recursion_depth, default 1000, is the
    // backstop if a cycle ever did get through); (c) no ROW_NUMBER dedup -- that exists in
    // BuildSubtreeCte only to pick one row per identity at a single as-of date, irrelevant here.
    private static string HistorySubtreeCte() =>
        """
        WITH RECURSIVE hist_subtree AS (
          SELECT @rootOrgUnitId AS id
          UNION DISTINCT
          SELECT v.org_unit_id FROM org_unit_version v JOIN hist_subtree s ON v.parent_id = s.id
        ) SELECT id FROM hist_subtree
        """;

    private static OrgUnitVersionDto ToDto(OrgUnitVersionEntity e) =>
        new(
            e.Id,
            e.IdentityId,
            e.EffectiveFrom,
            e.EffectiveTo,
            e.IsActive,
            e.OrgCode,
            e.OrgNameFullVn,
            e.OrgNameShortVn,
            e.ParentId,
            e.RecordedAt,
            e.RecordedBy,
            e.Reason,
            new OrgUnitSupplementalDto(
                e.BusinessNumber,
                e.AddrLineVn,
                e.AddrLineEn,
                e.AddrWardVn,
                e.AddrWardEn,
                e.AddrDistrictVn,
                e.AddrDistrictEn,
                e.AddrProvinceVn,
                e.AddrProvinceEn,
                e.AdminDivisionLevel,
                e.NameFullEn,
                e.NameShortEn,
                e.Phone,
                e.Fax,
                e.Email),
            e.Status,
            e.OperationKind,
            e.ParentOrgCodeAsOf,
            e.ParentOrgNameFullVnAsOf);
}
