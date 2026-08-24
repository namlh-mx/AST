using AST.Core.Time;

namespace AST.Modules.IAM.Tests.TestSupport;

// Simulates a midnight rollover BETWEEN two reads of "today" within a SINGLE business operation: the
// FIRST call to `.Today` (whichever consumer reads first) returns `start`; every call after that
// returns `start.AddDays(1)`. Exists to discriminate exactly the defect design-effective-period.md §3
// forbids -- "a single business operation captures D (and 'today') ONCE, used consistently for every
// parameter within that operation" -- which the pre-fix cancel path violated:
// RoleDeclarationService/OrgUnitDeclarationService captured `dates.Today` once to derive the
// Retire-vs-CancelPlan branch, then VersionedRepository.CancelVersionCoreAsync independently re-read
// its OWN injected IBusinessDateProvider for the cancel-eligibility guard. Sharing ONE instance of this
// double between the service and the repository under test reproduces that exact double-read shape.
internal sealed class AdvancingBusinessDateProvider(DateOnly start) : IBusinessDateProvider
{
    private bool _read;

    public DateOnly Today
    {
        get
        {
            if (!_read)
            {
                _read = true;
                return start;
            }

            return start.AddDays(1);
        }
    }
}
