using ErrorOr;

namespace AST.Core.Iam;

// [Slice A declares — Slice C3 implements] Async because the resolution body READS the DB via a repository
// (every DB operation in the project is async); keeping a sync signature would force a sync-over-async block
// on the UI thread -> WPF dispatcher deadlock (skill preventing-dispatcher-deadlock). No caller existed at the
// time of the change -> blast-radius = 0.
[SharedComponent]
public interface IAuthorizationService
{
    // Level 1 (open?) + level 2 (scope). Resolves user->(role,org unit) by TODAY (D5).
    // Not granted -> Error.Forbidden("Authz.NotGranted", ...).
    Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey);

    // Only checks whether the function is open (level 1) — used for leaf-menu show/hide (spec ⑥).
    Task<bool> IsFunctionOpenAsync(string username, string functionKey);
}
