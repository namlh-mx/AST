namespace AST.Core.Data;

// What the engine did to one exclusively-owned dependent while its parent was narrowed or stopped.
//   Shrunk    — the dependent kept its earlier coverage and got a remnant ending at CutTo.
//   Cancelled — the dependent had not completed a single effective day and lost all coverage, so it
//               was cancelled outright (B1, 2026-08-15). No remnant exists for it.
public enum AutoCutAction { Shrunk, Cancelled }

// One reported auto-cut effect. The engine owns the coverage arithmetic; a service that must
// journal the effect reads this instead of re-deriving it — there is exactly one home for
// "what happened to that dependent".
public sealed record AutoCutOutcome(
    string DependentVersionTable,
    long DependentIdentityId,
    long SourceVersionId,
    AutoCutAction Action,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    DateOnly? CutTo);
