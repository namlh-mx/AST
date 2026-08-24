namespace AST.Core.Presentation;

// One candidate row for AstOrgUnitPicker. For org-unit parents, IOrgUnitRepository.GetEligibleParentsAsync
// produces this ready-to-bind list directly (N2 filtering + "{org_code} — {org_name_short_vn}" Display
// formatting both happen there, since the repository already holds the coverage data needed for N2 — no
// second round-trip to reformat identically in the VM). The VM's job is only to pass the result to
// AstOrgUnitPicker.Items; the picker itself still neither filters nor queries.

[SharedComponent]
public sealed record OrgUnitPickerItem(long Id, string Display);
