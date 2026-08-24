using AST.Core.Data;
using AST.Core.EffectivePeriod;
using ErrorOr;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam.Repositories;

public interface IFunctionRepository
{
    Task<IReadOnlyList<FunctionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf);

    Task<ErrorOr<FunctionVersionDto>> GetByIdentityAsync(long functionId, DateOnly asOf);

    // [C3] Looks up the function OPEN at `asOf` by function_key (permission resolution + menu show/hide, D5).
    // Not found/not open -> NotFound (caller maps to Forbidden). function_key is unique-per-day (C2:
    // 1 key = 1 identity) -> >1 row violates the invariant -> Error.Conflict (CLEAR fail).
    Task<ErrorOr<FunctionVersionDto>> GetByKeyAsync(string functionKey, DateOnly asOf);

    Task<ErrorOr<UpsertResult>> UpsertAsync(
        long functionId, Period period, string functionKey, string businessCode,
        string displayName, string menuGroup, string navTarget,
        string recordedBy, string? reason);

    // [C2] Creates a new function IDENTITY + first version (sync case 1). `period` = epoch [2000-01-01, open].
    //
    // The identity and its first version commit or roll back TOGETHER (design-effective-period.md §7), and
    // the whole attempt runs under the catalog-create lock: the key's absence is re-decided INSIDE the
    // transaction, so two workstations syncing at once cannot both create it. Returning
    // `KeyAlreadyPresent` is therefore a normal outcome, not an error — see FunctionCreateOutcome.
    Task<ErrorOr<FunctionCreateOutcome>> CreateAsync(
        Period period, string functionKey, string businessCode,
        string displayName, string menuGroup, string navTarget,
        string recordedBy, string? reason);

    // [C2] Every function_key that has EVER appeared in the DB (including closed ones) — distinguishes "no
    // identity yet" (create new) from "exists but not active" (reopen candidate).
    //
    // This is an UNLOCKED read and decides nothing on its own: between it and a create, another
    // workstation may create the same key. What actually holds "1 key = 1 identity" is CreateAsync's
    // re-read under the catalog-create lock. (Until 2026-08-17 this comment claimed the read "blocks
    // creating a duplicate identity" — it never could.)
    Task<IReadOnlyList<string>> GetAllKnownFunctionKeysAsync();
}

// What a create attempt did, as a closed set of the only two possibilities — deliberately NOT a bool plus
// a nullable payload, a shape that would permit the unconstructible state "created, but nothing written".
// Same reasoning as RoleSaveTarget's.
//
// `KeyAlreadyPresent` is named for what the repository can OBSERVE, never for why. Under the lock it sees
// only that the key already has a version; a concurrent create and a plain sequential second call are
// indistinguishable from here, and a name asserting the first would be a causal claim the repository
// cannot make. Both are correct outcomes: C2 forbids a second identity for a key either way.
public abstract record FunctionCreateOutcome
{
    private FunctionCreateOutcome() { }

    public sealed record Created(long FunctionId, UpsertResult Written) : FunctionCreateOutcome;

    public sealed record KeyAlreadyPresent : FunctionCreateOutcome;
}
