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

internal sealed class FunctionRepository(
    IDbConnectionFactory connections,
    IStandardScopeFilterBuilder scopeFilter,
    IEffectivePeriodResolver resolver,
    IPeriodEditor periodEditor,
    ITemporalFkValidator fkValidator,
    ITemporalFkRegistry fkRegistry,
    IBusinessDateProvider dates)
    : VersionedRepository<FunctionVersionEntity>(connections, scopeFilter, resolver, periodEditor, fkValidator, fkRegistry, dates),
        IFunctionRepository
{
    protected override string VersionTable => "function_version";
    protected override string IdentityColumn => "function_id";

    protected override IReadOnlyList<(string Column, string Property)> BusinessColumns =>
    [
        ("function_key", nameof(FunctionVersionEntity.FunctionKey)),
        ("business_code", nameof(FunctionVersionEntity.BusinessCode)),
        ("display_name", nameof(FunctionVersionEntity.DisplayName)),
        ("menu_group", nameof(FunctionVersionEntity.MenuGroup)),
        ("nav_target", nameof(FunctionVersionEntity.NavTarget)),
    ];

    protected override IReadOnlyDictionary<string, long> ExtractParentIdentityIds(FunctionVersionEntity newValues) =>
        new Dictionary<string, long>();

    public async Task<IReadOnlyList<FunctionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
    {
        var rows = await QueryInScopeAsync(scope, asOf);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ErrorOr<FunctionVersionDto>> GetByIdentityAsync(long functionId, DateOnly asOf)
    {
        var result = await ResolveAtAsync(functionId, asOf);
        if (result.IsError)
        {
            return result.Errors;
        }

        return ToDto(result.Value);
    }

    public async Task<ErrorOr<FunctionVersionDto>> GetByKeyAsync(string functionKey, DateOnly asOf)
    {
        var rows = await QueryApplicableAsync($"{Alias}.function_key = @functionKey", new { functionKey }, asOf);
        if (rows.Count == 0)
        {
            return Error.NotFound(
                "Function.NotOpen",
                $"Function '{functionKey}' không mở tại {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} (chưa đồng bộ hoặc đã đóng).");
        }

        if (rows.Count > 1)
        {
            return Error.Conflict(
                "Function.DuplicateKey",
                $"Có {rows.Count} function active trùng key '{functionKey}' tại {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} — vi phạm bất biến 1 key = 1 căn cước (C2).");
        }

        return ToDto(rows[0]);
    }

    // Serialises EVERY function create against every other one — deliberately a singleton, not a key-derived
    // lock, and the reasoning is worth keeping because two key-derived designs were rejected here:
    //   * MySQL caps GET_LOCK names at 64 characters, while function_key is VARCHAR(150). A raw-key name
    //     could exceed it, and MySQL then THROWS rather than returning 0/NULL — measured against this
    //     project's own server on 2026-08-17, not inferred: SELECT GET_LOCK(REPEAT('a',65), 1) raises a
    //     MySqlException. So the create would fail as an unhandled exception out of CompositeWrite, not as
    //     any Error a caller could read. (An earlier draft of this comment claimed it would be misreported
    //     as VersionedRepository.LockTimeout; that was a guess, and it was wrong.)
    //   * A NORMALISED key would have to be at least as WIDE as utf8mb4_0900_ai_ci (which folds case AND
    //     accents), or two writers take different locks and both pass the re-check below. RoleRepository
    //     .CodeLockKey gets away with ASCII-only folding because RoleDeclarationService rejects a non-ASCII
    //     code at the boundary first; `function` has NO such boundary — FunctionRegistry.Register validates
    //     nothing — so the same trick would either be unsound or introduce an unenforceable precondition.
    // A constant sidesteps both: bounded by construction, and indifferent to what the key contains.
    // The cost is real and accepted: creates queue behind each other. They happen only when a deployment
    // introduces a new key, and the critical section is one COUNT plus at most one INSERT pair.
    public const string CatalogCreateLockKey = "iam:function:catalog-create-singleton";

    public async Task<ErrorOr<UpsertResult>> UpsertAsync(
        long functionId, EffectivePeriod period, string functionKey, string businessCode,
        string displayName, string menuGroup, string navTarget, string recordedBy, string? reason) =>
        await UpsertVersionAsync(
            functionId, period,
            BuildVersion(functionId, period, functionKey, businessCode, displayName, menuGroup, navTarget, recordedBy, reason),
            recordedBy, reason);

    // [C2] Creates a new identity AND writes its first version inside ONE transaction, under the
    // catalog-create lock. Two invariants ride on that single transaction:
    //   * §7 — the header and its first version commit or roll back TOGETHER. A failed write leaves no row
    //     at all, so no compensating delete exists here and none is relied upon.
    //   * C2 — 1 key = 1 identity. The caller's "is this key absent?" read happened WITHOUT a lock, so it is
    //     a TOCTOU; it is retaken below under the lock, inside the transaction. Until 2026-08-17 nothing
    //     enforced this on the CREATE path and IntegrityCheckService's duplicate-key sweep was the only
    //     guard — which reports a violation after it has already been written.
    //     The reach is exactly this method, and stating it wider would be false: UpsertAsync writes whatever
    //     function_key it is handed under only astep:function_version:{id}, with no catalog lock and no key
    //     re-read. Nothing introduces a NEW key that way today (the sync's upsert branch re-passes the
    //     identity's own key), which is why that is a boundary to respect rather than a defect to fix here.
    // Returns KeyAlreadyPresent when the re-read finds the key: a normal outcome, not an error. The losing
    // caller mints NOTHING, so it leaves neither a header nor a consumed AUTO_INCREMENT value.
    public async Task<ErrorOr<FunctionCreateOutcome>> CreateAsync(
        EffectivePeriod period, string functionKey, string businessCode,
        string displayName, string menuGroup, string navTarget, string recordedBy, string? reason)
    {
        FunctionCreateOutcome? outcome = null;

        // Nothing is Enlist-ed by IDENTITY: `function` declares no temporal-FK parent, and the identity
        // minted below takes no named lock of its own (§7 carve-out — nothing committed can name it until
        // commit). The lock above is a different lock, protecting a different invariant, and like every
        // other key it is acquired UP FRONT in CompositeWrite's single fixed-order batch.
        var run = await new CompositeWrite(Connections)
            .Enlist(CatalogCreateLockKey)
            .ExecuteAsync(async context =>
            {
                // Asks exactly what GetAllKnownFunctionKeysAsync asks — ANY version ever, not "active
                // today". A key whose only versions are closed is PRESENT, and reopening it is the sync's
                // reopen-candidate branch, never a second create.
                if (await KeyHasAnyVersionAsync(context, functionKey))
                {
                    outcome = new FunctionCreateOutcome.KeyAlreadyPresent();
                    return Result.Success;
                }

                var functionId = await InsertIdentityAsync(context);
                var upsert = await UpsertVersionAsync(
                    context, functionId, period,
                    BuildVersion(functionId, period, functionKey, businessCode, displayName, menuGroup, navTarget, recordedBy, reason),
                    recordedBy, reason);
                if (upsert.IsError)
                {
                    return upsert.Errors;
                }

                outcome = new FunctionCreateOutcome.Created(functionId, upsert.Value);
                return Result.Success;
            });

        if (run.IsError)
        {
            return run.Errors;
        }

        // Unreachable: ExecuteAsync only reports success after the delegate returned Success, and both of
        // its Success paths assign. Fail clearly rather than dereference a null.
        return outcome ?? throw new InvalidOperationException(
            "Tạo chức năng thành công nhưng không ghi nhận được kết quả — trạng thái không hợp lệ.");
    }

    private async Task<bool> KeyHasAnyVersionAsync(ICompositeWriteContext context, string functionKey) =>
        await context.Connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {VersionTable} WHERE function_key = @functionKey",
            new { functionKey },
            context.Transaction) > 0;

    private static FunctionVersionEntity BuildVersion(
        long functionId, EffectivePeriod period, string functionKey, string businessCode,
        string displayName, string menuGroup, string navTarget, string recordedBy, string? reason) =>
        new()
        {
            IdentityId = functionId,
            EffectiveFrom = period.From,
            EffectiveTo = period.To,
            IsActive = true,
            FunctionKey = functionKey,
            BusinessCode = businessCode,
            DisplayName = displayName,
            MenuGroup = menuGroup,
            NavTarget = navTarget,
            RecordedBy = recordedBy,
            Reason = reason,
        };

    public async Task<IReadOnlyList<string>> GetAllKnownFunctionKeysAsync()
    {
        using var connection = Connections.CreateConnection();
        var keys = await connection.QueryAsync<string>(
            $"SELECT DISTINCT function_key FROM {VersionTable}");
        return keys.ToList();
    }

    private static FunctionVersionDto ToDto(FunctionVersionEntity e) => new(
        e.Id, e.IdentityId, e.EffectiveFrom, e.EffectiveTo, e.IsActive,
        e.FunctionKey, e.BusinessCode, e.DisplayName, e.MenuGroup, e.NavTarget,
        e.RecordedAt, e.RecordedBy, e.Reason);
}
