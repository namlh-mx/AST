using System.Data;
using System.Globalization;
using AST.Core;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using Dapper;
using ErrorOr;
// "EffectivePeriod" is both the namespace name (AST.Core.EffectivePeriod) and the name of the struct inside it ->
// a bare "EffectivePeriod" reference in this file is ambiguous. The "Period" alias resolves the ambiguity.
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Infrastructure;

// §3.6 (docs/design-iam-schema.md) — base repository shared by EVERY entity with an effective
// period. Modules INHERIT it, they do not write their own filter/8-case algorithm/temporal-FK/named-lock (D13a).
//
// [Note B2 — 2026-07-03] This class did NOT exist yet in "Slice A" (only a signature
// described in §3.6 of the design doc, no .cs file). This implementation follows exactly the 2
// protected method signatures (QueryInScopeAsync/UpsertVersionAsync) + the 6-dependency constructor
// listed in §3.6, PLUS the seams that had to be decided (task B2 delegated the decision, requiring it to be stated explicitly):
//  - Added constructor parameter `ITemporalFkRegistry fkRegistry` (BEYOND the 6 dependencies in §3.6): the named
//    lock (§7) must lock the relevant PARENT identities as well before validating — it needs to know
//    ParentVersionTable for each FK column. Without injecting the registry, each sub-repository would have to
//    hardcode the edge metadata again, duplicating the declaration source already in TemporalFkRegistry (AST.Core).
//  - `BusinessColumns`: declares (DB column name, DTO property name) — used to generate BOTH the select alias (SELECT
//    ... AS Property) AND the insert/remnant-copy (INSERT ... (columns,...) SELECT/VALUES ...), a single source,
//    avoiding declaring it twice in two places. The business value on insert is fetched via reflection by
//    property name (the entity only has public-get properties) — no need for a separate abstract "ToBusinessParameters"
//    since it would duplicate information already in BusinessColumns.
//  - `ExtractParentIdentityIds`: parent FK column -> parent identity id taken from the new DTO — used both to call
//    ITemporalFkValidator.ValidateChildCoverage, and to compute the parent lock-key set (named lock).
//  - `OrgUnitColumn`/`OwnerColumn`: virtual, default "NULL" (SQL literal, NOT a column name) for
//    entities without the concept of "belongs to an org unit"/"has an owner". Invoking scope levels that need
//    this column on such an entity does NOT silently return empty — `EnsureScopeApplicable` THROWS a clear error (see the method).
//    IMPORTANT: IStandardScopeFilterBuilder.Build uses the column string VERBATIM (does not add an
//    alias itself) — an override must prepend `{Alias}.<column>` itself (see AST.Core.Tests/Data/StandardScopeFilterBuilderTests
//    passing "t.org_unit_id" rather than a bare "org_unit_id").
[SharedComponent]
public abstract class VersionedRepository<TVersion> : IVersionedWriteTarget where TVersion : IVersionRow
{
    protected const string Alias = "v";

    private readonly IDbConnectionFactory _connections;
    private readonly IStandardScopeFilterBuilder _scopeFilter;
    private readonly IEffectivePeriodResolver _resolver;
    private readonly IPeriodEditor _periodEditor;
    private readonly ITemporalFkValidator _fkValidator;
    private readonly ITemporalFkRegistry _fkRegistry;

    // READ-path date ONLY (§6 "scope evaluated by TODAY", QueryInScopeAsync below) — TASK 0 (2026-08-11):
    // renamed from `_dates` so no future WRITE guard can quietly re-read it the
    // way CancelVersionCoreAsync's cancel-eligibility guard used to (design-effective-period.md §3: a
    // single business operation captures "today" ONCE, at the caller, not re-derived independently
    // inside the engine). Any write-path date needed by a guard must arrive as an explicit method
    // parameter (see CancelVersionAsync's `operationDate`), never through this field.
    private readonly IBusinessDateProvider _scopeToday;

    // For subclasses to reuse the factory (e.g. raw queries outside the scope/temporal helpers) WITHOUT having to
    // capture the ctor parameter separately -> avoids CS9107 (keeping 2 copies of the same singleton).
    protected IDbConnectionFactory Connections => _connections;

    protected VersionedRepository(
        IDbConnectionFactory connections,
        IStandardScopeFilterBuilder scopeFilter,
        IEffectivePeriodResolver resolver,
        IPeriodEditor periodEditor,
        ITemporalFkValidator fkValidator,
        ITemporalFkRegistry fkRegistry,
        IBusinessDateProvider dates)
    {
        _connections = connections;
        _scopeFilter = scopeFilter;
        _resolver = resolver;
        _periodEditor = periodEditor;
        _fkValidator = fkValidator;
        _fkRegistry = fkRegistry;
        _scopeToday = dates;
    }

    protected abstract string VersionTable { get; }
    protected abstract string IdentityColumn { get; }

    // (DB column name, DTO property name) — the property MUST exist as a public getter on TVersion.
    protected abstract IReadOnlyList<(string Column, string Property)> BusinessColumns { get; }

    // parent FK column -> parent identity id taken from the new DTO. Empty if the entity has no temporal-FK edge,
    // or skips the column when the value is NULL (e.g. root org_unit, parent_id null -> exempt from checking per the registry).
    protected abstract IReadOnlyDictionary<string, long> ExtractParentIdentityIds(TVersion newValues);

    // Sentinel: an entity WITHOUT the concept of "belongs to an org unit"/"has an owner" keeps the default "NULL"
    // (SQL literal, not a column name). An override returns the real column name when the entity has a matching column.
    protected virtual string OrgUnitColumn => "NULL";
    protected virtual string OwnerColumn => "NULL";

    // Opt-in upsert gap policy (D7 default = warn). When true, UpsertVersionAsync returns Error.Validation
    // instead of writing when PlanUpsert reports any gap warning. Close/Delete paths are unchanged.
    protected virtual bool GapIsBlocking => false;

    // Validation error code when GapIsBlocking turns a gap into a block. Default is table-derived;
    // override when the public code must differ (e.g. OrgUnit.GapNotAllowed vs org_unit_version.*).
    protected virtual string GapBlockErrorCode => $"{VersionTable}.GapNotAllowed";

    // Opt-in: entity supports canceling a version with no completed effective day (starting today or later, N6). Default false —
    // other IAM repos have no `cancelled` column; SELECT includes it only when overridden true.
    protected virtual bool SupportsCancellation => false;

    // Opt-in: entity records WHICH user-facing action (Add/Edit/Close/Cancel) produced each written row
    // (Phase 4d history-grid read). Default false — other IAM repos have no `operation_kind` column;
    // INSERT includes it only when overridden true (exact mirror of SupportsCancellation above).
    protected virtual bool RecordsOperationKind => false;

    // Opt-in (spec §16.1 capability 2 / §15.2 D-7 — P11 "aggregate auto-cut"): dependents this entity
    // EXCLUSIVELY OWNS. Empty by default => every existing repository keeps today's BLOCK-only reverse-FK
    // behaviour, exactly like GapIsBlocking/SupportsCancellation/RecordsOperationKind above. See
    // AutoCutDependent.cs for the P11 condition and the audit-trail requirement. Honoured by
    // CloseVersionAsync (auto-cut before ValidateParentChange, same transaction).
    protected virtual IReadOnlyList<AutoCutDependent> ExclusivelyOwnedDependents => [];

    // Composite-write seam (spec §16.1 capability 1). Explicit implementation so the version-table name
    // does not widen the public surface of every repository — a CompositeWrite gets it, callers do not.
    string IVersionedWriteTarget.VersionTableName => VersionTable;

