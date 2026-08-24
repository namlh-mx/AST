using AST.Core.Data;
using AST.Core.EffectivePeriod;
using ErrorOr;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam.Repositories;

public interface IUserRepository
{
    Task<IReadOnlyList<UserVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf);

    Task<ErrorOr<UserVersionDto>> GetByIdentityAsync(long userId, DateOnly asOf);

    // [C3] Looks up the user IN EFFECT at `asOf` by username (permission resolution, D5). Not found -> NotFound.
    // >1 row -> Error.Conflict (CLEAR fail): violates the username-unique-per-day invariant (§1.3), does NOT
    // silently pick one. Caller (AuthorizationService) fails CLOSED: NotFound -> Forbidden, Conflict -> propagate.
    Task<ErrorOr<UserVersionDto>> GetByUsernameAsync(string username, DateOnly asOf);

    // STRICT temporal-FK (D8): org_unit_id/role_id must be continuously covered by org_unit_version/role_version
    // across the whole `period`, otherwise -> Error (does NOT save).
    Task<ErrorOr<UpsertResult>> UpsertAsync(
        long userId, Period period, string username, string displayName,
        long orgUnitId, long roleId, string recordedBy, string? reason);

    // Writes the SID once into the `user` header (write-once, guard `sid IS NULL` — R4 2026-07-03).
    // Returns false if sid already has a different value (does NOT overwrite) -- boundary: this function does NOT
    // decide "username reused for someone else -> create a new identity" itself; that is a business decision of
    // the Slice C service (the service layer), the repo only provides the guarded write-once primitive.
    Task<bool> TrySetSidOnceAsync(long userId, string sid);
}
