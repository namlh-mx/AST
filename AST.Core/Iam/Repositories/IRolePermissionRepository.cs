using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using ErrorOr;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam.Repositories;

public interface IRolePermissionRepository
{
    // No mint here, and no compensation for one: a grant identity is created INSIDE the composite
    // transaction that writes its first version (design-effective-period.md §7), through the concrete
    // repository's context overload — so a header with zero versions is not a state this interface can
    // produce. Model 2 is unchanged: every "Add function to role" still creates a brand-new identity,
    // never reusing an earlier grant's id.
    Task<IReadOnlyList<RolePermissionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf);

    Task<ErrorOr<RolePermissionVersionDto>> GetByIdentityAsync(long rolePermissionId, DateOnly asOf);

    // Full timeline for one grant identity (or all when null) — no isactive/period filter.
    Task<IReadOnlyList<RolePermissionVersionDto>> GetHistoryAsync(long? rolePermissionId = null);

    // Active grants for `roleId` whose effective period OVERLAPS `period` (isactive=1 AND period overlap).
    Task<IReadOnlyList<RolePermissionVersionDto>> GetActiveGrantsForPeriodAsync(long roleId, Period period);

    // [C3] Looks up the grant (role x function) IN EFFECT at `asOf` -> scope_level (permission resolution, D5).
    // No grant -> NotFound (caller maps to Forbidden `Authz.NotGranted`). >1 row in-period violates
    // invariant §1.5 (overlapping active grants for same (R,F)) -> Error.Conflict (CLEAR fail).
    Task<ErrorOr<RolePermissionVersionDto>> GetGrantAsync(long roleId, long functionId, DateOnly asOf);

    // STRICT temporal-FK (D8): role_id/function_id must be continuously covered by role_version/function_version
    // across the whole `period`.
    // `operationDate` is the caller-captured business date (docs/design-effective-period.md §3).
    // `permission` is `Immediate` on the same terms as role version creation.
    Task<ErrorOr<UpsertResult>> UpsertAsync(
        long rolePermissionId, Period period, long roleId, long functionId, ScopeLevel scopeLevel,
        VersionOperationKind operationKind, OperationDate operationDate, string recordedBy, string? reason);
}
