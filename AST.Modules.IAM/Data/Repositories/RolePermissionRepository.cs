using System.Data;
using System.Globalization;
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

internal sealed class RolePermissionRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates)
    : VersionedRepository<RolePermissionVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates),
        IRolePermissionRepository
{
    protected override string VersionTable => "role_permission_version";
    protected override string IdentityColumn => "role_permission_id";

    protected override bool SupportsCancellation => true;
    protected override bool RecordsOperationKind => true;

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("role_id", nameof(RolePermissionVersionEntity.RoleId)),
        ("function_id", nameof(RolePermissionVersionEntity.FunctionId)),
        ("scope_level", nameof(RolePermissionVersionEntity.ScopeLevel)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(RolePermissionVersionEntity newValues) =>
        new Dictionary<string, long>
        {
            ["role_id"] = newValues.RoleId,
            ["function_id"] = newValues.FunctionId,
        };

    // Own connection, so the header does NOT commit with its first version — kept only for test fixtures
    // (no production caller since the backlog). The production grant-add path uses the context
    // overload below.
    public async Task<long> CreateIdentityAsync()
    {
        using var connection = Connections.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO role_permission () VALUES (); SELECT LAST_INSERT_ID();");
    }

    // §7: mints the grant identity inside the caller's composite transaction — header and first version
    // commit or roll back together. Concrete-only, like every other composite overload
    // (rule-module-boundary §3).
    internal Task<long> CreateIdentityAsync(ICompositeWriteContext context) => InsertIdentityAsync(context);

    public async Task DeleteEmptyIdentityAsync(long rolePermissionId)
    {
        using var connection = Connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            DELETE FROM role_permission
            WHERE id = @rolePermissionId
              AND NOT EXISTS (
                  SELECT 1 FROM role_permission_version WHERE role_permission_id = @rolePermissionId)
            """,
            new { rolePermissionId });
    }

    public async Task<IReadOnlyList<RolePermissionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
    {
        var rows = await QueryInScopeAsync(scope, asOf);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<RolePermissionVersionDto>> GetByIdentityAsync(long rolePermissionId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(rolePermissionId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    // Composite-write overload: reads via context.Connection/Transaction (read-your-own-writes under READ
    // COMMITTED) — see VersionedRepository.ResolveAtAsync(ICompositeWriteContext, ...) and the RoleRepository
    // composite-Upsert precedent. Deliberately NOT on IRolePermissionRepository (composite overloads stay
    // concrete-only, rule-module-boundary §3).
    public async Task<ErrorOr<RolePermissionVersionDto>> GetByIdentityAsync(
        ICompositeWriteContext context, long rolePermissionId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(context, rolePermissionId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    public async Task<IReadOnlyList<RolePermissionVersionDto>> GetHistoryAsync(long? rolePermissionId = null)
    {
        var businessSelect = string.Join(", ", BusinessColumns.Select(c => $"h.{c.Column} AS {c.Property}"));
        var sql =
            $"""
            SELECT h.id AS Id,
                   h.role_permission_id AS IdentityId,
                   h.effective_from AS EffectiveFrom,
                   h.effective_to AS EffectiveTo,
                   h.isactive AS IsActive,
                   h.recorded_at AS RecordedAt,
                   h.recorded_by AS RecordedBy,
                   h.reason AS Reason,
                   h.cancelled AS Cancelled,
                   h.operation_kind AS OperationKind,
                   {businessSelect}
            FROM role_permission_version h
            WHERE (@rolePermissionId IS NULL OR h.role_permission_id = @rolePermissionId)
            ORDER BY h.recorded_at DESC, h.id DESC
            """;

        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<RolePermissionVersionEntity>(sql, new { rolePermissionId });
        return rows.Select(ToDto).ToList();
    }

    // Shared by both GetActiveGrantsForPeriodAsync overloads below — BusinessColumns is an instance
    // property (per-entity override), so this cannot be a static field; computed once per call is cheap
    // (string.Join over 3 columns) and avoids keeping two copies of the query text in sync.
    private string ActiveGrantsForPeriodSql
    {
        get
        {
            var businessSelect = string.Join(", ", BusinessColumns.Select(c => $"{c.Column} AS {c.Property}"));
            return
                $"""
                SELECT id AS Id,
                       role_permission_id AS IdentityId,
                       effective_from AS EffectiveFrom,
                       effective_to AS EffectiveTo,
                       isactive AS IsActive,
                       recorded_at AS RecordedAt,
                       recorded_by AS RecordedBy,
                       reason AS Reason,
                       cancelled AS Cancelled,
                       operation_kind AS OperationKind,
                       {businessSelect}
                FROM role_permission_version
                WHERE isactive = 1
                  AND role_id = @roleId
                  AND effective_from <= @to
                  AND @from <= effective_to
                """;
        }
    }

    public async Task<IReadOnlyList<RolePermissionVersionDto>> GetActiveGrantsForPeriodAsync(
        long roleId, EffectivePeriod period)
    {
        using var connection = Connections.CreateConnection();
        var rows = await connection.QueryAsync<RolePermissionVersionEntity>(
            ActiveGrantsForPeriodSql, new { roleId, from = period.From, to = period.To });
        return rows.Select(ToDto).ToList();
    }

    // Composite-context sibling of GetActiveGrantsForPeriodAsync above: the plain overload opens its OWN
    // connection via Connections.CreateConnection(), which under READ COMMITTED cannot see writes made
    // earlier in the same composite transaction (e.g. a revoke run just before this overlap check). This
    // overload executes on context.Connection/context.Transaction instead — read-your-own-writes, same
    // pattern as GetByIdentityAsync(ICompositeWriteContext, …) above. Deliberately NOT on
    // IRolePermissionRepository (composite overloads stay concrete-only, rule-module-boundary §3).
    public async Task<IReadOnlyList<RolePermissionVersionDto>> GetActiveGrantsForPeriodAsync(
        ICompositeWriteContext context, long roleId, EffectivePeriod period)
    {
        var rows = await context.Connection.QueryAsync<RolePermissionVersionEntity>(
            ActiveGrantsForPeriodSql, new { roleId, from = period.From, to = period.To }, context.Transaction);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<RolePermissionVersionDto>> GetGrantAsync(long roleId, long functionId, DateOnly asOf)
    {
        var rows = await QueryApplicableAsync(
            $"{Alias}.role_id = @roleId AND {Alias}.function_id = @functionId",
            new { roleId, functionId }, asOf);
        if (rows.Count == 0)
        {
            return Error.NotFound(
                "RolePermission.NotGranted",
                $"Role {roleId} has no grant for function {functionId} at {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.");
        }

        if (rows.Count > 1)
        {
            return Error.Conflict(
                "RolePermission.DuplicateGrant",
                $"{rows.Count} overlapping active grants for (role {roleId}, function {functionId}) at {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} - violates Model 2 non-overlap.");
        }

        return ToDto(rows[0]);
    }

    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        long rolePermissionId, EffectivePeriod period, long roleId, long functionId, ScopeLevel scopeLevel,
        VersionOperationKind operationKind, OperationDate operationDate, string recordedBy, string? reason)
    {
        if (GuardImmediateUpsert(period, operationDate) is { } immediateError)
        {
            return immediateError;
        }

        var newValues = new RolePermissionVersionEntity
        {
            IdentityId = rolePermissionId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            RoleId = roleId,
            FunctionId = functionId,
            ScopeLevel = scopeLevel,
            RecordedBy = recordedBy,
            Reason = reason,
        };

        return await UpsertVersionAsync(rolePermissionId, period, newValues, recordedBy, reason, operationKind);
    }

    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        ICompositeWriteContext context, long rolePermissionId, EffectivePeriod period, long roleId,
        long functionId, ScopeLevel scopeLevel, VersionOperationKind operationKind,
        OperationDate operationDate, string recordedBy, string? reason)
    {
        if (GuardImmediateUpsert(period, operationDate) is { } immediateError)
        {
            return immediateError;
        }

        var newValues = new RolePermissionVersionEntity
        {
            IdentityId = rolePermissionId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            RoleId = roleId,
            FunctionId = functionId,
            ScopeLevel = scopeLevel,
            RecordedBy = recordedBy,
            Reason = reason,
        };

        return await UpsertVersionAsync(context, rolePermissionId, period, newValues, recordedBy, reason, operationKind);
    }

    // Revoke = Close: truncates the grant's active version so `effectiveThrough` is its new last
    // effective day, INCLUSIVE (same semantics as the base CloseVersionAsync's `newTo`) — the grant
    // is no longer effective starting the following day. Reverse-FK has no dependents registered
    // against role_permission today, so this cannot BLOCK on anything currently in the schema —
    // kept as a real Close (not a bespoke revoke path) so any future dependent edge is automatically
    // protected without touching this method.
    public Task<ErrorOr<UpsertResult>> RevokeAsync(
        long rolePermissionId, long versionId, DateOnly effectiveThrough, OperationDate operationDate,
        string recordedBy, string? reason) =>
        CloseVersionAsync(rolePermissionId, versionId, effectiveThrough, operationDate, recordedBy, reason);

    public Task<ErrorOr<UpsertResult>> RevokeAsync(
        ICompositeWriteContext context, long rolePermissionId, long versionId, DateOnly effectiveThrough,
        OperationDate operationDate, string recordedBy, string? reason) =>
        CloseVersionAsync(context, rolePermissionId, versionId, effectiveThrough, operationDate, recordedBy, reason);

    // Composite-write wrapper for Close: the base composite overload is `protected internal`.
    // Straight passthrough — close-date policy lives on ValidateClose, which both base overloads call.
    public new Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        ICompositeWriteContext context, long rolePermissionId, long versionId, DateOnly newTo,
        OperationDate operationDate, string recordedBy, string? reason) =>
        base.CloseVersionAsync(context, rolePermissionId, versionId, newTo, operationDate, recordedBy, reason);

    // Thin wrapper unblocking the base cancel path (SupportsCancellation is already true above, but the
    // base's CancelVersionAsync overloads are `protected`/`protected internal` and unreachable from
    // RoleDeclarationService's assembly without this passthrough) — mirrors
    // OrgUnitRepository.CancelPlanAsync (OrgUnitRepository.cs:188-201). `operationDate` is the
    // CALLER-supplied operation date for this whole operation (design-effective-period.md §3 — captured
    // ONCE by the caller and threaded through unchanged), NOT re-read from any engine-internal clock.
    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        long rolePermissionId, long versionId, DateOnly operationDate, string recordedBy, string reason) =>
        CancelVersionAsync(rolePermissionId, versionId, operationDate, recordedBy, reason);

    // Composite-context sibling of CancelPlanAsync above, same rationale as the plain overload — the
    // base's composite CancelVersionAsync(ICompositeWriteContext, …) is `protected internal`, unreachable
    // from RoleDeclarationService's assembly. Straight passthrough to the base seam.
    public Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        ICompositeWriteContext context, long rolePermissionId, long versionId, DateOnly operationDate,
        string recordedBy, string reason) =>
        CancelVersionAsync(context, rolePermissionId, versionId, operationDate, recordedBy, reason);

    // The PERIOD half of §1.5's Immediate rule: a grant version starts on the operation date and ends
    // at the open end. A grant has nothing editable in place — a second version on an existing
    // identity is a defect (identity half: ValidateUpsertAsync below).
    //
    // Pure function of (period, operationDate): no I/O, no clock, no identity — which is why it may run
    // before the write lock is taken. The IDENTITY half of the same rule reads the database and must
    // therefore run UNDER the lock; it lives in ValidateUpsertAsync below. Do NOT add an identity check
    // here: at this point nothing is serialised, so it would be a check-then-act race.
    private static Error? GuardImmediateUpsert(EffectivePeriod period, OperationDate operationDate)
    {
        var today = operationDate.Value;
        if (period.To != EffectivePeriod.OpenEnd)
        {
            return Error.Validation(
                "RolePermission.EffectiveToMustBeOpen",
                $"A grant version must end on {EffectivePeriod.OpenEnd:yyyy-MM-dd} (open end); requested {period.To:yyyy-MM-dd}.");
        }

        if (period.From == today)
        {
            return null;
        }

        return Error.Validation(
            "RolePermission.EffectiveFromMustBeToday",
            $"A grant version must start on today ({today:yyyy-MM-dd}), not {period.From:yyyy-MM-dd}.");
    }

    // §1.5 (Model 2): a grant identity carries ONE version for life — `(role_id, function_id)` and
    // `scope_level` are fixed once written, and only the period's END moves, to end the grant. So an
    // identity that already has ANY version row may never be upserted again: a scope change is
    // revoke-old + grant-new, on a NEW identity.
    //
    // The probe is existence-ANY, and the two tempting filters fail on DIFFERENT shapes, so neither is
    // a safe simplification: an "effective today" read calls a REVOKED identity empty (its remnant
    // ends yesterday, though that remnant is still `isactive = 1`), while an `isactive = 1` read finds
    // that remnant but calls a CANCELLED-only identity empty (its sole version is `isactive = 0,
    // cancelled = 1`, with no predecessor to restore). Either would let the identity be re-granted in
    // place — exactly the restoration §1.5 forbids.
    //
    // Runs on the ambient connection/transaction under the identity's named lock (see the base hook),
    // so it also sees rows this same operation wrote earlier and is serialised against the other
    // cooperating writer of this identity.
    protected override async Task<Error?> ValidateUpsertAsync(
        IDbConnection connection, IDbTransaction transaction, long identityId)
    {
        var exists = await connection.ExecuteScalarAsync<long?>(
            "SELECT 1 FROM role_permission_version WHERE role_permission_id = @identityId LIMIT 1",
            new { identityId }, transaction);

        return exists is null
            ? null
            : Error.Validation(
                "RolePermission.IdentityAlreadyVersioned",
                $"Grant identity {identityId} already has version history; a grant carries one version for life, so a change is revoke-old + grant-new on a new identity.");
    }

    protected override Error? ValidateClose(OperationDate operationDate, DateOnly newTo)
    {
        var required = operationDate.Value.AddDays(-1);
        if (newTo == required)
        {
            return null;
        }

        return Error.Validation(
            "RolePermission.RevokeDateMustBeImmediate",
            $"A grant revoke must end on today minus one day ({required:yyyy-MM-dd}); requested {newTo:yyyy-MM-dd}.");
    }

    private static RolePermissionVersionDto ToDto(RolePermissionVersionEntity e) => new(
        e.Id, e.IdentityId, e.EffectiveFrom, e.EffectiveTo, e.IsActive,
        e.RoleId, e.FunctionId, e.ScopeLevel, e.RecordedAt, e.RecordedBy, e.Reason,
        e.Cancelled, e.OperationKind);
}