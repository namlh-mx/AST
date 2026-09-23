using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Presentation;
using ErrorOr;
// [Fix CS0118] The "EffectivePeriod" sub-namespace of "AST.Core" shares its name with the struct inside it ->
// lookup favors the namespace when this file (AST.Core.Iam.Repositories) is also nested under "AST.Core". Alias with a different name.
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Iam.Repositories;

public interface IOrgUnitRepository
{
    // NO identity mint and NO compensating delete on this interface (2026-08-17, backlog 0.4b). Org-unit
    // identity creation goes only through IOrgUnitDeclarationService, which mints inside the same transaction
    // as the first version (design-effective-period.md §7) -- so a zero-version header is not a state this
    // interface can produce, and there is nothing for it to compensate.
    // WIDENED 2026-09-04: AddOrgUnitDeclarationAsync is no longer the ONLY mint path.
    // ReplaceOrgUnitDeclarationAsync mints the SUCCESSOR identity the same way, inside the same transaction as
    // its first version. The property above is unchanged -- both mints are on that one service -- but a reader
    // must not infer "created" from operation_kind = 'Add': a replacement successor has no Add row anywhere in
    // its history, and its creation date is the date of its FIRST row, whatever kind that row carries.
    // WIDENED 2026-08-21: UpsertAsync is gone from here too. IOrgUnitRepository declares no
    // version writer. Close and Cancel go only through OrgUnitDeclarationService; there is no Delete
    // business operation. The concrete writers remain on OrgUnitRepository and VersionedRepository<TVersion>.
    // AST.Meta.Tests/OrgUnitWritePathAbsenceTests guards that boundary by the called member and its type;
    // its header is what that guard does and does not prove.

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
    // `excludedSubtreeRootId` excludes that identity and every identity below it; null preserves Add's
    // existing candidate universe because a new declaration has no identity of its own to exclude.
    Task<IReadOnlyList<OrgUnitPickerItem>> GetEligibleParentsAsync(
        DataScope scope, Period childPeriod, long? excludedSubtreeRootId = null);

    // H2 (N9): the "affected versions" preview for a warn-before-save UI check. Returns the isactive=1
    // versions of `orgUnitId` whose period OVERLAPS `period` -- empty means a clean append (no cut/remnant
    // will happen); non-empty means the save will soft-deactivate/split at least one existing version.
    // Read-only; does not write.
    Task<IReadOnlyList<OrgUnitVersionDto>> PreviewUpsertAsync(long orgUnitId, Period period);

    // Scope-checked-write membership primitive (2026-08-05 security fix, decision-log): does `orgUnitId`
    // fall within `scope` at ANY point in its FULL version history (active, inactive, cancelled, past,
    // present, or future alike) -- NOT just "as of today". Mirrors GetHistoryInScopeAsync's own scope
    // predicate (a unit being edited/closed may be entirely past- or future-dated, spec 2.7.6), so a
    // caller must gate a write (edit/close) on this returning true before the service reaches a concrete
    // writer -- those writers do NOT re-check scope themselves (spec 2.7.9 read/write split still applies:
    // this call takes an already-resolved
    // DataScope, it does not call AuthorizeAsync). Throws InvalidOperationException for ScopeLevel.Self,
    // same as GetHistoryInScopeAsync (org_unit_version has no owner column).
    Task<bool> IsWithinScopeAsync(DataScope scope, long orgUnitId);
}
