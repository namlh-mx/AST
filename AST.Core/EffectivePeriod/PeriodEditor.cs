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

        // Gap warning (D7): the nearest neighbour on each side of newPeriod, drawn from the coverage this
        // plan LEAVES BEHIND -- the untouched versions PLUS every period this plan inserts (head/tail
        // remnants and newPeriod itself). Reading `untouched` alone reported a gap the plan's own remnant
        // was about to fill: cutting a version that ABUTTED its neighbour yields a remnant landing exactly
        // in the reported "gap". For org-unit that is not a spurious warning, it is a refusal to write a
        // legal edit (GapIsBlocking). See spec 2026-08-22-orgunit-edit-close-code-reuse-shaping section 18.1.
        //
        // The scope stays deliberately narrow -- only the two boundaries this edit touches, never the whole
        // timeline. VersionedRepository.ComputeGapWarnings walks the WHOLE remaining coverage, which is
        // correct for a coverage-REDUCING write that only warns; applying it here would let a pre-existing
        // hole refuse every later edit of the same identity. The two questions are not the same question.
        //
        // On how such a hole ARISES, corrected 2026-08-26 after the first wording over-claimed it: Cancel
        // does not perforate a timeline, it HEALS one (CancelVersionCoreAsync extends the adjacent
        // predecessor over the cancelled range). DeleteVersionAsync does leave an interior hole and only
        // warns -- but it has NO production caller, so no screen can reach the state today. The scope
        // choice here bounds a shape the engine permits; it is not a response to a hole operators make.
        //
        // newPeriod is in this list and can never be SELECTED from it, because neither `p.To < newPeriod.From`
        // nor `p.From > newPeriod.To` can hold for newPeriod itself once From <= To -- which the guard at the
        // top of this method has already enforced. Loosening either filter to <= / >= would break that -- but
        // only for a SINGLE-DAY newPeriod (From == To), which would then satisfy its own filter, win the pick
        // and suppress that side's warning. A multi-day period is unaffected by the loosening, which is
        // exactly what would make such a defect hide for a long time.
        var resultingCoverage = untouched
            .Select(v => new EffectivePeriod(v.EffectiveFrom, v.EffectiveTo))
            .Concat(operations
                .Where(o => o.Kind == VersionOpKind.Insert)
                .Select(o => o.Period))
            .ToList();

        var before = resultingCoverage
            .Where(p => p.To < newPeriod.From)
            .OrderByDescending(p => p.To)
            .Cast<EffectivePeriod?>()
            .FirstOrDefault();
        if (before is { } left && EffectivePeriod.GapBetween(left.To, newPeriod.From) is { } gapBefore)
        {
            warnings.Add(gapBefore);
        }

        var after = resultingCoverage
            .Where(p => p.From > newPeriod.To)
            .OrderBy(p => p.From)
            .Cast<EffectivePeriod?>()
            .FirstOrDefault();
        if (after is { } right && EffectivePeriod.GapBetween(newPeriod.To, right.From) is { } gapAfter)
        {
            warnings.Add(gapAfter);
        }

        return new PeriodEditPlan(operations, warnings);
    }
}
