namespace AST.Core.EffectivePeriod;

public sealed record PeriodEditPlan(
    IReadOnlyList<VersionOp> Operations,
    IReadOnlyList<GapWarning> Warnings);
