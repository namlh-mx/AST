using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Time;
using ErrorOr;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Modules.IAM;

// [Slice C2] Syncs the `function` catalog from IFunctionRegistry.
// Semantics: docs/design-function-catalog-sync.md. AUTOMATIC only ADDS (epoch) + UPDATES metadata (case 7);
// REMOVE/RESTORE only list candidates (admin confirms, future admin-screen phase). Does NOT close/open a period here ->
// no coverage shrinkage -> reverse-FK is not touched (qa Slice B condition #1 is honored by deferring the close operation).
internal sealed class FunctionCatalogSyncService(
    IFunctionRepository functions,
    IFunctionRegistry registry,
    IBusinessDateProvider dates) : IFunctionCatalogSync
{
    // Fixed epoch (C2, approved 2026-07-03): an open period starting very early so that a child declaring any period
    // is not mistakenly blocked by STRICT temporal-FK.
    private static readonly Period Epoch = new(new DateOnly(2000, 1, 1), Period.OpenEnd);
    private const string SyncActor = "system-sync";

    public async Task<ErrorOr<FunctionSyncReport>> SyncAsync()
    {
        var today = dates.Today;
        var scope = new DataScope(ScopeLevel.Global, null, SyncActor);

        var activeToday = await functions.GetInScopeAsync(scope, today);
        var activeByKey = activeToday
            .GroupBy(f => f.FunctionKey)
            .ToDictionary(g => g.Key, g => g.First());
        var knownKeys = (await functions.GetAllKnownFunctionKeysAsync()).ToHashSet();
        var desiredKeys = registry.All.Select(d => d.FunctionKey).ToHashSet();

        var created = new List<string>();
        var metadataUpdated = new List<string>();
        var reopenCandidates = new List<string>();

        foreach (var d in registry.All)
        {
            if (activeByKey.TryGetValue(d.FunctionKey, out var current))
            {
                if (!MetadataMatches(current, d))
                {
                    // Upserts into the EXACT period of the current active version (does not use the Epoch constant) -> always an EXACT
                    // MATCH (case 7) regardless of the period, even after an admin reopens the function on period [D, open] with D != 2000. Using
                    // the Epoch here would be a "superset" -> silently pulling effective_from back to 2000, rewriting the period's history.
                    var currentPeriod = new Period(current.EffectiveFrom, current.EffectiveTo);
                    var upsert = await functions.UpsertAsync(
                        current.FunctionId, currentPeriod, d.FunctionKey, d.BusinessCode, d.DisplayName,
                        d.MenuGroupCode, d.NavTarget, SyncActor, "metadata-sync");
                    if (upsert.IsError)
                    {
                        return upsert.Errors;
                    }

                    metadataUpdated.Add(d.FunctionKey);
                }
            }
            else if (knownKeys.Contains(d.FunctionKey))
            {
                // An old identity already exists (closed) but is not active -> re-add: informational only, admin reopens it (Option X).
                reopenCandidates.Add(d.FunctionKey);
            }
            else
            {
                var create = await functions.CreateAsync(
                    Epoch, d.FunctionKey, d.BusinessCode, d.DisplayName,
                    d.MenuGroupCode, d.NavTarget, SyncActor, "code-sync-new");
                if (create.IsError)
                {
                    return create.Errors;
                }

                // KeyAlreadyPresent states only what the repository OBSERVED under the lock: the key already
                // had a version. Do NOT read it as "another workstation just created it" — that is a cause,
                // and the outcome type deliberately refuses to claim one (see FunctionCreateOutcome). The
                // expected cause is indeed a co-starting machine, which is normal operation rather than an
                // incident; but backlog 0.6's collation mismatch reaches this same branch PERMANENTLY, and a
                // comment naming the benign cause would send whoever meets it hunting a race that is not there.
                // Either way the handling is the same: not an error (which would abandon the remaining
                // descriptors) and not a `created` entry (which would claim work this run did not do).
                if (create.Value is FunctionCreateOutcome.Created)
                {
                    created.Add(d.FunctionKey);
                }
            }
        }

        var removalCandidates = activeToday
            .Where(f => !desiredKeys.Contains(f.FunctionKey))
            .Select(f => f.FunctionKey)
            .ToList();

        return new FunctionSyncReport(created, metadataUpdated, removalCandidates, reopenCandidates);
    }

    private static bool MetadataMatches(FunctionVersionDto current, FunctionDescriptor desired) =>
        current.BusinessCode == desired.BusinessCode
        && current.DisplayName == desired.DisplayName
        && current.MenuGroup == desired.MenuGroupCode
        && current.NavTarget == desired.NavTarget;
}
