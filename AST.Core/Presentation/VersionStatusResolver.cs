namespace AST.Core.Presentation;

// Pure §2.7.3 status-label computation for a single version row: isactive/cancelled/dates -> the shared
// 4-state VersionStatus. VM-agnostic (works for any versioned-parameter row with the same shape), so it lives
// here next to VersionStatus/VersionStatusPresentation rather than inside a screen ViewModel.
[SharedComponent]
public static class VersionStatusResolver
{
    public static VersionStatus Resolve(bool isActive, bool cancelled, DateOnly effectiveFrom, DateOnly effectiveTo, DateOnly today)
    {
        if (!isActive)
        {
            // N6: cancelled is a DURABLE marker, never inferred from dates -- it always wins over "ended".
            // Relies on the data-model invariant cancelled=1 => isactive=0 (enforced by CancelVersionAsync);
            // cancelled is intentionally not checked outside this branch.
            return cancelled ? VersionStatus.Cancelled : VersionStatus.Expired;
        }

        if (effectiveTo < today)
        {
            return VersionStatus.Expired;
        }

        return effectiveFrom > today ? VersionStatus.Pending : VersionStatus.Effective;
    }
}
