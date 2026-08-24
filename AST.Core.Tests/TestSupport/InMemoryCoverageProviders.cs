using System.Data;
using AST.Core.EffectivePeriod;
using EP = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Tests.TestSupport;

// Pure "fake" (in-memory) for ITemporalFkValidator — replaces the DB-reading implementation (Slice B).
internal sealed class InMemoryParentCoverageProvider : IParentCoverageProvider
{
    private readonly Dictionary<(string Table, long IdentityId), List<EP>> _coverage = [];

    public InMemoryParentCoverageProvider Add(string parentVersionTable, long parentIdentityId, EP period)
    {
        var key = (parentVersionTable, parentIdentityId);
        if (!_coverage.TryGetValue(key, out var list))
        {
            list = [];
            _coverage[key] = list;
        }

        list.Add(period);
        return this;
    }

    public IReadOnlyList<EP> GetActiveCoverage(string parentVersionTable, long parentIdentityId, IDbTransaction? ambientTransaction) =>
        _coverage.TryGetValue((parentVersionTable, parentIdentityId), out var list) ? list : [];
}

internal sealed class InMemoryDependentCoverageProvider : IDependentCoverageProvider
{
    private readonly Dictionary<(string ChildTable, long ParentIdentityId), List<EP>> _dependents = [];

    public InMemoryDependentCoverageProvider Add(TemporalFkEdge edge, long parentIdentityId, EP period)
    {
        var key = (edge.ChildVersionTable, parentIdentityId);
        if (!_dependents.TryGetValue(key, out var list))
        {
            list = [];
            _dependents[key] = list;
        }

        list.Add(period);
        return this;
    }

    public IReadOnlyList<EP> GetDependentPeriods(TemporalFkEdge edge, long parentIdentityId, IDbTransaction? ambientTransaction) =>
        _dependents.TryGetValue((edge.ChildVersionTable, parentIdentityId), out var list) ? list : [];
}
