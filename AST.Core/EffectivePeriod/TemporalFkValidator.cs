using System.Data;
using System.Globalization;
using ErrorOr;

namespace AST.Core.EffectivePeriod;

// STRICT temporal-FK (§5, D8) — pure, computes over coverage injected via a provider
// (see the assumption notes on IParentCoverageProvider/IDependentCoverageProvider).
public sealed class TemporalFkValidator(
    ITemporalFkRegistry registry,
    IParentCoverageProvider parentCoverageProvider,
    IDependentCoverageProvider dependentCoverageProvider) : ITemporalFkValidator
{
    public ErrorOr<Success> ValidateChildCoverage(
        string childVersionTable,
        IReadOnlyDictionary<string, long> parentIdentityIdsByColumn,
        EffectivePeriod childPeriod,
        IDbTransaction? ambientTransaction)
    {
        foreach (var edge in registry.EdgesForChild(childVersionTable))
        {
            if (!parentIdentityIdsByColumn.TryGetValue(edge.ChildParentColumn, out var parentIdentityId))
            {
                // The FK column was not provided (e.g. parent_id NULL for a root org unit) => this edge is exempt from checking.
                continue;
            }

            var coverage = parentCoverageProvider.GetActiveCoverage(edge.ParentVersionTable, parentIdentityId, ambientTransaction);
            if (CoverageGap.TryFind(coverage, childPeriod, out var gap))
            {
                return Error.Failure(
                    "TemporalFk.ParentGap",
                    $"Tham số cha '{edge.ParentVersionTable}' chưa khai báo hiệu lực cho khoảng " +
                    $"[{gap.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}-{gap.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}]");
            }
        }

        return Result.Success;
    }

    public ErrorOr<Success> ValidateParentChange(
        string parentVersionTable,
        long parentIdentityId,
        IReadOnlyList<EffectivePeriod> remainingParentCoverage,
        IDbTransaction? ambientTransaction)
    {
        var uncoveredCount = 0;
        EffectivePeriod? firstUncovered = null;

        foreach (var edge in registry.EdgesForParent(parentVersionTable))
        {
            foreach (var dependentPeriod in dependentCoverageProvider.GetDependentPeriods(edge, parentIdentityId, ambientTransaction))
            {
                if (!CoverageGap.TryFind(remainingParentCoverage, dependentPeriod, out var gap))
                {
                    continue;
                }

                uncoveredCount++;
                firstUncovered ??= gap;
            }
        }

        if (uncoveredCount > 0)
        {
            return Error.Failure(
                "TemporalFk.DependentsUncovered",
                $"{uncoveredCount} tham số con phụ thuộc khoảng " +
                $"[{firstUncovered!.Value.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}-{firstUncovered!.Value.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}]");
        }

        return Result.Success;
    }
}
