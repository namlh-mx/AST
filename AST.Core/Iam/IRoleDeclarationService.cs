using AST.Core.Data;
using ErrorOr;

namespace AST.Core.Iam;

// Orchestration use-case that closes OPEN-B2: "Edit role + Revoke a grant + Add a grant in one Save"
//. ONE Save call composes:
//   - the role's own version edit (RoleRepository.UpsertAsync, composite overload),
//   - stopping (Revoke) any number of existing role_permission grants — per grant the service DERIVES
//     whether that means closing it or cancelling it outright (boundary's home: VersionCloseRules.BranchFor),
//   - opening (Add) any number of new role_permission grants,
// all inside ONE CompositeWrite transaction, with an `audit_log` row per grant Add/Revoke/Cancel and one
// per role Add/Edit (O2 as amended 2026-08-14), all at target `role:{roleId}`, all carrying the save's
// operation id in `detail`. Authority (admin-flag change / actor identity) is derived server-side from
// IBreakGlassPolicy/ICurrentWindowsUser — the request DTO deliberately does NOT carry those, so a caller
// cannot self-assert authorization.
//
// Implementation: AST.Modules.IAM/RoleDeclarationService.cs (internal — depends on the CONCRETE
// RoleRepository/RolePermissionRepository classes for their composite-write overloads, which are NOT on
// the public IRoleRepository/IRolePermissionRepository interfaces by design).
[SharedComponent]
public interface IRoleDeclarationService
{
    Task<ErrorOr<SaveRoleDeclarationResult>> SaveRoleDeclarationAsync(SaveRoleDeclarationRequest request);

    // Second use-case (brief 060): Close (retire) a role version whose EffectiveFrom is strictly before
    // today, or Cancel-plan one whose EffectiveFrom is today or later (D1, 2026-08-10 — a version that
    // has not completed a single effective day is cancellable even though it is displayed as effective
    // today). WHICH of the two runs is derived server-side from the target version's own EffectiveFrom
    // vs. business "today" (D5) — the caller does NOT get to pick "close" vs "cancel" itself, so it
    // cannot self-assert which repository operation runs. Same server-derivation posture as
    // SaveRoleDeclarationAsync's adminFlagChangeAuthorized/actor: nothing here lets the caller assert its
    // own authority or the branch taken.
    Task<ErrorOr<UpsertResult>> CloseRoleDeclarationAsync(CloseRoleDeclarationRequest request);
}

// WHICH role a save targets, as a closed set of two cases — deliberately not a nullable id plus nullable
// tokens, because that shape permits "existing role, no expected version", i.e. exactly the unchecked
// delta merge B4 exists to end (F10). Here that state cannot be constructed.
public abstract record RoleSaveTarget
{
    private RoleSaveTarget() { }

    // The operator is declaring a role BY CODE. The service resolves the code to zero/one/many historical
    // identities under the code lock and either mints or reattaches to a single NOT-CURRENTLY-IN-FORCE
    // owner (B2, settled item 8) — the caller does not choose, so it cannot split an identity, and it
    // cannot accidentally edit a live role that happens to share the code.
    public sealed record NewRole : RoleSaveTarget;

    // Editing the role the caller has loaded.
    //   ExpectedRoleVersionId — the version the caller READ; a mismatch rejects the save (B4, F10).
    //   ExpectedRoleCode      — that version's code as the caller read it. Used as a LOCK KEY before the
    //                           transaction opens and then VERIFIED inside it: a caller that passes the
    //                           wrong code fails cleanly instead of silently taking the wrong lock.
    public sealed record ExistingRole(long RoleId, long ExpectedRoleVersionId, string ExpectedRoleCode) : RoleSaveTarget;
}

// `RoleCode`/`RoleName`/`IsAdminRole`/`Reason` are the role-edit fields the service writes via the
// composite `RoleRepository.UpsertAsync`. Role version creation goes only through `IRoleDeclarationService`.
// `OperationKind` is derived server-side from `Target` (NewRole → Add, ExistingRole → Edit).
// `adminFlagChangeAuthorized` and the actor (`recordedBy`) are deliberately NOT here — both are derived
// server-side (IBreakGlassPolicy.IsBreakGlassAdmin / ICurrentWindowsUser.Username) so a caller cannot
// self-assert either.
//
// Carries NO period (requester decision, 2026-08-13). `role` is Immediate: the only legal start for a
// written version is the operation's own business date, and the only legal end is the open end. The
// service derives `new EffectivePeriod(operationDate, OpenEnd)` for the role version and every
// grant-to-add.
public sealed record SaveRoleDeclarationRequest(
    RoleSaveTarget Target,
    string RoleCode,
    string RoleName,
    bool IsAdminRole,
    string? Reason,
    IReadOnlyList<long> GrantIdentityIdsToRevoke,
    IReadOnlyList<RolePermissionGrantToAdd> GrantsToAdd);

// One new role_permission grant to open. `Note` becomes that grant version's `reason` column and the
// audit row's `note` field. Carries NO period — same Immediate derivation as SaveRoleDeclarationRequest
// (one operation, one business date; a caller-supplied grant start would be the same trap).
public sealed record RolePermissionGrantToAdd(
    long FunctionId,
    ScopeLevel ScopeLevel,
    string? Note);

public sealed record SaveRoleDeclarationResult(
    long RoleId,
    long RoleVersionId,
    bool ReattachedToExistingIdentity,
    IReadOnlyList<long> AddedRolePermissionIds,
    IReadOnlyList<long> RevokedRolePermissionIds);

// `RoleId`+`VersionId` identify the exact version to close/cancel (read server-side via
// RoleRepository.GetHistoryAsync — never trusted to be "the currently active one" implicitly).
// No actor/authorization field, same reasoning as SaveRoleDeclarationRequest above — derived server-side.
//
// Carries NO close date (requester decision, 2026-08-12). `role` is `Immediate`: it stops the moment the
// change is declared, so exactly ONE close date is legal — the day before the operation's own business
// date, recorded as the INCLUSIVE end `operationDate - 1` meaning "ceases from today". The service
// derives it. A caller-supplied date could only ever be that same value or an error, and an input whose
// every non-trivial value is rejected is a trap for the screen that fills it in; the field is removed
// rather than left ignored, which would be the same trap one level quieter.
public sealed record CloseRoleDeclarationRequest(
    long RoleId,
    long VersionId,
    string? Note);
