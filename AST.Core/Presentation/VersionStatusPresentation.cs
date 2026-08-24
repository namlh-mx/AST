namespace AST.Core.Presentation;

// Single home of the version-status → VN label + brush-key mapping (mirrors StatusSeverityPresentation). The
// View resolves the brush KEY to a themed resource, keeping the kernel free of any System.Windows reference.
// Labels are product UI copy (the one allowed Vietnamese); brush keys signed off 2026-07-25.
[SharedComponent]
public static class VersionStatusPresentation
{
    // No dates in the label — the effective-period block already shows the dates (§2.7.3).
    public static string DisplayText(VersionStatus status) => status switch
    {
        VersionStatus.Cancelled => "Bị hủy",
        VersionStatus.Expired => "Hết hiệu lực",
        VersionStatus.Effective => "Hiệu lực",
        VersionStatus.Pending => "Chờ hiệu lực",
        _ => string.Empty,
    };

    // Requester-confirmed 2026-07-25: effective = green, pending = accent/blue (future), expired = muted,
    // cancelled = red (a discarded plan), none = muted. Change here if the requester picks different colours.
    public static string BrushKey(VersionStatus status) => status switch
    {
        VersionStatus.Effective => "AstSuccessBrush",
        VersionStatus.Pending => "AstAccentLinkBrush",
        VersionStatus.Cancelled => "AstErrorBrush",
        _ => "AstTextSecondaryBrush",
    };
}