    // WRITE, enlisted in a caller-owned composite transaction: same 8-case algebra + temporal-FK as
    // UpsertVersionAsync above, but running on the composite's shared connection/transaction, whose lock
    // keys were already acquired UP FRONT in the §7 fixed order by CompositeWrite.ExecuteAsync.
    //
    protected async Task<ErrorOr<UpsertResult>> UpsertVersionAsync(
        ICompositeWriteContext context, long identityId, Period period, TVersion newValues,
        string recordedBy, string? reason, VersionOperationKind? operationKind = null)
    {
        var parentIds = ExtractParentIdentityIds(newValues);
        var enlistError = EnsureCompositeEnlisted(context, identityId, parentIds);
        if (enlistError is not null)
        {
            return enlistError.Value;
        }

        var activeVersions = await LoadActiveVersionsAsync(context.Connection, context.Transaction, identityId);
        return await ApplyUpsertPlanAsync(
            context.Connection, context.Transaction, activeVersions, identityId, period, newValues,
            recordedBy, reason, operationKind);
    }

    private const string NoColumn = "NULL";

    // READ: 3 conditions (isactive/period/scope) at date asOf, with scope evaluated by TODAY (§6, invariant #2).
    protected async Task<IReadOnlyList<TVersion>> QueryInScopeAsync(
        DataScope scope, DateOnly asOf, string extraWhere = "", object? param = null)
    {
        EnsureScopeApplicable(scope.Level);

        var filter = _scopeFilter.Build(scope, asOf, _scopeToday.Today, Alias, OrgUnitColumn, OwnerColumn);

        var sql = $"SELECT {BuildSelectClause()} FROM {VersionTable} {Alias} WHERE {filter.WhereSql}";
        if (!string.IsNullOrWhiteSpace(extraWhere))
        {
            sql += $" AND ({extraWhere})";
        }

        var parameters = new DynamicParameters();
        foreach (var (key, value) in filter.Parameters)
        {
            parameters.Add(key, value);
        }
        if (param is not null)
        {
            parameters.AddDynamicParams(param);
        }

        using var connection = _connections.CreateConnection();
        var rows = await connection.QueryAsync<TVersion>(sql, parameters);
        return rows.ToList();
    }

    // Blocks a MEANINGLESS scope-level x entity combination: a scope level needing a column the entity does not have
    // (keeping the "NULL" sentinel) would make StandardScopeFilterBuilder generate an always-false clause (e.g. `NULL = @currentUsername`)
    // -> silently returning empty, hiding a caller/config error. FAILS CLEARLY right away instead of staying silent (the
    // "clear failure over ambiguous behavior" invariant). This is a violation of the calling contract (the
    // upstream authorization layer must ensure the scope level is valid for the target entity), not a normal data
    // result -> THROWS, not ErrorOr.
    protected void EnsureScopeApplicable(ScopeLevel level)
    {
        switch (level)
        {
            case ScopeLevel.Self when OwnerColumn == NoColumn:
                throw new InvalidOperationException(
                    $"Phạm vi Self không áp dụng được cho '{VersionTable}': thực thể không có cột chủ sở hữu.");

            case (ScopeLevel.OwnOrgUnit or ScopeLevel.OwnOrgUnitAndDescendants) when OrgUnitColumn == NoColumn:
                throw new InvalidOperationException(
                    $"Phạm vi {level} không áp dụng được cho '{VersionTable}': thực thể không thuộc đơn vị nào.");
        }
    }

    // [C3] Loads the versions APPLICABLE at `asOf` (isactive + within period — 2 of 3 conditions in §6, NOT
    // filtering by scope) matching business predicate `predicateSql` (e.g. `v.username = @username`). Used for
    // permission-resolution lookups by business value (username/function_key/(role,function)) — where the caller
    // itself checks the UNIQUENESS invariant and fails CLEARLY if >1 (e.g. username §1.3). Returns all so the caller
    // decides, the base does NOT pick one itself, to avoid hiding an invariant violation.
    protected async Task<IReadOnlyList<TVersion>> QueryApplicableAsync(
        string predicateSql, object param, DateOnly asOf)
    {
        var sql = $"SELECT {BuildSelectClause()} FROM {VersionTable} {Alias} " +
                  $"WHERE {Alias}.isactive = 1 AND {Alias}.effective_from <= @asOf AND @asOf <= {Alias}.effective_to " +
                  $"AND ({predicateSql})";

        var parameters = new DynamicParameters(param);
        parameters.Add("asOf", asOf);

        using var connection = _connections.CreateConnection();
        var rows = await connection.QueryAsync<TVersion>(sql, parameters);
        return rows.ToList();
    }

    // Resolves "the usable version at asOf" DIRECTLY from the DB for 1 identity (§3.3: "direct-from-DB
    // resolution is the base repository's job"). No coverage -> Error.NotFound (D9: STOPS + reports clearly).
    protected async Task<ErrorOr<TVersion>> ResolveAtAsync(long identityId, DateOnly asOf)
    {
        using var connection = _connections.CreateConnection();
        var candidates = await LoadActiveVersionsAsync(connection, null, identityId);
        return _resolver.ResolveAt(candidates, asOf);
    }

    // Composite-context overload: same resolution, but reads via context.Connection/Transaction so an
    // in-flight write earlier in the SAME composite is visible under READ COMMITTED (read-your-own-writes —
    // same rationale as RoleRepository.UpsertAsync(ICompositeWriteContext, ...)'s own reads).
    protected async Task<ErrorOr<TVersion>> ResolveAtAsync(ICompositeWriteContext context, long identityId, DateOnly asOf)
    {
        var candidates = await LoadActiveVersionsAsync(context.Connection, context.Transaction, identityId);
        return _resolver.ResolveAt(candidates, asOf);
    }

    // WRITE: 8-case algebra (PlanUpsert) + STRICT temporal-FK + named lock on parent+child (fixed order) +
    // 1 READ COMMITTED transaction (§4/§5/§7). Default: a gap warning (D7) is returned alongside, NOT an Error.
    // When GapIsBlocking is true, any plan gap warning becomes Error.Validation before writes (no partial write).
    protected async Task<ErrorOr<UpsertResult>> UpsertVersionAsync(
        long identityId, Period period, TVersion newValues, string recordedBy, string? reason,
        VersionOperationKind? operationKind = null)
    {
        var parentIds = ExtractParentIdentityIds(newValues);
        var lockKeys = BuildLockKeys(identityId, parentIds);

        return await ExecuteWriteAsync(lockKeys, identityId, async (connection, transaction, activeVersions) =>
            await ApplyUpsertPlanAsync(
                connection, transaction, activeVersions, identityId, period, newValues,
                recordedBy, reason, operationKind));
    }

