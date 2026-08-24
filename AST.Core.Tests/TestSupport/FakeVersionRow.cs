using AST.Core.EffectivePeriod;

namespace AST.Core.Tests.TestSupport;

internal sealed record FakeVersionRow(
    long Id,
    long IdentityId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsActive = true) : IVersionRow;
