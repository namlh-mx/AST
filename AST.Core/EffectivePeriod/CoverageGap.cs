namespace AST.Core.EffectivePeriod;

// Shared "does this coverage list continuously cover the target period" algebra (D8/N2). Extracted from
// TemporalFkValidator so the org-unit-picker eligibility read (N2) reuses the exact same gap-detection logic
// instead of re-deriving it — the two callers must never diverge on what "continuous coverage" means.
[SharedComponent]
public static class CoverageGap
{
    public static bool TryFind(IReadOnlyList<EffectivePeriod> coverage, EffectivePeriod target, out EffectivePeriod gap)
    {
        var relevant = coverage
            .Where(p => p.Overlaps(target))
            .OrderBy(p => p.From)
            .ToList();

        DateOnly? cursor = target.From;
        foreach (var period in relevant)
        {
            if (cursor is null)
            {
                break;
            }

            if (period.From > cursor.Value)
            {
                gap = new EffectivePeriod(cursor.Value, EffectivePeriod.PreviousDay(period.From)!.Value);
                return true;
            }

            if (period.To >= target.To)
            {
                cursor = null;
                break;
            }

            cursor = EffectivePeriod.NextDay(period.To);
        }

        if (cursor is not null)
        {
            gap = new EffectivePeriod(cursor.Value, target.To);
            return true;
        }

        gap = default;
        return false;
    }
}
