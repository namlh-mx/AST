using ErrorOr;

namespace AST.Core.EffectivePeriod;

[SharedComponent]
public interface IEffectivePeriodResolver
{
    // Picks the usable version at asOf (isactive=1 AND From<=asOf<=To) among a candidate set
    // for THE SAME identity. None found -> Error.NotFound (D9: caller STOPS + reports clearly).
    // MORE than one -> Error.Conflict: D6 (no overlapping active versions) has been breached and the answer would
    // otherwise depend on row order. Never resolves ambiguity by picking one.
    ErrorOr<TVersion> ResolveAt<TVersion>(IReadOnlyList<TVersion> candidates, DateOnly asOf)
        where TVersion : IVersionRow;
    // Conventional error codes: Error.NotFound("EffectivePeriod.NoCoverage",
    //   "Tham số '{name}' chưa có giá trị hiệu lực tại ngày {asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
    //   Error.Conflict("EffectivePeriod.OverlappingVersions", ...).
}
