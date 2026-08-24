using AST.Core.Time;

namespace AST.Modules.IAM.Tests.TestSupport;

internal sealed class FixedBusinessDateProvider(DateOnly today) : IBusinessDateProvider
{
    public DateOnly Today => today;
}
