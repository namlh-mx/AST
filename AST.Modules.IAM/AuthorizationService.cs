using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Time;
using ErrorOr;

namespace AST.Modules.IAM;

// [Slice C3] Implements IAuthorizationService — a PURE decision function (reads the DB, does NOT write
// audit_log; break-glass/login auditing belongs to the calling layer §⑤/login). Resolves by TODAY (D5, dates.Today). Fails CLOSED.
// Semantics: docs/design-iam-schema.md (IAuthorizationService).
internal sealed class AuthorizationService(
    IUserRepository users,
    IFunctionRepository functions,
    IRolePermissionRepository grants,
    IBreakGlassPolicy breakGlass,
    IBusinessDateProvider dates) : IAuthorizationService
{
    public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) =>
        ResolveAsync(username, functionKey);

    // Leaf menu show/hide: only needs a bool. Every rejection path (Forbidden) AND invariant violation (Conflict) -> hide
    // (fail CLOSED for the menu). Shares ResolveAsync so the invariant "leaf shown <=> Authorize succeeds" holds.
    public async Task<bool> IsFunctionOpenAsync(string username, string functionKey) =>
        !(await ResolveAsync(username, functionKey)).IsError;

    // 5 steps (docs §...): [1] break-glass -> Global; [2] is the function open today?; [3] is the user valid today?; [4]
    // is there a grant (user's role x function) today?; [5] -> DataScope(scope_level, user's root org unit, username).
    private async Task<ErrorOr<DataScope>> ResolveAsync(string username, string functionKey)
    {
        var today = dates.Today;

        // [1] Break-glass wins BEFORE any DB lookup (first bootstrap admin / rescue when the permission grid is empty, §⑤).
        if (breakGlass.IsBreakGlassAdmin(username))
        {
            return new DataScope(ScopeLevel.Global, null, username);
        }

        // [2] The function must be open today.
        var function = await functions.GetByKeyAsync(functionKey, today);
        if (function.IsError)
        {
            return DenyOrPropagate(function.Errors);
        }

        // [3] The user must be valid today (duplicate username -> Conflict, propagate = fail CLEARLY).
        var user = await users.GetByUsernameAsync(username, today);
        if (user.IsError)
        {
            return DenyOrPropagate(user.Errors);
        }

        // [4] Grant for the user's role x function today. None -> Forbidden; overlapping duplicate grants
        // (Conflict §1.5) -> propagate = fail CLEARLY (does NOT get hidden as Forbidden).
        var grant = await grants.GetGrantAsync(user.Value.RoleId, function.Value.FunctionId, today);
        if (grant.IsError)
        {
            return DenyOrPropagate(grant.Errors);
        }

        // [5] Granted -> scope per scope_level. RootOrgUnitId = null when Global (DataScope convention),
        // otherwise = the user's root org unit today.
        var rootOrgUnitId = grant.Value.ScopeLevel == ScopeLevel.Global ? (long?)null : user.Value.OrgUnitId;
        return new DataScope(grant.Value.ScopeLevel, rootOrgUnitId, username);
    }

    // Fail CLOSED: "not found" (function/user not valid today) -> Forbidden (does NOT leak details of what is
    // missing — avoids probing). Other errors (Conflict: uniqueness invariant violation) -> propagate AS-IS
    // (fail CLEARLY, does not get hidden as Forbidden -- prefer clear failure over ambiguous behavior).
    private static ErrorOr<DataScope> DenyOrPropagate(IReadOnlyList<Error> errors) =>
        errors[0].Type == ErrorType.NotFound ? Forbidden() : errors.ToList();

    private static Error Forbidden() =>
        Error.Forbidden("Authz.NotGranted", "Không được cấp quyền cho chức năng này.");
}
