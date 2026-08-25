namespace AST.Core.Presentation;

// The shared version-lifecycle vocabulary for every versioned-parameter UI (org unit / role / user / …).
// Mirrors the effective-period model: Cancelled = a plan closed before it completed a single effective
// day (isactive=0 AND status='cancelled', N6) — told apart from a naturally-ended version by the durable
// lifecycle marker (VersionLifecycleStatus, persisted in the version row's `status` column since V010),
// never by display-time inference. Replaced carries the same shape for a version retired by a replacement
// gesture: also isactive=0, told apart only by its durable marker. Cancel eligibility (D1, 2026-08-10) covers both a
// still-"Chờ hiệu lực" plan (From > today) AND a same-day version already labelled "Hiệu lực" (From ==
// today) — the label is unaffected (VersionStatusResolver keeps labelling From == today as Effective;
// cancellability is a server-side rule, not a display fact), so a version can show Effective right up
// to the moment it is cancelled and then show Cancelled. None = card empty (label hidden). The VM
// computes this from the version row + business today; the label control only displays it.
[SharedComponent]
public enum VersionStatus
{
    None = 0,
    Cancelled,  // Bị hủy
    Replaced,   // Bị thay thế
    Expired,    // Hết hiệu lực
    Effective,  // Hiệu lực
    Pending,    // Chờ hiệu lực
}
