using AST.Core.Data;

namespace AST.Core.Presentation;

// Pure §2.7.3 status-label computation for a single version row: isactive/status/dates -> the shared
// VersionStatus. VM-agnostic (works for any versioned-parameter row with the same shape), so it lives
// here next to VersionStatus/VersionStatusPresentation rather than inside a screen ViewModel.
[SharedComponent]
public static class VersionStatusResolver
{
    public static VersionStatus Resolve(
        bool isActive, VersionLifecycleStatus status,
        DateOnly effectiveFrom, DateOnly effectiveTo, DateOnly today)
    {
        if (!isActive)
        {
            // N6: the durable lifecycle marker is never inferred from dates -- it always wins over
            // "ended". It wins only INSIDE this branch: an isactive = 1 row carrying a marker would fall
            // through to the date arms below and be labelled Effective. That row cannot exist, because
            // V010's CHECKs forbid it -- chk_ouv_status admits `cancelled|replaced` only with
            // isactive = 0, and chk_rv_status / chk_rpv_status do not admit `replaced` AT ALL
            // (replacement is org-unit-only in v1). So the guarantee is the database's, not this
            // function's, and a fake DTO built by hand in a test is outside it.
            return status switch
            {
                VersionLifecycleStatus.Cancelled => VersionStatus.Cancelled,
                VersionLifecycleStatus.Replaced => VersionStatus.Replaced,
                _ => VersionStatus.Expired,
            };
        }

        if (effectiveTo < today)
        {
            return VersionStatus.Expired;
        }

        return effectiveFrom > today ? VersionStatus.Pending : VersionStatus.Effective;
    }
}
