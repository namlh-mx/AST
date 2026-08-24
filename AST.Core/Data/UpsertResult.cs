using AST.Core.EffectivePeriod;

namespace AST.Core.Data;

// §3.6 — result of VersionedRepository's UpsertVersionAsync (8-case algebra + temporal-FK).
// AutoCutOutcomes — what P11 did to this parent's exclusively-owned dependents during this write
// (empty for every write that touched none). Required, never defaulted: a caller that must journal
// the cascade would otherwise silently journal nothing.
public sealed record UpsertResult(
    long NewVersionId,
    IReadOnlyList<GapWarning> Warnings,
    IReadOnlyList<AutoCutOutcome> AutoCutOutcomes);
