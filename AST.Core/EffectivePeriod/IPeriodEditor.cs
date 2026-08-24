using ErrorOr;

namespace AST.Core.EffectivePeriod;

[SharedComponent]
public interface IPeriodEditor
{
    // Computes the cut/split/insert plan per the 8-case algebra for 1 identity (does NOT touch the DB).
    // Invalid input (From>To...) -> Error.Validation. Day gaps -> Warnings (not an Error).
    // The 9999-12-31 boundary = "infinity": no b+1/T+1 arithmetic at the boundary (§4).
    ErrorOr<PeriodEditPlan> PlanUpsert(
        IReadOnlyList<IVersionRow> activeVersions,   // the identity's currently active periods
        EffectivePeriod newPeriod);
}
