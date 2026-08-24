using AST.Core.Data;
using AST.Core.Iam.Repositories;
using ErrorOr;
// [Fix CS0118] The "EffectivePeriod" sub-namespace of "AST.Core" shares its name with the struct inside it ->
// lookup favors the namespace from a file also nested under "AST.Core". Alias with a different name, same
// convention as AST.Core/Iam/Repositories/IOrgUnitRepository.cs.
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam;

// Orchestration use-case for retiring an org unit: Close (retire) an org-unit version that started
// strictly before today, or Cancel-plan one whose own EffectiveFrom is today or later (D1, 2026-08-10
// — "today or later" is deliberate: a version that has not completed a single effective day is
// cancellable even though it is displayed as effective today), together with its `audit_log` row,
// inside ONE CompositeWrite transaction.
//
// This contract exists because the close/cancel guards must not be bypassable. Before it, the only
// caller (the declaration screen) held those guards itself and picked the close-vs-cancel branch
// itself, so any other caller of the repository got none of them and the org unit's retirement went
// unrecorded. Four properties are therefore load-bearing here and must not be relaxed by an
// implementation:
//   - WHICH operation runs (retire vs cancel-plan) is derived server-side from the target version's
//     own EffectiveFrom vs. business "today" — the caller cannot pick it,
//   - the close-date rules are the shared VersionCloseRules (docs/shared-components.md §⑥), never
//     re-implemented here,
//   - the actor and the authorization scope are derived server-side (ICurrentWindowsUser /
//     IAuthorizationService), so the request DTO deliberately carries neither,
//   - the version write and its `audit_log` row share one transaction: either both land or neither.
//
// Scope is close/cancel, Add AND Edit — every org-unit version write. Edit joined the other two on
// 2026-08-21 (backlog 0.7, requester ruling): the parent of an org unit is IMMUTABLE after creation,
// and a guard that lives in the calling ViewModel is not a guard. Edit also carries its own
// authorization and scope check, which was bypassable for exactly as long as it lived on the screen.
// IOrgUnitRepository therefore declares NO write member at all; AST.Meta.Tests asserts that
// mechanically.
//
// Implementation: AST.Modules.IAM/OrgUnitDeclarationService.cs (internal — it depends on the CONCRETE
// OrgUnitRepository class for its composite-write overloads, which are NOT on the public
// IOrgUnitRepository interface by design; same posture as the role side).
[SharedComponent]
public interface IOrgUnitDeclarationService
{
    Task<ErrorOr<UpsertResult>> CloseOrgUnitDeclarationAsync(CloseOrgUnitDeclarationRequest request);

    // Second use-case (backlog 0.4b, 2026-08-17): declare a BRAND-NEW org unit — mint its identity and
    // write its first version inside ONE CompositeWrite, with its audit_log row on the same transaction.
    // A failure at any step leaves NO row: there is no compensating delete to forget or to fail.
    //
    // Three properties are load-bearing here and must not be relaxed by an implementation:
    //   - the actor and the authorization scope are derived server-side (ICurrentWindowsUser /
    //     IAuthorizationService), so the request DTO deliberately carries neither,
    //   - Add requires ScopeLevel.Global. A narrower scope has no notion of "which unit to attach under"
    //     that IsWithinScopeAsync could check the way Edit/Close do, so requiring Global outright is the
    //     only sound gate,
    //   - root uniqueness is by OVERLAPPING PERIOD, not by "a root exists today" (requester ruling
    //     2026-08-17): at most one root org unit on any given day, so a retired root MAY be succeeded by
    //     a new one. The probe reads every ACTIVE root regardless of date and regardless of the actor's
    //     scope — a future-dated root counts, and an operator who cannot see the existing root still
    //     cannot create a second one.
    //
    // The caller does not supply VersionOperationKind: it is Add by construction, so it cannot be
    // mislabelled from outside.
    Task<ErrorOr<AddOrgUnitDeclarationResult>> AddOrgUnitDeclarationAsync(AddOrgUnitDeclarationRequest request);

