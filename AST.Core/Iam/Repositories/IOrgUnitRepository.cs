using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Presentation;
using AST.Core.Time;
using ErrorOr;
// [Fix CS0118] The "EffectivePeriod" sub-namespace of "AST.Core" shares its name with the struct inside it ->
// lookup favors the namespace when this file (AST.Core.Iam.Repositories) is also nested under "AST.Core". Alias with a different name.
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam.Repositories;

public interface IOrgUnitRepository
{
    // NO identity mint and NO compensating delete on this interface (2026-08-17, backlog 0.4b). Org-unit
    // identity creation goes only through IOrgUnitDeclarationService.AddOrgUnitDeclarationAsync, which mints
    // inside the same transaction as the first version (design-effective-period.md §7) -- so a zero-version
    // header is not a state this interface can produce, and there is nothing for it to compensate.
    // WIDENED 2026-08-21 (backlog 0.7): UpsertAsync is gone from here too. EVERY org-unit version write goes
    // through IOrgUnitDeclarationService IN PRODUCTION -- CloseVersionAsync/DeleteVersionAsync/CancelPlanAsync
    // below are the primitives that service drives, and they remain CALLABLE by anyone holding this interface.
    // The only production holder is OrgUnitDeclarationViewModel, which calls no writer on it (verified
    // 2026-08-21). Say "no production caller outside the service", NOT "no write member": the earlier wording
    // was an over-claim -- the root-close rule and its audit rows live in the SERVICE, so a
    // direct caller of the members below skips them.
    // AST.Meta.Tests/OrgUnitWritePathAbsenceTests guards the convention; its own header is the single home
    // for what those legs do and do not prove.

    Task<IReadOnlyList<OrgUnitVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf);

    // Resolves directly from the DB (D9): no coverage at asOf -> Error.NotFound.
    Task<ErrorOr<OrgUnitVersionDto>> GetByIdentityAsync(long orgUnitId, DateOnly asOf);

    // Full timeline of identities IN `scope` — every version ever recorded (active, inactive,
    // cancelled alike), NO isactive/period filter (rule-effective-period invariant #2 does NOT
    // apply to this read by design — history is a raw audit trail, not "usable at date D"; a
    // caller must not add such a filter to "fix" this). Unlike GetInScopeAsync/GetByIdentityAsync
    // (which resolve "as of a date", isactive=1 only), this is the history-grid read (Phase 4d).
    // `orgUnitId` null = every identity in scope ("Hiển tất cả"); non-null = that identity only.
    // The scope filter is applied SERVER-side, so an out-of-scope id returns empty -- no
    // caller-side gate is needed. Ordered by RecordedAt (the audit timestamp of when the action
    // was performed) descending, Id as a deterministic tiebreaker. Throws InvalidOperationException
    // for ScopeLevel.Self (org_unit_version has no owner column), same as every other read on this
    // interface. Does NOT call AuthorizeAsync itself -- callers pass an already-resolved DataScope
    // (spec 2.7.9: reads do not re-authorize).
    Task<IReadOnlyList<OrgUnitVersionDto>> GetHistoryInScopeAsync(DataScope scope, long? orgUnitId = null);

    // N2: eligible parents for a child effective-period — active versions must CONTINUOUSLY cover the whole
    // `childPeriod` (no gap). Feeds AstOrgUnitPicker.Items directly; the picker itself does no filtering.
    Task<IReadOnlyList<OrgUnitPickerItem>> GetEligibleParentsAsync(DataScope scope, Period childPeriod);

    // Cut/close the period: shrinks effective_to of version `versionId` down to `newTo`. Reverse-FK (D8) BLOCKS if a
    // child (a sub-org-unit or a user belonging to this org unit) would lose coverage; a gap warning is returned alongside.
    // `operationDate`: the caller-captured business date for this operation, same contract as
    // CancelPlanAsync's below. `org_unit` has no close-date policy of its own — it carries the date
    // because the base engine's close path takes it uniformly for every entity, and the entities that
    // DO have a policy (role/permission, `Immediate`) must not be able to re-read a clock instead.
    Task<ErrorOr<UpsertResult>> CloseVersionAsync(
        long orgUnitId, long versionId, DateOnly newTo, OperationDate operationDate,
        string recordedBy, string? reason);

    // Delete 1 period: soft-deletes version `versionId`. The original version must still exist (blocked if it is the last active version);
    // reverse-FK BLOCKS if a child would lose coverage.
    Task<ErrorOr<UpsertResult>> DeleteVersionAsync(long orgUnitId, long versionId);

    // Cancels a version that has NOT completed a single effective day (N6, requester decision D1
    // 2026-08-10): isactive=0 AND cancelled=1. `operationDate` is the CALLER-supplied operation date
    // (design-effective-period.md §3 -- captured ONCE by the caller for this whole operation, e.g.
    // IOrgUnitDeclarationService's derived-branch date; TASK 0, 2026-08-11), NOT re-read from any
    // engine-internal clock -- the boundary is the caller-supplied operation date, not "business
    // today" resolved independently inside this method. BLOCKS (Validation,
    // VersionedRepository.NotAFuturePlan) when EffectiveFrom is strictly before `operationDate` --
    // such a version must be retired via CloseVersionAsync instead. Reverse-FK (D8) BLOCKS if a child
    // (a sub-org-unit or a user belonging to this org unit) would lose coverage as a result; a gap
    // warning is returned alongside.
    Task<ErrorOr<UpsertResult>> CancelPlanAsync(
        long orgUnitId, long versionId, DateOnly operationDate, string recordedBy, string reason);

    // H2 (N9): the "affected versions" preview for a warn-before-save UI check. Returns the isactive=1
    // versions of `orgUnitId` whose period OVERLAPS `period` -- empty means a clean append (no cut/remnant
    // will happen); non-empty means the save will soft-deactivate/split at least one existing version.
    // Read-only; does not write.
    Task<IReadOnlyList<OrgUnitVersionDto>> PreviewUpsertAsync(long orgUnitId, Period period);

    // Scope-checked-write membership primitive (2026-08-05 security fix, decision-log): does `orgUnitId`
    // fall within `scope` at ANY point in its FULL version history (active, inactive, cancelled, past,
    // present, or future alike) -- NOT just "as of today". Mirrors GetHistoryInScopeAsync's own scope
    // predicate (a unit being edited/closed may be entirely past- or future-dated, spec 2.7.6), so a
    // caller must gate a write (edit/close) on this returning true before ever reaching
    // CloseVersionAsync/DeleteVersionAsync/CancelPlanAsync -- those methods do NOT re-check scope
    // themselves (spec 2.7.9 read/write split still applies: this call takes an already-resolved
    // DataScope, it does not call AuthorizeAsync). Throws InvalidOperationException for ScopeLevel.Self,
    // same as GetHistoryInScopeAsync (org_unit_version has no owner column).
    Task<bool> IsWithinScopeAsync(DataScope scope, long orgUnitId);
}
