namespace AST.Core.EffectivePeriod;

public enum VersionOpKind { SoftDeactivate, Insert }

// 1 operation on the version table: deactivate the old version / insert a new one (including remnants).
// Invariant: CarriesOldBusinessData ⟺ SourceVersionId.HasValue.
public sealed record VersionOp(
    VersionOpKind Kind,
    long? ExistingVersionId,        // SoftDeactivate: id of the soft-deleted version
    EffectivePeriod Period,         // Insert: period of the inserted version (new period or remnant)
    bool CarriesOldBusinessData,    // true = remnant (keeps the old version's business data, only the period changes)
    long? SourceVersionId = null);  // Insert-remnant: id of the source version to COPY business columns from.
                                     // SoftDeactivate and Insert-new-period (no remnant) -> null.