    // Third use-case (backlog 0.7, 2026-08-21): edit an EXISTING org unit's business fields.
    //
    // The load-bearing property, and the reason this method exists at all: a new parent is
    // UNEXPRESSIBLE, not rejected. EditOrgUnitDeclarationRequest carries no "desired parent" field, so
    // there is no rejection branch a caller can probe and no way to ask for a re-parenting in the first
    // place. The value written is the one this service reads back under the identity lock — never a
    // value the caller supplied.
    //
    // Two more, same posture as the two methods above:
    //   - the actor and the authorization scope are derived server-side (ICurrentWindowsUser /
    //     IAuthorizationService), so the request DTO deliberately carries neither,
    //   - the caller does not supply VersionOperationKind: it is Edit by construction.
    Task<ErrorOr<UpsertResult>> EditOrgUnitDeclarationAsync(EditOrgUnitDeclarationRequest request);
}

// `Period` is caller-supplied: `org_unit` is a Declared entity (docs/design-temporality-classes.md), so
// unlike the Immediate role/permission side a start other than today is legal and must not be derived.
//
// No actor and no scope field, and no operation kind — all derived server-side, so a caller cannot
// self-assert either its authority or what kind of write this is.
public sealed record AddOrgUnitDeclarationRequest(
    Period Period,
    string OrgCode,
    string OrgNameFullVn,
    string OrgNameShortVn,
    long? ParentId,
    string? Reason,
    OrgUnitSupplementalDto? Supplemental);

// `OrgUnitId` is the identity the service minted — the caller cannot know it in advance and needs it to
// reload the record it just declared.
public sealed record AddOrgUnitDeclarationResult(long OrgUnitId, UpsertResult Write);

// NO desired-parent field: re-parenting is unexpressible, which is a stronger guarantee than rejecting it.
//
// `ExpectedParentId` and `ExpectedVersionId` are what the caller READ, echoed back. They are not the
// caller asserting a parent: the service pre-enlists `ExpectedParentId` so the composite's up-front lock
// batch covers it (locks are acquired before the transaction body runs -- a parent first learned INSIDE
// the transaction was never enlisted and the write fails CompositeWrite.NotEnlisted).
//
// WHERE EACH ECHO IS ACTUALLY CHECKED -- stated precisely because an earlier version of this comment said
// "verifies both under the identity lock" and only one of them is:
//   - `ExpectedParentId` is re-read and verified INSIDE the enlisted transaction -> OrgUnit.ParentMismatch.
//   - `ExpectedVersionId` is checked BEFORE the composite opens, against the same server-side history read
//     the close path uses. That leaves the same VERSION-read TOCTOU the close path documents and accepts:
//     a concurrent writer can supersede the version between the check and the lock, and the engine's own
//     failure surfaces instead. Accepted, not fixed -- fail-clear, no corruption.
// Same caller-declares / server-verifies contract as the role side's
// RoleSaveTarget.ExistingRole(RoleId, ExpectedRoleVersionId, ExpectedRoleCode).
//
// `ExpectedParentId` is nullable because null is a real value here -- "I read this unit as a root" -- not
// because it is optional. It is a required positional parameter: a correctness-carrying parameter ships
// required, never with a default.
//
// `Period` is caller-supplied for the same reason it is on Add: `org_unit` is a Declared entity
// (docs/design-temporality-classes.md), so a start other than today is legal and must not be derived.
//
// No actor and no scope field, and no operation kind -- all derived server-side.
public sealed record EditOrgUnitDeclarationRequest(
    long OrgUnitId,
    long ExpectedVersionId,
    long? ExpectedParentId,
    Period Period,
    string OrgCode,
    string OrgNameFullVn,
    string OrgNameShortVn,
    string? Reason,
    OrgUnitSupplementalDto? Supplemental);

// `OrgUnitId`+`VersionId` identify the exact version to close/cancel — read back server-side rather
// than trusted to be "the currently active one" implicitly.
//
// `EffectiveThrough` applies ONLY to the retire branch: required there, and required to be null on the
// cancel-plan branch. A caller supplying a cut date for a plan that has no valid cut-date concept is a
// caller bug, reported as such rather than silently coerced.
//
// `Note` is optional, matching the role side: the actor is recorded in `audit_log` regardless, so a
// missing note never leaves a retirement unattributed.
//
// No actor and no scope field: both are derived server-side, so a caller cannot self-assert either.
public sealed record CloseOrgUnitDeclarationRequest(
    long OrgUnitId,
    long VersionId,
    DateOnly? EffectiveThrough,
    string? Note);
