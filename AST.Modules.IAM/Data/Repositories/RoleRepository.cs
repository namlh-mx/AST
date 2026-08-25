using System.Data;
using AST.Core.Data;
using AST.Infrastructure;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Time;
using AST.Modules.IAM.Data.Entities;
using Dapper;
using ErrorOr;

namespace AST.Modules.IAM.Data.Repositories;

internal sealed class RoleRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates)
    : VersionedRepository<RoleVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates),
        IRoleRepository
{
    protected override string VersionTable => "role_version";
    protected override string IdentityColumn => "role_id";

    // role is a system-wide configuration entity, not owned by any org unit -> keep the default "NULL"
    // (Self/OwnOrgUnit/Descendants THROW a clear error if mistakenly used with this entity — EnsureScopeApplicable;
    // Global is always valid).
    protected override bool SupportsCancellation => true;
    protected override bool RecordsOperationKind => true;

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("role_code", nameof(RoleVersionEntity.RoleCode)),
        ("role_name", nameof(RoleVersionEntity.RoleName)),
        ("is_admin_role", nameof(RoleVersionEntity.IsAdminRole)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(RoleVersionEntity newValues) =>
        new Dictionary<string, long>();

    // P11 (spec §15.2 D-7): a role's permission grants exist ONLY to express that role's permissions —
    // they are not independently addressable by another aggregate, so closing/narrowing the role may
    // auto-cut them in the same transaction. The `function` side of the very same grant does NOT own it
    // (FunctionRepository deliberately declares nothing) — closing a function still BLOCKs on reverse-FK.
    protected override IReadOnlyList<AutoCutDependent> ExclusivelyOwnedDependents =>
    [
        new AutoCutDependent("role_permission_version", "role_id", "role_permission_id", DependentSupportsCancellation: true),
    ];

    // N-14 #2: ≤1 admin-flag-true role active system-wide on any day. Callers composing an
    // isAdminRole:true Upsert through CompositeWrite MUST Enlist(this key) before ExecuteAsync —
    // the repository cannot self-enlist (it does not own the CompositeWrite). RoleDeclarationService
    // (AST.Modules.IAM/RoleDeclarationService.cs, OPEN-B2) now Enlists this key UNCONDITIONALLY on
    // every Save, closing the race for that orchestrated path; any OTHER caller that composes an
    // isAdminRole:true Upsert directly (bypassing RoleDeclarationService) is still on its own honesty
    // — the integrity sweep (N-14) remains the backstop for that bypass.
    public const string AdminFlagLockKey = "iam:role:admin-flag-singleton";

    // B2 (2026-08-14): serialises every save touching a given role CODE, so "which identity owns this
    // code?" is decided under a lock instead of by a racing read. RoleDeclarationService Enlists it for
    // the submitted code AND for the loaded role's code on an edit.
    //
    // The `!code` segment sorts BEFORE AdminFlagLockKey in CompositeWrite's ordinal extra-key order
    // (`iam:role:!…` < `iam:role:admin…`), so a save blocked on its code lock does not hold the admin
    // singleton and unrelated codes can still proceed (brief 075 FR-3 pin test).
    //
    // NORMALISATION IS CORRECTNESS: role_code is utf8mb4_0900_ai_ci (migrations/V002__role.sql:29), so
    // the DB reads 'vt-kt' and 'VT-KT' as ONE code. A key that told them apart would let two concurrent
    // declarations hold different locks and both pass the ownership re-check. Upper-invariant matches the
    // collation for case; accents are NOT folded, which would make the key NARROWER than the comparison —
    // so a non-ASCII code is rejected by RoleDeclarationService BEFORE this is called. The throw below is
    // therefore unreachable defence-in-depth: do not write a test to reach it (see Task 2).
    //
    // The predicate is `c > 127 || char.IsControl(c)`, character for character the same one
    // RoleDeclarationService applies at the boundary. It used to be `c > 127` alone (closed 2026-08-15,
    // queued by slice 1): two definitions of "an acceptable code" one call apart is the exact drift shape
    // that slice cost two fix rounds to remove elsewhere, and an unreachable guard that disagrees with the
    // reachable one is a trap for whoever makes it reachable.
    public static string CodeLockKey(string roleCode)
    {
        var trimmed = roleCode.Trim();
        if (trimmed.Any(c => c > 127 || char.IsControl(c)))
        {
            throw new ArgumentException(
                $"Role code '{roleCode}' contains a non-ASCII or control character; the caller must reject it "
                + "before deriving a lock key.",
                nameof(roleCode));
        }

        return $"iam:role:!code:{trimmed.ToUpperInvariant()}";
    }

    public sealed record RoleCodeOwner(long RoleId, bool HasVersionInForceToday, bool HasFutureVersion);

    // Dapper cannot materialise a positional record when MySQL returns role_id as ulong and MAX(...) as
    // long — same rationale as ActiveAdminProbe below. Map through this row type into RoleCodeOwner.
    private sealed class RoleCodeOwnerRow
    {
        public long RoleId { get; init; }
        public bool HasVersionInForceToday { get; init; }
        public bool HasFutureVersion { get; init; }
    }

    // B2 — which identities have EVER carried this code, and whether each still has a version in force
    // today. Deliberately unfiltered by isactive/date for OWNERSHIP (its purpose is to find the closed
    // role a re-declared code belongs to, which the today-only resolution cannot see, F7), while
    // `HasVersionInForceToday` is computed in the SAME statement so "may I reattach?" cannot be answered
    // from a second, later round-trip.
    private const string CodeOwnersSql =
        """
        SELECT CAST(rv.role_id AS SIGNED)                                              AS RoleId,
               (MAX(rv.isactive = 1 AND rv.effective_from <= @today
                                   AND rv.effective_to   >= @today) > 0)              AS HasVersionInForceToday,
               (MAX(rv.isactive = 1 AND rv.effective_from >  @today) > 0)              AS HasFutureVersion
        FROM role_version rv
        WHERE rv.role_id IN (SELECT DISTINCT role_id FROM role_version WHERE role_code = @roleCode)
        GROUP BY rv.role_id
        ORDER BY rv.role_id
        """;

    internal async Task<IReadOnlyList<RoleCodeOwner>> GetCodeOwnersAsync(string roleCode, DateOnly today)
    {
        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<RoleCodeOwnerRow>(CodeOwnersSql, new { roleCode, today });
        return rows.Select(r => new RoleCodeOwner(r.RoleId, r.HasVersionInForceToday, r.HasFutureVersion)).ToList();
    }

    internal async Task<IReadOnlyList<RoleCodeOwner>> GetCodeOwnersAsync(
        ICompositeWriteContext context, string roleCode, DateOnly today)
    {
        var rows = await context.Connection.QueryAsync<RoleCodeOwnerRow>(
            CodeOwnersSql, new { roleCode, today }, context.Transaction);
        return rows.Select(r => new RoleCodeOwner(r.RoleId, r.HasVersionInForceToday, r.HasFutureVersion)).ToList();
    }

    // Own connection, so the header does NOT commit with its first version. NO production caller remains —
    // it is kept for test fixtures only (backlog 0.4b's one un-migrated caller is org_unit's own repository,
    // not this one; `function` migrated 2026-08-17). The production role-save path uses the context overload below;
    // AST.Meta.Tests/RoleWritePathAbsenceTests guards that it stays that way.
    public async Task<long> CreateIdentityAsync()
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO `role` () VALUES (); SELECT LAST_INSERT_ID();");
    }

    // §7: mints the role identity inside the caller's composite transaction — header and first version
    // commit or roll back together. Concrete-only, like every other composite overload
    // (rule-module-boundary §3).
    internal Task<long> CreateIdentityAsync(ICompositeWriteContext context) => InsertIdentityAsync(context);

    // N6 wrapper (Seam-2 base `CancelVersionAsync` is `protected`; not on `IRoleRepository` by design —
    // role version creation goes only through `IRoleDeclarationService`; this Cancel path stays on the
    // concrete type, reached via same-assembly `internal`, no interface needed). Mirrors OrgUnitRepository.CancelPlanAsync's shape (line 188-189)
    // adapted to Role's own nullability: `role_version.reason` is an OPTIONAL "Ghi chú" (nullable column,
    // migrations/V002__role.sql), but the shared base takes a non-nullable `reason` — coerced to
    // `string.Empty` explicitly here rather than widening the shared base signature for one entity's need.
    //
    // Predecessor-aware admin-flag gate: the base's
    // `CancelVersionCoreAsync` (AST.Infrastructure/VersionedRepository.cs, restore-adjacent-predecessor
    // branch) resolves an adjacent predecessor (`v.Id != versionId && v.EffectiveTo ==
    // target.EffectiveFrom.AddDays(-1)`), soft-deactivates it, and re-inserts a remnant covering
    // `[predecessor.EffectiveFrom … target.EffectiveTo]` via `InsertRemnantAsync`, which copies business
    // columns VERBATIM — including `is_admin_role`. So cancelling a plain (non-admin) future plan can
    // silently RESTORE an admin predecessor to active, open-ended coverage: a cancel can GRANT admin-flag
    // coverage, not only remove it — the exact mirror of what the bidirectional gate in `UpsertAsync`
    // above forbids. Gated here (target OR predecessor's admin flag), required (no default), so no
    // same-assembly caller can forget it. The CLOSE branch's counterpart gate is target-only and lives in
    // `RoleDeclarationService.CloseRoleDeclarationAsync` — Close never restores a predecessor, so a
    // target-only check is correct there; this comment cross-references that gate so neither reads as the
    // sole home.
    //
    // `LoadActiveVersionsAsync` (the base's equivalent read) is `private` — not reachable from this
    // subclass — so this is a fresh plain SELECT, matching the SAME predicate/style as the base's
    // (`role_id = @roleId AND isactive = 1`, no period filter: multiple simultaneously-active versions
    // are legal under the 8-case algebra). Race posture: this read runs OUTSIDE the base's
    // transaction/lock — accepted as "race-adequate under single-active-admin (E1)"; do not restructure
    // the base to move this inside the transaction, that is out of scope for this fix.
    public async Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        long roleId, long versionId, DateOnly operationDate, bool adminFlagChangeAuthorized, string recordedBy,
        string? reason)
    {
        using (var connection = Connections.CreateConnection())
        {
            var versions = await LoadActiveAdminProbesAsync(connection, null, roleId);
            var gateError = EvaluateCancelAdminFlagGate(versions, versionId, adminFlagChangeAuthorized);
            if (gateError is not null)
            {
                return gateError.Value;
            }
        }

        return await CancelVersionAsync(roleId, versionId, operationDate, recordedBy, reason ?? string.Empty);
    }

    // Composite-context sibling of CancelPlanAsync above (business-layer atomic audit_log write, qa
    // F2): unblocks RoleDeclarationService, which is in a different assembly than
    // VersionedRepository and is not itself a subclass, so it cannot reach the base's `protected
    // internal CancelVersionAsync(ICompositeWriteContext, …)` directly — mirrors
    // UpsertAsync(ICompositeWriteContext, …) above and CloseVersionAsync(ICompositeWriteContext, …)
    // below. Carries the SAME predecessor-aware admin-flag gate as the plain overload — factored into
    // EvaluateCancelAdminFlagGate below so the predicate/message exist exactly ONCE, per
    // rule-prefer-existing.
    //
    // MUST read active versions via context.Connection/context.Transaction (never a fresh
    // Connections.CreateConnection()) so the read sees the composite's own in-flight writes under READ
    // COMMITTED — the same rule UpsertAsync(ICompositeWriteContext, …) already states for its own
    // `currentlyAdmin` EXISTS and `role_code` uniqueness checks.
    public async Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        ICompositeWriteContext context, long roleId, long versionId, DateOnly operationDate,
        bool adminFlagChangeAuthorized, string recordedBy, string? reason)
    {
        var versions = await LoadActiveAdminProbesAsync(context.Connection, context.Transaction, roleId);
        var gateError = EvaluateCancelAdminFlagGate(versions, versionId, adminFlagChangeAuthorized);
        if (gateError is not null)
        {
            return gateError.Value;
        }

        return await CancelVersionAsync(context, roleId, versionId, operationDate, recordedBy, reason ?? string.Empty);
    }

    // Composite-write wrapper for Close (business-layer atomic audit_log write): same
    // accessibility rationale as the CancelPlanAsync(ICompositeWriteContext, …) wrapper above — the
    // base's composite overload (VersionedRepository.CloseVersionAsync(ICompositeWriteContext, …)) is
    // `protected internal`, unreachable from RoleDeclarationService's assembly. Unlike Cancel, the
    // base's PLAIN (non-composite) CloseVersionAsync is already `public`, so this composite sibling has
    // the SAME name as that inherited public member — `new` hides it deliberately (a distinct
    // overload distinguished by the leading `ICompositeWriteContext` parameter; no ambiguity), rather
    // than picking a different name, so both Close entry points read as one pair like the base's own.
    //
    // No admin-flag gate here: Close only shrinks the target's own coverage and never restores a
    // predecessor (that is Cancel's job above), so the Close-side admin-flag gate is target-only and
    // lives in RoleDeclarationService.CloseRoleDeclarationAsync — this wrapper is a straight
    // passthrough to the base seam, not a place a gate was forgotten.
    public new Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        ICompositeWriteContext context, long roleId, long versionId, DateOnly newTo,
        OperationDate operationDate, string recordedBy, string? reason) =>
        base.CloseVersionAsync(context, roleId, versionId, newTo, operationDate, recordedBy, reason);

    // Shared read for both CancelPlanAsync overloads (rule-prefer-existing: extend once, not
    // duplicate). `LoadActiveVersionsAsync` (the base's equivalent) is `private` — not reachable from
    // this subclass — so this is a fresh plain SELECT, matching the SAME predicate/style as the base's
    // (`role_id = @roleId AND isactive = 1`, no period filter: multiple simultaneously-active versions
    // are legal under the 8-case algebra). `transaction: null` for the plain-path caller — Dapper
    // treats a null transaction as "use the connection's own ambient state", which is correct there
    // since that path holds no composite transaction.
    private static async Task<List<ActiveAdminProbe>> LoadActiveAdminProbesAsync(
        IDbConnection connection, IDbTransaction? transaction, long roleId)
    {
        var rows = await connection.QueryAsync<ActiveAdminProbe>(
            """
            SELECT id AS Id, effective_from AS EffectiveFrom, effective_to AS EffectiveTo, is_admin_role AS IsAdminRole
            FROM role_version
            WHERE role_id = @roleId AND isactive = 1
            """,
            new { roleId },
            transaction);
        return rows.ToList();
    }

    // Predecessor-aware admin-flag gate, factored out of both
    // CancelPlanAsync overloads so the predicate/message exist exactly ONCE: see the comment above the
    // plain CancelPlanAsync overload for the full rationale (restoring a predecessor can silently GRANT
    // admin-flag coverage, the mirror of what UpsertAsync's bidirectional gate forbids). Returns null
    // when there is no active version with this id — the caller falls through and lets the base's
    // CancelVersionCoreAsync return its own `VersionedRepository.VersionNotFound`, this helper does not
    // pre-empt that error surface.
    private static Error? EvaluateCancelAdminFlagGate(
        IReadOnlyList<ActiveAdminProbe> versions, long versionId, bool adminFlagChangeAuthorized)
    {
        var target = versions.FirstOrDefault(v => v.Id == versionId);
        if (target is null)
        {
            return null;
        }

        var predecessor = versions.FirstOrDefault(
            v => v.Id != versionId && v.EffectiveTo == target.EffectiveFrom.AddDays(-1));

        if ((target.IsAdminRole || predecessor?.IsAdminRole == true) && !adminFlagChangeAuthorized)
        {
            return Error.Forbidden(
                "Role.AdminFlagChangeNotAuthorized",
                "Cancelling this plan would change admin-flag coverage (the version restored in its place is an admin role); that requires break-glass authority.");
        }

        return null;
    }

    // Plain class with init-only properties, not a positional record: Dapper's record-constructor
    // binding requires an EXACT parameter-type match (e.g. it rejects role.id's actual reader type
    // `ulong` against a `long` parameter), whereas property-setter binding performs the usual numeric
    // widening/narrowing conversion — matching the style already used by RoleVersionEntity/VersionFlags
    // in this assembly.
    private sealed class ActiveAdminProbe
    {
        public long Id { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly EffectiveTo { get; init; }
        public bool IsAdminRole { get; init; }
    }

    public async Task DeleteEmptyIdentityAsync(long roleId)
    {
        using var connection = Connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            DELETE FROM `role`
            WHERE id = @roleId
              AND NOT EXISTS (SELECT 1 FROM role_version WHERE role_id = @roleId)
            """,
            new { roleId });
    }

    public async Task<IReadOnlyList<RoleVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
    {
        var rows = await QueryInScopeAsync(scope, asOf);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<RoleVersionDto>> GetByIdentityAsync(long roleId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(roleId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    // Composite-write overload: reads via context.Connection/Transaction (read-your-own-writes under READ
    // COMMITTED) — see RolePermissionRepository.GetByIdentityAsync(ICompositeWriteContext, …) precedent.
    // Deliberately NOT on IRoleRepository (composite overloads stay concrete-only, rule-module-boundary §3).
    public async Task<ErrorOr<RoleVersionDto>> GetByIdentityAsync(
        ICompositeWriteContext context, long roleId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(context, roleId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    public async Task<IReadOnlyList<RoleVersionDto>> GetHistoryAsync(long? roleId = null)
    {
        var businessSelect = string.Join(", ", BusinessColumns.Select(c => $"h.{c.Column} AS {c.Property}"));
        var sql =
            $"""
            SELECT h.id AS Id,
                   h.role_id AS IdentityId,
                   h.effective_from AS EffectiveFrom,
                   h.effective_to AS EffectiveTo,
                   h.isactive AS IsActive,
                   h.recorded_at AS RecordedAt,
                   h.recorded_by AS RecordedBy,
                   h.reason AS Reason,
                   h.status AS Status,
                   h.operation_kind AS OperationKind,
                   {businessSelect}
            FROM role_version h
            WHERE (@roleId IS NULL OR h.role_id = @roleId)
            ORDER BY h.recorded_at DESC, h.id DESC
            """;

        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<RoleVersionEntity>(sql, new { roleId });
        return rows.Select(ToDto).ToList();
    }

    // Composite-write overload (spec §16.1 capability 1): identical inputs, but enlisted in the caller's
    // transaction. Delegates to the base seam — see CompositeWrite.cs.
    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        ICompositeWriteContext context, long roleId, EffectivePeriod period, string roleCode, string roleName,
        bool isAdminRole, bool adminFlagChangeAuthorized, VersionOperationKind operationKind,
        OperationDate operationDate, string recordedBy, string? reason)
    {
        if (GuardImmediateUpsert(period, operationDate) is { } immediateError)
        {
            return immediateError;
        }

        // Bidirectional admin-flag gate. Period-scoped EXISTS — multi-active versions are legal
        // (8-case algebra); never LIMIT 1.
        // MUST use context.Connection/Transaction so the SELECT sees the composite's own
        // in-flight writes (READ COMMITTED otherwise misses them).
        bool currentlyAdmin = false;
        if (!isAdminRole)
        {
            currentlyAdmin = await context.Connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM role_version
                    WHERE role_id = @roleId AND isactive = 1 AND is_admin_role = 1
                      AND effective_from <= @to AND @from <= effective_to
                )
                """,
                new { roleId, from = period.From, to = period.To },
                context.Transaction);
        }

        if ((isAdminRole || currentlyAdmin) && !adminFlagChangeAuthorized)
        {
            return Error.Forbidden(
                "Role.AdminFlagChangeNotAuthorized",
                "Changing the admin-flag on a role requires an explicit authority check by the caller.");
        }

        // P6: role_code unique among OTHER identities whose active periods overlap this upsert.
        // Excludes this identity's own rows so extending/editing the same role is never a self-duplicate.
        // MUST use context.Connection/Transaction so the
        // SELECT sees the composite's own in-flight writes (READ COMMITTED otherwise misses them —
        // same rationale as VersionedRepository.EnsureCompositeDependentsEnlistedAsync).
        // Plain SELECT (no lock) — race-adequate under single-active-admin (E1).
        {
            var candidates = await context.Connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
                """
                SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo
                FROM role_version
                WHERE isactive = 1
                  AND role_code = @roleCode
                  AND role_id <> @roleId
                """,
                new { roleCode, roleId },
                context.Transaction);

            foreach (var row in candidates)
            {
                if (new EffectivePeriod(row.EffectiveFrom, row.EffectiveTo).Overlaps(period))
                {
                    return Error.Validation(
                        "Role.CodeInUse",
                        $"Role code '{roleCode}' is already in use by another role for an overlapping effective period.");
                }
            }
        }

        // Admin-flag uniqueness: integrity sweep (N-14) + caller must Enlist(AdminFlagLockKey) when
        // composing isAdminRole:true — see constant comment. This method does not Enlist.
        var newValues = new RoleVersionEntity
        {
            IdentityId = roleId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            RoleCode = roleCode,
            RoleName = roleName,
            IsAdminRole = isAdminRole,
            RecordedBy = recordedBy,
            Reason = reason,
        };

        return await UpsertVersionAsync(context, roleId, period, newValues, recordedBy, reason, operationKind);
    }

    // Immediate: the only legal written period is [operationDate, OpenEnd]. No database read.
    // A running role is renamed by closing yesterday and opening today, not by rewriting the past.
    private static Error? GuardImmediateUpsert(EffectivePeriod period, OperationDate operationDate)
    {
        var today = operationDate.Value;
        if (period.To != EffectivePeriod.OpenEnd)
        {
            return Error.Validation(
                "Role.EffectiveToMustBeOpen",
                $"A role version must end on {EffectivePeriod.OpenEnd:yyyy-MM-dd} (open end); requested {period.To:yyyy-MM-dd}.");
        }

        if (period.From == today)
        {
            return null;
        }

        return Error.Validation(
            "Role.EffectiveFromMustBeToday",
            $"A role version must start on today ({today:yyyy-MM-dd}), not {period.From:yyyy-MM-dd}.");
    }

    protected override Error? ValidateClose(OperationDate operationDate, DateOnly newTo)
    {
        var required = operationDate.Value.AddDays(-1);
        if (newTo == required)
        {
            return null;
        }

        return Error.Validation(
            "Role.CloseDateMustBeImmediate",
            $"A role close must end on today minus one day ({required:yyyy-MM-dd}); requested {newTo:yyyy-MM-dd}.");
    }

    private static RoleVersionDto ToDto(RoleVersionEntity e) =>
        new(e.Id, e.IdentityId, e.EffectiveFrom, e.EffectiveTo, e.IsActive,
            e.RoleCode, e.RoleName, e.IsAdminRole, e.RecordedAt, e.RecordedBy, e.Reason,
            e.Status, e.OperationKind);
}
