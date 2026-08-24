using ErrorOr;

namespace AST.Core.EffectivePeriod;

// 8-case algebra (§4, docs/design-effective-period.md) — pure, does not touch the DB.
public sealed class PeriodEditor : IPeriodEditor
{
    public ErrorOr<PeriodEditPlan> PlanUpsert(
        IReadOnlyList<IVersionRow> activeVersions,
        EffectivePeriod newPeriod)
    {
        if (newPeriod.From > newPeriod.To)
        {
            return Error.Validation(
                "EffectivePeriod.InvalidRange",
                "Kỳ hiệu lực không hợp lệ: effective_from phải <= effective_to.");
        }

        var sorted = activeVersions
            .Where(v => v.IsActive)
            .OrderBy(v => v.EffectiveFrom)
            .ToList();

        var operations = new List<VersionOp>();
        var warnings = new List<GapWarning>();
        var untouched = new List<IVersionRow>();

        foreach (var existing in sorted)
        {
            var existingPeriod = new EffectivePeriod(existing.EffectiveFrom, existing.EffectiveTo);

            if (!existingPeriod.Overlaps(newPeriod))
            {
                // Disjoint (case 1) or adjacent (case 2): the old version is untouched.
                untouched.Add(existing);
                continue;
            }

            // Overlapping => soft-delete the old version + head/tail remnant if any (case 3-7, repeats for case 8).
            operations.Add(new VersionOp(
                VersionOpKind.SoftDeactivate,
                existing.Id,
                existingPeriod,
                CarriesOldBusinessData: false));

            if (existingPeriod.From < newPeriod.From)
            {
                var headEnd = EffectivePeriod.PreviousDay(newPeriod.From)!.Value;
                operations.Add(new VersionOp(
                    VersionOpKind.Insert,
                    null,
                    new EffectivePeriod(existingPeriod.From, headEnd),
                    CarriesOldBusinessData: true,
                    SourceVersionId: existing.Id));
            }

            if (newPeriod.To < existingPeriod.To)
            {
                var tailStart = EffectivePeriod.NextDay(newPeriod.To)!.Value;
                operations.Add(new VersionOp(
                    VersionOpKind.Insert,
                    null,
                    new EffectivePeriod(tailStart, existingPeriod.To),
                    CarriesOldBusinessData: true,
                    SourceVersionId: existing.Id));
            }
        }

        operations.Add(new VersionOp(
            VersionOpKind.Insert,
            null,
            newPeriod,
            CarriesOldBusinessData: false));

        // Gap warning (D7): only consider the 2 nearest untouched neighbors on both sides of newPeriod.
        var before = untouched
            .Where(v => v.EffectiveTo < newPeriod.From)
            .OrderByDescending(v => v.EffectiveTo)
            .FirstOrDefault();
        if (before is not null && EffectivePeriod.GapBetween(before.EffectiveTo, newPeriod.From) is { } gapBefore)
        {
            warnings.Add(gapBefore);
        }

        var after = untouched
            .Where(v => v.EffectiveFrom > newPeriod.To)
            .OrderBy(v => v.EffectiveFrom)
            .FirstOrDefault();
        if (after is not null && EffectivePeriod.GapBetween(newPeriod.To, after.EffectiveFrom) is { } gapAfter)
        {
            warnings.Add(gapAfter);
        }

        return new PeriodEditPlan(operations, warnings);
    }
}