    // CUT/CLOSE PERIOD (§4 "close an open period", §9.5): shrinks effective_to of active version `versionId` down to `newTo`
    // (newTo < the current To). Mechanism = soft-delete the old version + a remnant [oldFrom, newTo] (keeping the business data,
    // staying append-only). The portion [newTo+1, oldTo] LOSES coverage -> reverse-FK BLOCKS if a dependent child remains (D8).
    // A remnant always remains -> the original version still exists. A gap warning (D7) is returned alongside, it does NOT block.
    // `operationDate`: the caller-captured business date for THIS operation, same contract as
    // CancelVersionAsync's below (design-effective-period.md §3). Required, never defaulted — a
    // correctness-carrying parameter with a default institutionalises the bug it was added to prevent.
    public async Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        long identityId, long versionId, DateOnly newTo, OperationDate operationDate,
        string recordedBy, string? reason)
    {
        if (ValidateClose(operationDate, newTo) is { } closeError)
        {
            return closeError;
        }

        // Pre-read exclusively-owned dependent identities so their §7 lock keys can be acquired
        // UP FRONT with the parent's key (same fixed order as BuildLockKeys). The probed identity
        // set is threaded into AutoCut so a dependent that appears after this probe cannot be cut
        // without its lock (grow-only TOCTOU guard — VersionedRepository.DependentSetChanged).
        List<string> lockKeys;
        IReadOnlySet<(string Table, long IdentityId)> lockedDependents;
        using (var probe = _connections.CreateConnection())
        {
            probe.Open();
            (lockKeys, lockedDependents) = await BuildLockKeysWithDependentProbeAsync(probe, identityId);
        }

        return await ExecuteWriteAsync(lockKeys, identityId, async (connection, transaction, activeVersions) =>
            await CloseVersionCoreAsync(
                connection, transaction, activeVersions, identityId, versionId, newTo, operationDate, recordedBy, reason,
                (table, dependentId) => lockedDependents.Contains((table, dependentId))));
    }

    // Composite-context sibling (Seam-2 gap F1): same validation + P11 auto-cut as the non-context
    // CloseVersionAsync above, run on the composite's SHARED connection/transaction instead of opening
    // its own. No lock acquisition here — every lock this call could need (the identity itself, plus any
    // exclusively-owned dependent this Close might auto-cut) must already have been Enlist-ed up front by
    // the caller, exactly like UpsertVersionAsync(ICompositeWriteContext, …) above. `context.IsEnlisted`
    // stands in for the probed/locked-set argument the non-context path builds via
    // BuildLockKeysWithDependentProbeAsync — the real §7 locks are already held by the composite, so no
    // separate probe connection is needed. `protected internal` (not `protected`, matching
    // AutoCutExclusivelyOwnedAsync below): AST.Modules.IAM.Tests reaches this via InternalsVisibleTo
    // without every repository needing its own composite-Close wrapper (mirrors CloseVersionAsync's own
    // public accessibility — Close/Cancel have a uniform signature across every entity, unlike Upsert).
    protected internal async Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        ICompositeWriteContext context, long identityId, long versionId, DateOnly newTo,
        OperationDate operationDate, string recordedBy, string? reason)
    {
        if (ValidateClose(operationDate, newTo) is { } closeError)
        {
            return closeError;
        }

        var enlistError = EnsureCompositeEnlisted(context, identityId, EmptyParentIds);
        if (enlistError is not null)
        {
            return enlistError.Value;
        }

        var dependentEnlistError = await EnsureCompositeDependentsEnlistedAsync(context, identityId);
        if (dependentEnlistError is not null)
        {
            return dependentEnlistError.Value;
        }

        var activeVersions = await LoadActiveVersionsAsync(context.Connection, context.Transaction, identityId);
        return await CloseVersionCoreAsync(
            context.Connection, context.Transaction, activeVersions, identityId, versionId, newTo, operationDate, recordedBy, reason,
            context.IsEnlisted);
    }

    // Entity-specific close-date policy, applied by BOTH close paths above before anything is written.
    // Default = no extra policy, so every existing repository keeps its current behaviour unchanged.
    //
    // This is a HOOK, not a convention a subclass is trusted to follow: an override cannot be bypassed
    // by the static type of the reference the caller holds. The alternative a subclass reaches for —
    // declaring `public new CloseVersionAsync(...)` and guarding there — only intercepts callers whose
    // static type is the subclass; anyone holding a `VersionedRepository<T>` reference silently gets
    // the unguarded base, with no compile error and no failing test. For an authorization-bearing
    // invariant (IAM's `Immediate` rule: a role/grant stops today, never on a scheduled future date)
    // that is not a guard. Do NOT reintroduce method hiding here — override this instead.
    protected virtual Error? ValidateClose(OperationDate operationDate, DateOnly newTo) => null;

    // Entity-specific UPSERT policy that needs to read the database, applied by BOTH upsert paths
    // before anything is planned or written. Default = no extra policy, so every existing repository
    // keeps its current behaviour unchanged. Sibling of ValidateClose above, and the same reasoning
    // against method hiding applies.
    //
    // WHERE it is called from is the whole point: `ApplyUpsertPlanAsync` is the only place the two
    // writers converge while the identity's named lock is already held — the plain writer arrives
    // inside `ExecuteWriteAsync`'s callback (locks acquired first, then the transaction), the
    // composite writer arrives directly on a context whose locks `CompositeWrite` took up front. A
    // hook on `ExecuteWriteAsync` would therefore guard the plain writer only and silently skip the
    // composite one. An override may consequently rely on being serialised against other cooperating
    // writers of the same identity (it is NOT protected against direct SQL).
    //
    // The parameter list is deliberately narrow: the ambient connection + transaction (so an override
    // sees this operation's own uncommitted rows) and the identity id. It does NOT receive the active
    // version list — that list is `isactive = 1` only, and an override that mistook it for the
    // identity's HISTORY would wave through an identity whose every row is inactive or cancelled.
    protected virtual Task<Error?> ValidateUpsertAsync(
        IDbConnection connection, IDbTransaction transaction, long identityId) =>
        Task.FromResult<Error?>(null);

    private static readonly IReadOnlyDictionary<string, long> EmptyParentIds = new Dictionary<string, long>();

    private async Task<ErrorOr<UpsertResult>> CloseVersionCoreAsync(
        IDbConnection connection, IDbTransaction transaction, List<TVersion> activeVersions,
        long identityId, long versionId, DateOnly newTo, OperationDate operationDate, string recordedBy, string? reason,
        Func<string, long, bool> isDependentLocked)
    {
        var target = activeVersions.FirstOrDefault(v => v.Id == versionId);
        if (target is null)
        {
            return Error.NotFound(
                "VersionedRepository.VersionNotFound",
                $"Không tìm thấy phiên bản active id={versionId} của '{VersionTable}'.");
        }

        if (newTo < target.EffectiveFrom || newTo >= target.EffectiveTo)
        {
            return Error.Validation(
                "VersionedRepository.InvalidShrink",
                $"Ngày cắt {newTo.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} phải trong [{target.EffectiveFrom.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}, {target.EffectiveTo.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}) của phiên bản.");
        }

        var remaining = activeVersions
            .Where(v => v.Id != versionId)
            .Select(v => new Period(v.EffectiveFrom, v.EffectiveTo))
            .ToList();
        remaining.Add(new Period(target.EffectiveFrom, newTo));

        // P11: cut exclusively-owned dependents FIRST so ValidateParentChange does not BLOCK them.
        var autoCut = await AutoCutExclusivelyOwnedAsync(
            connection, transaction, identityId, remaining, operationDate.Value, recordedBy, reason, isDependentLocked);
        if (autoCut.IsError)
        {
            return autoCut.Errors;
        }

        // Must run on the SAME connection/transaction as AutoCut — passing `transaction` as the
        // ambient transaction makes ITemporalFkValidator see this transaction's own uncommitted
        // writes (the just-cut dependents) instead of a separate connection's stale committed snapshot.
        var reverseFk = _fkValidator.ValidateParentChange(VersionTable, identityId, remaining, transaction);
        if (reverseFk.IsError)
        {
            return reverseFk.Errors;
        }

        await connection.ExecuteAsync(
            $"UPDATE {VersionTable} SET isactive = 0 WHERE id = @id", new { id = versionId }, transaction);
        var remnantId = await InsertRemnantAsync(
            connection, transaction, versionId, new Period(target.EffectiveFrom, newTo), recordedBy, reason,
            VersionOperationKind.Close);

        return new UpsertResult(remnantId, ComputeGapWarnings(remaining), autoCut.Value);
    }

    // DELETE 1 PERIOD (§9.5): soft-deletes exactly the active version `versionId`. THE ORIGINAL VERSION MUST STILL EXIST -> blocked if
    // it is the ONLY active version. Reverse-FK BLOCKS if a child would lose coverage. A gap warning is returned alongside.
    // (Who deleted it / why -> written to audit_log at the app layer, not the base repository's responsibility.)
    public async Task<ErrorOr<UpsertResult>> DeleteVersionAsync(long identityId, long versionId)
    {
        return await ExecuteWriteAsync([LockKey(VersionTable, identityId)], identityId, async (connection, transaction, activeVersions) =>
        {
            var target = activeVersions.FirstOrDefault(v => v.Id == versionId);
            if (target is null)
            {
                return Error.NotFound(
                    "VersionedRepository.VersionNotFound",
                    $"Không tìm thấy phiên bản active id={versionId} của '{VersionTable}'.");
            }

            if (activeVersions.Count <= 1)
            {
                return Error.Validation(
                    "VersionedRepository.BaseVersionRequired",
                    $"Không thể xóa phiên bản active cuối cùng của '{VersionTable}' (bản gốc bắt buộc tồn tại).");
            }

            var remaining = activeVersions
                .Where(v => v.Id != versionId)
                .Select(v => new Period(v.EffectiveFrom, v.EffectiveTo))
                .ToList();

            var reverseFk = _fkValidator.ValidateParentChange(VersionTable, identityId, remaining, transaction);
            if (reverseFk.IsError)
            {
                return reverseFk.Errors;
            }

            await connection.ExecuteAsync(
                $"UPDATE {VersionTable} SET isactive = 0 WHERE id = @id", new { id = versionId }, transaction);

            // DeleteVersionAsync never runs P11 auto-cut (only Close/Cancel do) -- no outcomes to report.
            return new UpsertResult(0, ComputeGapWarnings(remaining), []);
        });
    }

    // CANCEL a version that has NOT completed a single effective day (N6): the target must be an isactive=1
    // version whose EffectiveFrom is >= business "today" — i.e. a still-pending future plan OR one that only
    // starts today (requester decision D1, 2026-08-10: such a version "never really counted"). Sets
    // isactive=0 AND cancelled=1 — distinct from CloseVersionAsync, which retires a version that has already
    // been effective for at least one full day by shrinking its coverage.
    //
    // Predecessor-coverage restore: creating a future plan commonly OVERLAP-CUTS
    // a predecessor version down to a shorter tail (PeriodEditor.PlanUpsert case 4 — the normal outcome under
    // GapIsBlocking=true, since gap=BLOCK forces the new plan to overlap rather than leave a gap). If cancelling the
    // plan only deactivated the target, the identity's coverage would stay PERMANENTLY TRUNCATED at the cut point —
    // nobody asked for that shrink. So: find the immediately-adjacent predecessor (EffectiveTo == target.EffectiveFrom
    // - 1 day) among the identity's active versions and restore its ORIGINAL coverage back to the target's EffectiveTo
    // via the same soft-deactivate + InsertRemnantAsync mechanism CloseVersionAsync uses (business columns carry over
    // automatically).
    //
    // Reverse-FK (D8, fixed 2026-08-05): the restore branch (a predecessor was found) is
    // COVERAGE-NEUTRAL — it only extends coverage back to what it was before this plan existed, never reduces it,
    // so ValidateParentChange is a cheap no-op there. But when there is NO adjacent predecessor (e.g. the target is
    // an identity's ONLY version — an identity whose plan is being abandoned before it completed an effective day),
    // cancelling it REDUCES the identity's coverage to nothing over that period, exactly like DeleteVersionAsync —
    // and a dependent child (declared via ITemporalFkRegistry, e.g. org_unit_version.parent_id self-edge or
    // user_version.org_unit_id) could be silently stranded. The check below is called UNCONDITIONALLY (covers both
    // branches uniformly) rather than only in the no-predecessor branch, so a future code change to the restore
    // logic can't accidentally reopen this gap.
    //
    // KNOWN LIMITATION (accepted, requester decision 2026-07-22): the adjacency check cannot distinguish "predecessor
    // was mechanically cut by creating this exact plan" from "predecessor was deliberately closed earlier via
    // CloseVersionAsync (which does NOT enforce gap=BLOCK) and this plan was later added, unrelated, to start the very
    // next day" — both look identical in the data (no lineage column exists). The restore-on-cancel therefore also
    // re-extends the former case. Accepted because the plan/spec never describes that two-step scenario, the Phase
    // 3-4 UI that could make it common doesn't exist yet, and adding a lineage/tracking column to solve it precisely
    // is out of scope for this fix. Do NOT add a lineage/tracking column to "fix" this — it is a deliberate trade-off,
    // not an oversight.
    // `operationDate` (TASK 0, 2026-08-11): the caller-captured "today" for THIS operation
    // (design-effective-period.md §3 — captured ONCE by the caller, e.g.
    // RoleDeclarationService.CloseRoleDeclarationAsync's `today`, and threaded through unchanged). The
    // engine no longer re-reads its own injected IBusinessDateProvider for this guard — see
    // CancelVersionCoreAsync below.
    protected async Task<ErrorOr<UpsertResult>> CancelVersionAsync(
        long identityId, long versionId, DateOnly operationDate, string recordedBy, string reason)
    {
        if (!SupportsCancellation)
        {
            throw new InvalidOperationException(
                $"'{VersionTable}' does not support cancellation (SupportsCancellation is false).");
        }

        // Same probe as CloseVersionAsync (Seam-2 gap F3): P11 auto-cut now also runs on Cancel, so the
        // dependent identities it might touch need their §7 lock keys up front too, with the same
        // grow-only TOCTOU guard.
        List<string> lockKeys;
        IReadOnlySet<(string Table, long IdentityId)> lockedDependents;
        using (var probe = _connections.CreateConnection())
        {
            probe.Open();
            (lockKeys, lockedDependents) = await BuildLockKeysWithDependentProbeAsync(probe, identityId);
        }

        return await ExecuteWriteAsync(lockKeys, identityId, async (connection, transaction, activeVersions) =>
            await CancelVersionCoreAsync(
                connection, transaction, activeVersions, identityId, versionId, operationDate, recordedBy, reason,
                (table, dependentId) => lockedDependents.Contains((table, dependentId))));
    }

    // Composite-context sibling (Seam-2 gap F1), same shape/rationale as CloseVersionAsync's composite
    // overload above: runs on the composite's shared connection/transaction, no lock acquisition of its
    // own — the identity (and any dependent this Cancel might auto-cut, F3) must already be Enlist-ed.
    protected internal async Task<ErrorOr<UpsertResult>> CancelVersionAsync(
        ICompositeWriteContext context, long identityId, long versionId, DateOnly operationDate,
        string recordedBy, string reason)
    {
        if (!SupportsCancellation)
        {
            throw new InvalidOperationException(
                $"'{VersionTable}' does not support cancellation (SupportsCancellation is false).");
        }

        var enlistError = EnsureCompositeEnlisted(context, identityId, EmptyParentIds);
        if (enlistError is not null)
        {
            return enlistError.Value;
        }

        var dependentEnlistError = await EnsureCompositeDependentsEnlistedAsync(context, identityId);
        if (dependentEnlistError is not null)
        {
            return dependentEnlistError.Value;
        }

        var activeVersions = await LoadActiveVersionsAsync(context.Connection, context.Transaction, identityId);
        return await CancelVersionCoreAsync(
            context.Connection, context.Transaction, activeVersions, identityId, versionId, operationDate,
            recordedBy, reason, context.IsEnlisted);
    }

    private async Task<ErrorOr<UpsertResult>> CancelVersionCoreAsync(
        IDbConnection connection, IDbTransaction transaction, List<TVersion> activeVersions,
        long identityId, long versionId, DateOnly operationDate, string recordedBy, string reason,
        Func<string, long, bool> isDependentLocked)
    {
        var target = activeVersions.FirstOrDefault(v => v.Id == versionId);
        if (target is null)
        {
            return Error.NotFound(
                "VersionedRepository.VersionNotFound",
                $"Không tìm thấy phiên bản active id={versionId} của '{VersionTable}'.");
        }

        // Requester decision D1 (2026-08-10): the boundary is "has NOT completed a single effective day",
        // not "has never been effective" — a version whose coverage STARTS TODAY never really counted, so it
        // is cancellable ("Bị hủy"); one that started yesterday or earlier already produced a full day of
        // business coverage and can only be retired via CloseVersionAsync. The error code is unchanged
        // (`NotAFuturePlan`) — screens/tests already map it.
        //
        // TASK 0 (2026-08-11): this guard used to re-read the engine's OWN injected
        // IBusinessDateProvider (`_scopeToday.Today`) here — a SECOND, independent read of "today" for the
        // same operation the caller (RoleDeclarationService/OrgUnitDeclarationService) had already read
        // once to pick this very Retire-vs-CancelPlan branch. Across a midnight rollover the two reads
        // could disagree, wrongly BLOCKing a cancel the caller legitimately routed here
        // (design-effective-period.md §3: "a single business operation captures D ... ONCE"). Routed
        // through VersionCloseRules.BranchFor (the single home of the D1 `>=` boundary, AST.Core) instead
        // of a private `<` comparison, using the CALLER-SUPPLIED `operationDate` — removes the last
        // private copy of this boundary and the double-read at once.
        if (VersionCloseRules.BranchFor(operationDate, new Period(target.EffectiveFrom, target.EffectiveTo)) != VersionCloseBranch.CancelPlan)
        {
            return Error.Validation(
                "VersionedRepository.NotAFuturePlan",
                $"Phiên bản id={versionId} đã có hiệu lực trọn ít nhất 1 ngày; chỉ hủy được phiên bản " +
                "bắt đầu từ hôm nay trở đi (chưa có ngày hiệu lực nào hoàn tất).");
        }

        var predecessor = activeVersions.FirstOrDefault(
            v => v.Id != versionId && v.EffectiveTo == target.EffectiveFrom.AddDays(-1));

        var remaining = activeVersions
            .Where(v => v.Id != versionId && (predecessor is null || v.Id != predecessor.Id))
            .Select(v => new Period(v.EffectiveFrom, v.EffectiveTo))
            .ToList();
        if (predecessor is not null)
        {
            remaining.Add(new Period(predecessor.EffectiveFrom, target.EffectiveTo));
        }

        // P11 (F3): auto-cut exclusively-owned dependents FIRST, same shape/order as CloseVersionAsync.
        // AutoCutExclusivelyOwnedAsync derives its own cut point PER DEPENDENT from `remaining`'s actual
        // coverage gap (see that method's comment) — no cutTo is computed here:
        //  - predecessor found: coverage is fully RESTORED through target.EffectiveTo (see the class
        //    comment above on CancelVersionAsync) — `remaining` already covers every dependent that was
        //    covered before, so AutoCut's own CoverageGap check finds no gap for any of them and this is
        //    a genuine no-op.
        //  - no predecessor: the identity loses ALL coverage over [target.EffectiveFrom, target.EffectiveTo]
        //    (and `remaining` may be empty, or bounded by some OTHER, unrelated active period on this
        //    identity). A dependent whose OWN start is not covered by `remaining` cannot be gracefully
        //    shrunk, and B1 (2026-08-15) decides what happens then — see AutoCutExclusivelyOwnedAsync's
        //    own shrink-window branch: on an edge declaring `DependentSupportsCancellation` the
        //    dependent is CANCELLED with its parent if it never completed an effective day, and the
        //    cancel SUCCEEDS (`CancelRole_SameDayRoleWithSameDayGrant_CancelsGrantWithRole`); otherwise
        //    AutoCut fails with the same InvalidShrink/BaseVersionRequired shape CloseVersionAsync has
        //    and rolls the whole cancel back
        //    (`CancelRole_GrantInForceInsideCoverageGap_StillFailsBaseVersionRequired`).
        var autoCut = await AutoCutExclusivelyOwnedAsync(
            connection, transaction, identityId, remaining, operationDate, recordedBy, reason, isDependentLocked);
        if (autoCut.IsError)
        {
            return autoCut.Errors;
        }

        var reverseFk = _fkValidator.ValidateParentChange(VersionTable, identityId, remaining, transaction);
        if (reverseFk.IsError)
        {
            return reverseFk.Errors;
        }

        await connection.ExecuteAsync(
            $"UPDATE {VersionTable} SET isactive = 0, cancelled = 1 WHERE id = @id", new { id = versionId }, transaction);

        if (predecessor is not null)
        {
            await connection.ExecuteAsync(
                $"UPDATE {VersionTable} SET isactive = 0 WHERE id = @id", new { id = predecessor.Id }, transaction);
            await InsertRemnantAsync(
                connection, transaction, predecessor.Id, new Period(predecessor.EffectiveFrom, target.EffectiveTo),
                recordedBy, reason, VersionOperationKind.Cancel);
        }

        return new UpsertResult(0, ComputeGapWarnings(remaining), autoCut.Value);
    }

    // Locks (fixed order) + READ COMMITTED transaction + loads active versions + runs `work`; commits if
    // `work` does NOT error, rolls back on error/exception; releases locks in reverse order. Shared by Upsert + cut/delete
    // period (§7). `work` does NOT commit/rollback itself — it only returns ErrorOr.
    private async Task<ErrorOr<UpsertResult>> ExecuteWriteAsync(
        IReadOnlyList<string> lockKeys,
        long identityId,
        Func<IDbConnection, IDbTransaction, List<TVersion>, Task<ErrorOr<UpsertResult>>> work)
    {
        using var connection = _connections.CreateConnection();
        connection.Open();

        var acquired = new List<string>();
        try
        {
            foreach (var key in lockKeys)
            {
                var got = await connection.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 10)", new { name = key });
                if (got != 1)
                {
                    return Error.Failure(
                        "VersionedRepository.LockTimeout",
                        $"Không lấy được khóa ghi cho '{key}' (timeout hoặc lỗi).");
                }
                acquired.Add(key);
            }

            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                var activeVersions = await LoadActiveVersionsAsync(connection, transaction, identityId);
                var result = await work(connection, transaction, activeVersions);
                if (result.IsError)
                {
                    transaction.Rollback();
                    return result.Errors;
                }

                transaction.Commit();
                return result.Value;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                await connection.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = acquired[i] });
            }
        }
    }

    // Gap warning (D7): after reducing coverage, a day gap between 2 adjacent active periods (sorted by From;
    // active periods do NOT overlap, per D6).
    private static IReadOnlyList<GapWarning> ComputeGapWarnings(IReadOnlyList<Period> coverage)
    {
        var sorted = coverage.OrderBy(p => p.From).ToList();
        var warnings = new List<GapWarning>();
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            if (EffectivePeriod.GapBetween(sorted[i].To, sorted[i + 1].From) is { } gap)
            {
                warnings.Add(gap);
            }
        }

        return warnings;
    }

    // [C2] Creates an empty IDENTITY record (header table with only an AUTO_INCREMENT id) -> returns the new id. The
    // header table name is derived from VersionTable (dropping the "_version" suffix), backtick-quoted because `function`/`user`
    // are MySQL keywords.
    //
    // ON ITS OWN CONNECTION, so a header minted here and the first version written afterwards do NOT commit
    // together: if that write fails, this header survives with zero versions. design-effective-period.md §7
    // forbids that ordering. NO PRODUCTION CALLER REMAINS as of 2026-08-17 (backlog 0.4b closed: `function`,
    // then `org_unit`) — the surviving callers are integration-test fixtures, which seed headers directly.
    // New code takes the context overload below instead; AST.Meta.Tests' *WritePathAbsenceTests keep the
    // migrated write paths from reaching back for this one.
    protected async Task<long> InsertIdentityAsync()
    {
        var header = VersionTable[..^"_version".Length];
        using var connection = _connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            $"INSERT INTO `{header}` () VALUES (); SELECT LAST_INSERT_ID();");
    }

    // §7: mints the identity INSIDE the composite transaction, on its connection, so the header and the
    // first version commit or roll back TOGETHER — a failure leaves no row at all, and nothing needs
    // compensating. The new id takes no named lock of its own (nothing committed can name it until commit);
    // it is marked created-here so UpsertVersionAsync's enlistment check accepts it, as child or as
    // temporal-FK parent, without any lock being acquired after the transaction opened.
    protected async Task<long> InsertIdentityAsync(ICompositeWriteContext context)
    {
        // Only AST.Infrastructure's own context implements the sink. A foreign ICompositeWriteContext could
        // not carry the mark, so the mint would silently produce an id no write could then use — fail clearly
        // instead (clear failure over silent ambiguity).
        if (context is not ICompositeCreatedIdentityRegistry registry)
        {
            throw new InvalidOperationException(
                $"Không thể tạo định danh mới trong giao dịch: {context.GetType().Name} không phải ngữ cảnh ghi tổ hợp chuẩn.");
        }

        var header = VersionTable[..^"_version".Length];
        var id = await context.Connection.ExecuteScalarAsync<long>(
            $"INSERT INTO `{header}` () VALUES (); SELECT LAST_INSERT_ID();",
            transaction: context.Transaction);

        registry.MarkCreated(VersionTable, id);
        return id;
    }

    private async Task<List<TVersion>> LoadActiveVersionsAsync(IDbConnection connection, IDbTransaction? transaction, long identityId)
    {
        var sql = $"SELECT {BuildSelectClause()} FROM {VersionTable} {Alias} " +
                  $"WHERE {Alias}.{IdentityColumn} = @identityId AND {Alias}.isactive = 1";
        var rows = await connection.QueryAsync<TVersion>(sql, new { identityId }, transaction);
        return rows.ToList();
    }

    private async Task<long> InsertNewAsync(
        IDbConnection connection, IDbTransaction transaction, long identityId, Period period,
        TVersion newValues, string recordedBy, string? reason, VersionOperationKind? operationKind)
    {
        var columnsCsv = string.Join(", ", BusinessColumns.Select(c => c.Column));
        var placeholdersCsv = string.Join(", ", BusinessColumns.Select(c => $"@{c.Column}"));
        var operationKindColumn = RecordsOperationKind ? ", operation_kind" : "";
        var operationKindPlaceholder = RecordsOperationKind ? ", @operationKind" : "";

        var sql = $"""
            INSERT INTO {VersionTable}
                ({IdentityColumn}, {columnsCsv}, effective_from, effective_to, isactive, recorded_by, reason{operationKindColumn})
            VALUES
                (@identityId, {placeholdersCsv}, @from, @to, 1, @recordedBy, @reason{operationKindPlaceholder});
            SELECT LAST_INSERT_ID();
            """;

        var parameters = new DynamicParameters();
        parameters.Add("identityId", identityId);
        parameters.Add("from", period.From);
        parameters.Add("to", period.To);
        parameters.Add("recordedBy", recordedBy);
        parameters.Add("reason", reason);
        if (RecordsOperationKind)
        {
            parameters.Add("operationKind", operationKind?.ToString());
        }
        foreach (var (column, property) in BusinessColumns)
        {
            parameters.Add(column, GetBusinessValue(newValues, property));
        }

        return await connection.ExecuteScalarAsync<long>(sql, parameters, transaction);
    }

    // Remnant: COPIES the business columns verbatim from the source version via SQL (INSERT ... SELECT) — only the period +
    // new id + isactive=1 + audit change. Does not go through DTO/reflection => columns cannot be missed/mismatched.
    private async Task<long> InsertRemnantAsync(
        IDbConnection connection, IDbTransaction transaction, long sourceVersionId, Period period,
        string recordedBy, string? reason, VersionOperationKind? operationKind = null)
    {
        var columnsCsv = string.Join(", ", BusinessColumns.Select(c => c.Column));
        var operationKindColumn = RecordsOperationKind ? ", operation_kind" : "";
        var operationKindValue = RecordsOperationKind ? ", @operationKind" : "";
        var sql = $"""
            INSERT INTO {VersionTable}
                ({IdentityColumn}, {columnsCsv}, effective_from, effective_to, isactive, recorded_by, reason{operationKindColumn})
            SELECT {IdentityColumn}, {columnsCsv}, @from, @to, 1, @recordedBy, @reason{operationKindValue}
            FROM {VersionTable} WHERE id = @sourceVersionId;
            SELECT LAST_INSERT_ID();
            """;

        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            from = period.From,
            to = period.To,
            recordedBy,
            reason,
            sourceVersionId,
            operationKind = operationKind?.ToString(),
        }, transaction);
    }

    private static object? GetBusinessValue(TVersion entity, string propertyName)
    {
        var property = typeof(TVersion).GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"'{typeof(TVersion).Name}' thiếu property '{propertyName}' khai trong BusinessColumns.");
        return property.GetValue(entity);
    }

    private List<string> BuildLockKeys(long identityId, IReadOnlyDictionary<string, long> parentIds)
    {
        var keys = new List<(string Table, long Id)> { (VersionTable, identityId) };

        var edges = _fkRegistry.EdgesForChild(VersionTable);
        foreach (var (column, parentId) in parentIds)
        {
            var edge = edges.FirstOrDefault(e => e.ChildParentColumn == column);
            if (edge is not null && !keys.Contains((edge.ParentVersionTable, parentId)))
            {
                keys.Add((edge.ParentVersionTable, parentId));
            }
        }

        // Fixed order (§7): sorted by table name then id -> every caller uses the same order -> avoids deadlock.
        return keys
            .OrderBy(k => k.Table, StringComparer.Ordinal)
            .ThenBy(k => k.Id)
            .Select(k => VersionedRepositoryLockKeys.Format(k.Table, k.Id))
            .ToList();
    }

    // Named-lock key for 1 (version table, identity) — a single format, every caller follows the same convention.
    private static string LockKey(string versionTable, long identityId) =>
        VersionedRepositoryLockKeys.Format(versionTable, identityId);

    private ErrorOr<UpsertResult>? EnsureCompositeEnlisted(
        ICompositeWriteContext context,
        long identityId,
        IReadOnlyDictionary<string, long> parentIds)
    {
        // An identity minted inside this transaction has no lock and needs none (§7 carve-out). Provenance is
        // read off the INTERNAL registry, never off `context` itself: a foreign ICompositeWriteContext must
        // not be able to claim it and buy an unlocked write. Null when the context is not ours — then the
        // carve-out simply does not apply, and only Enlist-ment can authorise the write.
        var createdHere = context as ICompositeCreatedIdentityRegistry;

        // Table-qualified, so a new id here never vouches for a same-numbered identity of another table that
        // genuinely had to be locked up front.
        if (!context.IsEnlisted(VersionTable, identityId)
            && createdHere?.IsCreatedHere(VersionTable, identityId) != true)
        {
            return Error.Failure(
                "CompositeWrite.NotEnlisted",
                $"Identity {identityId} of '{VersionTable}' was not Enlist-ed before CompositeWrite.ExecuteAsync — " +
                "late lock acquisition is forbidden (§7 / all-locks-up-front).");
        }

        var edges = _fkRegistry.EdgesForChild(VersionTable);
        foreach (var (column, parentId) in parentIds)
        {
            var edge = edges.FirstOrDefault(e => e.ChildParentColumn == column);
            if (edge is null)
            {
                continue;
            }

            // Same carve-out on the parent side: a parent minted inside this transaction (a brand-new role a
            // grant is being attached to) has no lock either. Table-qualified for the same reason as above.
            if (!context.IsEnlisted(edge.ParentVersionTable, parentId)
                && createdHere?.IsCreatedHere(edge.ParentVersionTable, parentId) != true)
            {
                return Error.Failure(
                    "CompositeWrite.NotEnlisted",
                    $"Temporal-FK parent {parentId} of '{edge.ParentVersionTable}' was not Enlist-ed before " +
                    "CompositeWrite.ExecuteAsync — late lock acquisition is forbidden (§7 / all-locks-up-front).");
            }
        }

        return null;
    }

    // F-2 fix: pre-checks that every exclusively-owned dependent
    // CURRENTLY on record for `identityId` is already Enlist-ed, BEFORE the composite Close/Cancel
    // core runs its P11 auto-cut. Without this, a caller who Enlist-ed the parent identity but forgot
    // to also Enlist an exclusively-owned dependent hit AutoCutExclusivelyOwnedAsync's grow-only TOCTOU
    // guard and got back "VersionedRepository.DependentSetChanged" — a message telling them to retry a
    // deterministic, never-succeeding programming mistake, indistinguishable from a genuine
    // concurrency race. This check runs on the SAME composite connection/transaction (so it sees the
    // composite's own in-flight writes) and returns a DISTINCT, non-retryable error code —
    // "DependentSetChanged" stays reserved for the one case this pre-check cannot close: a dependent
    // appearing between THIS SELECT and AutoCut's own later in-transaction SELECT.
    // Only EnsureCompositeEnlisted's identity+FK-parent check is unconditional (every UpsertVersionAsync
    // context overload needs it); this dependent check only applies to Close/Cancel, since only they run
    // P11 auto-cut.
    private async Task<ErrorOr<UpsertResult>?> EnsureCompositeDependentsEnlistedAsync(
        ICompositeWriteContext context, long identityId)
    {
        foreach (var dep in ExclusivelyOwnedDependents)
        {
            var dependentIds = await context.Connection.QueryAsync<long>(
                $"SELECT DISTINCT {dep.DependentIdentityColumn} FROM {dep.DependentVersionTable} " +
                $"WHERE {dep.DependentParentColumn} = @id AND isactive = 1",
                new { id = identityId }, context.Transaction);

            foreach (var dependentId in dependentIds)
            {
                if (!context.IsEnlisted(dep.DependentVersionTable, dependentId))
                {
                    return Error.Failure(
                        "VersionedRepository.DependentNotEnlisted",
                        $"Exclusively-owned dependent {dependentId} of '{dep.DependentVersionTable}' " +
                        $"(owned by '{VersionTable}' identity {identityId}) was not Enlist-ed before " +
                        "CompositeWrite.ExecuteAsync — enlist every dependent this Close/Cancel might auto-cut.");
                }
            }
        }

        return null;
    }

    private async Task<ErrorOr<UpsertResult>> ApplyUpsertPlanAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        List<TVersion> activeVersions,
        long identityId,
        Period period,
        TVersion newValues,
        string recordedBy,
        string? reason,
        VersionOperationKind? operationKind)
    {
        // FIRST, before any planning or temporal-FK work: an entity that rejects this upsert outright
        // must say so in its own terms. Running the algebra or the FK check first would let an
        // identity-level rejection surface as an algebra or `TemporalFk.ParentGap` error instead.
        var policyError = await ValidateUpsertAsync(connection, transaction, identityId);
        if (policyError is not null)
        {
            return policyError.Value;
        }

        var parentIds = ExtractParentIdentityIds(newValues);

        var planResult = _periodEditor.PlanUpsert(activeVersions.Cast<IVersionRow>().ToList(), period);
        if (planResult.IsError)
        {
            return planResult.Errors;
        }

        var fkResult = _fkValidator.ValidateChildCoverage(VersionTable, parentIds, period, transaction);
        if (fkResult.IsError)
        {
            return fkResult.Errors;
        }

        // Upsert (8-case algebra) NEVER reduces an identity's coverage (overlapping version -> head/tail remnant +
        // inserting the new period) -> cannot make a child lose coverage -> reverse-FK is NOT needed here. Reverse-FK is only
        // attached to operations that REDUCE coverage (CloseVersionAsync / DeleteVersionAsync).
        var plan = planResult.Value;
        if (GapIsBlocking && plan.Warnings.Count > 0)
        {
            var gap = plan.Warnings[0];
            return Error.Validation(
                GapBlockErrorCode,
                $"A date gap [{gap.GapFrom:yyyy-MM-dd}, {gap.GapTo:yyyy-MM-dd}] is not allowed for '{VersionTable}'.");
        }

        long newVersionId = 0;

        foreach (var op in plan.Operations)
        {
            if (op.Kind == VersionOpKind.SoftDeactivate)
            {
                await connection.ExecuteAsync(
                    $"UPDATE {VersionTable} SET isactive = 0 WHERE id = @id",
                    new { id = op.ExistingVersionId }, transaction);
            }
            else if (op.CarriesOldBusinessData)
            {
                await InsertRemnantAsync(
                    connection, transaction, op.SourceVersionId!.Value, op.Period, recordedBy, reason, operationKind);
            }
            else
            {
                newVersionId = await InsertNewAsync(
                    connection, transaction, identityId, op.Period, newValues, recordedBy, reason, operationKind);
            }
        }

        // Upsert never runs P11 auto-cut (only Close/Cancel narrow a parent's coverage) -- no outcomes.
        return new UpsertResult(newVersionId, plan.Warnings, []);
    }

    // Shared by CloseVersionAsync and CancelVersionAsync (F3): probes which exclusively-owned dependent
    // identities currently exist for `identityId` so their §7 lock keys can be acquired UP FRONT
    // alongside the parent's own key, before either operation's write transaction begins.
    private async Task<(List<string> LockKeys, IReadOnlySet<(string Table, long IdentityId)> LockedDependents)>
        BuildLockKeysWithDependentProbeAsync(IDbConnection connection, long identityId)
    {
        var keys = new List<(string Table, long Id)> { (VersionTable, identityId) };
        var lockedDependents = new HashSet<(string Table, long IdentityId)>();

        foreach (var dep in ExclusivelyOwnedDependents)
        {
            var dependentIds = await connection.QueryAsync<long>(
                $"SELECT DISTINCT {dep.DependentIdentityColumn} FROM {dep.DependentVersionTable} " +
                $"WHERE {dep.DependentParentColumn} = @id AND isactive = 1",
                new { id = identityId });

            foreach (var dependentId in dependentIds)
            {
                lockedDependents.Add((dep.DependentVersionTable, dependentId));
                var key = (dep.DependentVersionTable, dependentId);
                if (!keys.Contains(key))
                {
                    keys.Add(key);
                }
            }
        }

        var lockKeys = keys
            .OrderBy(k => k.Table, StringComparer.Ordinal)
            .ThenBy(k => k.Id)
            .Select(k => VersionedRepositoryLockKeys.Format(k.Table, k.Id))
            .ToList();
        return (lockKeys, lockedDependents);
    }

    // internal (not private): IAM.Tests reaches this via InternalsVisibleTo to pin the grow-only
    // locked-set vs in-transaction read guard without a flaky 2-connection race on CloseVersionAsync.
    // Kept as the HashSet-based overload existing tests already pin; delegates to the Func-based core
    // below so a composite-context caller can pass `context.IsEnlisted` directly instead of materializing
    // a HashSet first (no separate probe connection exists in the composite path — F1/F3).
    internal Task<ErrorOr<IReadOnlyList<AutoCutOutcome>>> AutoCutExclusivelyOwnedAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long parentIdentityId,
        IReadOnlyList<Period> remainingParentCoverage,
        DateOnly operationDate,
        string recordedBy,
        string? reason,
        IReadOnlySet<(string Table, long IdentityId)> lockedDependents) =>
        AutoCutExclusivelyOwnedAsync(
            connection, transaction, parentIdentityId, remainingParentCoverage, operationDate, recordedBy, reason,
            (table, dependentId) => lockedDependents.Contains((table, dependentId)));

    // The cut point is DERIVED PER DEPENDENT from CoverageGap.TryFind's own `gap` (the first uncovered
    // day), not accepted as a single caller-supplied date — Seam-2 gap F3 (extending this to
    // CancelVersionAsync) exposed that a single fixed cut point does not generalize: CancelVersionAsync's
    // `remaining` is not always bounded by one clean date the way CloseVersionAsync's `newTo` is (Cancel's
    // `remaining` can include OTHER, unrelated active periods on the same identity, or be empty). For the
    // already-pinned CloseVersionAsync path this is behaviour-preserving NOT because `remaining` happens to
    // be bounded at `newTo` (it isn't — `remaining` there is every other active version of the identity
    // PLUS [target.EffectiveFrom, newTo]), but because an active exclusively-owned dependent can never span
    // a hole in its parent's coverage in the first place: ValidateChildCoverage blocks that on every write,
    // and every coverage-reducing path (this auto-cut, Delete/Cancel's reverse-FK check) blocks or cuts. So
    // the only gap CoverageGap.TryFind can find for a dependent that was legally written is the one CLOSE
    // just introduced at `newTo`, making the derived cut point equal `newTo` in practice (verified against
    // `CloseRole_ExclusivelyOwnedGrant_IsAutoCutInSameTransaction_WithAuditRow`).
    private async Task<ErrorOr<IReadOnlyList<AutoCutOutcome>>> AutoCutExclusivelyOwnedAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long parentIdentityId,
        IReadOnlyList<Period> remainingParentCoverage,
        DateOnly operationDate,
        string recordedBy,
        string? reason,
        Func<string, long, bool> isDependentLocked)
    {
        var outcomes = new List<AutoCutOutcome>();

        foreach (var dep in ExclusivelyOwnedDependents)
        {
            var rows = (await connection.QueryAsync<(ulong Id, ulong IdentityId, DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
                $"""
                SELECT id AS Id,
                       {dep.DependentIdentityColumn} AS IdentityId,
                       effective_from AS EffectiveFrom,
                       effective_to AS EffectiveTo
                FROM {dep.DependentVersionTable}
                WHERE {dep.DependentParentColumn} = @parentId AND isactive = 1
                """,
                new { parentId = parentIdentityId },
                transaction)).ToList();

            // Grow-only: every identity the in-transaction read found must already be in the
            // probed/locked set. A new dependent that appeared after the probe must NOT be cut
            // without its §7 lock — fail clear and let the caller retry.
            foreach (var row in rows)
            {
                var dependentIdentityId = (long)row.IdentityId;
                if (!isDependentLocked(dep.DependentVersionTable, dependentIdentityId))
                {
                    return Error.Failure(
                        "VersionedRepository.DependentSetChanged",
                        "Có thay đổi khác vừa được ghi trong lúc thao tác này đang chuẩn bị. Vui lòng thử lại.");
                }
            }

            foreach (var row in rows)
            {
                var dependentIdentityId = (long)row.IdentityId;
                var from = row.EffectiveFrom;
                var to = row.EffectiveTo;
                var dependentPeriod = new Period(from, to);
                if (!CoverageGap.TryFind(remainingParentCoverage, dependentPeriod, out var gap))
                {
                    continue;
                }

                var cutTo = gap.From.AddDays(-1);

                // Same InvalidShrink window as CloseVersionAsync — a dependent whose remaining parent
                // coverage does not extend into (or past) its own start cannot be remnant-cut without
                // destroying its base coverage.
                if (cutTo < from || cutTo >= to)
                {
                    // B1 (requester decision, 2026-08-15): a dependent that has not completed a single
                    // effective day was never in force, so the parent's stop CANCELS it rather than
                    // failing the parent's whole write. The boundary is D1's, evaluated through its one
                    // home (VersionCloseRules.BranchFor) against the operation's single captured date —
                    // never a private `<` comparison and never a second read of "today".
                    // The `cutTo >= to` half is deliberately NOT covered by this: it means the gap
                    // starts after the dependent already ended, which no coverage arithmetic should
                    // produce — a clear failure is the right answer to an impossible state.
                    //
                    // `gap` must also swallow the dependent WHOLE.
                    // `CoverageGap.TryFind` returns the FIRST uncovered stretch, so a hole at the
                    // dependent's start says nothing about whether the parent still covers a LATER part
                    // of it: identity holds [today, today+4] (the cancel target) and [today+5, open],
                    // dependent [today, today+10] — cancelling the target leaves [today+5, today+10]
                    // legitimately covered, and cancelling the dependent outright would destroy it.
                    // Cancel is only ever the answer when NOTHING of the dependent survives; anything
                    // else falls through to the pre-B1 BLOCK, which is the conservative answer and the
                    // one the operator already understands. Unreachable through today's only production
                    // edge (`role` is Immediate, so no version starts later than today) — kept as
                    // defence-in-depth on a LOCKED shared engine whose opt-in flag is open to the next
                    // entity, and deliberately NOT test-driven: reaching it needs a second test double
                    // opting into cancellation, which would cost the suite its only proof that the flag
                    // gates at all. Same disposition as the `cutTo >= to` half above.
                    var dependentFullyUncovered = gap.From <= from && gap.To >= to;

                    var canCancel = dep.DependentSupportsCancellation
                        && cutTo < from
                        && dependentFullyUncovered
                        && VersionCloseRules.BranchFor(operationDate, dependentPeriod) == VersionCloseBranch.CancelPlan;

                    if (!canCancel)
                    {
                        return Error.Validation(
                            "VersionedRepository.BaseVersionRequired",
                            $"Không thể auto-cut '{dep.DependentVersionTable}' identity={row.IdentityId}: " +
                            $"ngày cắt {cutTo.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} không nằm trong phiên bản active " +
                            $"[{from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}, {to.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}).");
                    }

                    await connection.ExecuteAsync(
                        $"UPDATE {dep.DependentVersionTable} SET isactive = 0, cancelled = 1 WHERE id = @id",
                        new { id = (long)row.Id },
                        transaction);

                    outcomes.Add(new AutoCutOutcome(
                        dep.DependentVersionTable, dependentIdentityId, (long)row.Id,
                        AutoCutAction.Cancelled, from, to, CutTo: null));
                    continue;
                }

                await connection.ExecuteAsync(
                    $"UPDATE {dep.DependentVersionTable} SET isactive = 0 WHERE id = @id",
                    new { id = (long)row.Id },
                    transaction);

                await InsertRemnantOnTableAsync(
                    connection,
                    transaction,
                    dep.DependentVersionTable,
                    (long)row.Id,
                    new Period(from, cutTo),
                    recordedBy,
                    reason,
                    // `Close` regardless of whether the PARENT was closed or cancelled, and that is not an
                    // oversight: operation_kind describes what happened to THIS row's own identity, and what
                    // happened to it is that it was cut short. A dependent shrunk during a parent's cancel was
                    // still shrunk, not cancelled. The cancel branch above is the only place a dependent is
                    // cancelled, and it deliberately leaves the row's operation_kind alone — exactly as
                    // CancelVersionCoreAsync leaves the TARGET version's kind alone and records the fact in
                    // `cancelled` instead.
                    VersionOperationKind.Close);

                outcomes.Add(new AutoCutOutcome(
                    dep.DependentVersionTable, dependentIdentityId, (long)row.Id,
                    AutoCutAction.Shrunk, from, to, cutTo));
            }
        }

        return outcomes;
    }

    // Remnant-cut for a dependent version table that is NOT this repository's TVersion — copies every
    // non-system column from the source row (same INSERT…SELECT shape as InsertRemnantAsync).
    // `operationKind` (F11, 2026-08-15): the column list is read from INFORMATION_SCHEMA anyway, so
    // whether to include `operation_kind` is decided from that SAME read rather than a second probe —
    // it used to be excluded unconditionally and never supplied, so every auto-cut remnant landed NULL
    // and could not say why it existed. It is NOT copied (the source row's kind describes the source
    // write, not this one) — it is SET, and only on a table that has the column. A dependent table
    // without it keeps the pre-F11 shape by construction.
    private static async Task<long> InsertRemnantOnTableAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string versionTable,
        long sourceVersionId,
        Period period,
        string recordedBy,
        string? reason,
        VersionOperationKind operationKind)
    {
        var allColumns = (await connection.QueryAsync<string>(
            """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
              AND COLUMN_NAME NOT IN (
                  'id', 'effective_from', 'effective_to', 'isactive',
                  'recorded_at', 'recorded_by', 'reason', 'cancelled')
            ORDER BY ORDINAL_POSITION
            """,
            new { table = versionTable },
            transaction)).ToList();

        var hasOperationKind = allColumns.Remove("operation_kind");
        var copyColumns = allColumns;

        var columnsCsv = string.Join(", ", copyColumns);
        var operationKindColumn = hasOperationKind ? ", operation_kind" : "";
        var operationKindPlaceholder = hasOperationKind ? ", @operationKind" : "";
        var sql = $"""
            INSERT INTO {versionTable}
                ({columnsCsv}, effective_from, effective_to, isactive, recorded_by, reason{operationKindColumn})
            SELECT {columnsCsv}, @from, @to, 1, @recordedBy, @reason{operationKindPlaceholder}
            FROM {versionTable} WHERE id = @sourceVersionId;
            SELECT LAST_INSERT_ID();
            """;

        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            from = period.From,
            to = period.To,
            recordedBy,
            reason,
            sourceVersionId,
            operationKind = operationKind.ToString(),
        }, transaction);
    }

    private string BuildSelectClause()
    {
        var common = new List<(string Column, string Property)>
        {
            ("id", "Id"),
            (IdentityColumn, "IdentityId"),
            ("effective_from", "EffectiveFrom"),
            ("effective_to", "EffectiveTo"),
            ("isactive", "IsActive"),
            ("recorded_at", "RecordedAt"),
            ("recorded_by", "RecordedBy"),
            ("reason", "Reason"),
        };
        if (SupportsCancellation)
        {
            common.Add(("cancelled", "Cancelled"));
        }

        return string.Join(", ", common.Concat(BusinessColumns).Select(c => $"{Alias}.{c.Column} AS {c.Property}"));
    }
}
