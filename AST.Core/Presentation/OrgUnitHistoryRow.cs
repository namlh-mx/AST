using AST.Core.Iam.Repositories;

namespace AST.Core.Presentation;

// One row of Screen A's history grid. Lives in AST.Core (not AST.Shell) for the same View+VM-reachability
// reason as OrgUnitTreeNode/OrgUnitPickerItem (relocated 2026-07-31, Phase 4d Task 3a: was a View-local
// placeholder type in OrgUnitDeclarationView.xaml.cs before the VM gained a real history-load path).
// Renamed in Phase 4d Task 3b once real history data replaced the former sample-row placeholder type.
//
// Id/EffectiveTo/ParentId/Supplemental added 2026-08-10 (defect A): the History "Xem" flow reads a
// SPECIFIC row's own version, not "whatever is effective at some date" -- so the row has to carry
// every field OrgUnitDeclarationViewModel's card shows, letting LoadFromHistoryRow populate the card
// with no repository round trip (and no EffectivePeriodResolver date-coverage requirement, which is
// exactly what made a lapsed identity unviewable). FromText/ToText/StatusText/ParentLabel/Operation
// stay pre-formatted display strings; the 4 new fields are the raw values the card's editable
// properties need (OrgCode/NameFull/NameShort double as both, no duplication needed there).
[SharedComponent]
public sealed record OrgUnitHistoryRow(
    long Id,
    long OrgUnitId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    string FromText,
    string ToText,
    string RecordedAtText,
    string StatusText,
    VersionStatus Status,
    string OrgCode,
    string NameFull,
    string NameShort,
    long? ParentId,
    string ParentLabel,
    string Operation,
    string RecordedBy,
    string Reason,
    OrgUnitSupplementalDto Supplemental);
