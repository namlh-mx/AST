namespace AST.Core.EffectivePeriod;

// Holds the temporal-FK edges the application validates. AST.Core is the SharedKernel, so it deliberately knows
// NO module's schema: each module declares its own edges and hands them in (rule-module-boundary §1 -- adding a
// module must not require editing AST.Core).
//
// An EMPTY registry is rejected, and that guard is load-bearing rather than defensive: TemporalFkValidator walks
// the registry and returns Success when it yields nothing, so an empty one silently disables the whole STRICT
// temporal-FK invariant (D8) on both the forward and reverse branches -- with no error and
// every test still green. It is reachable by accident: a container asked to auto-wire this type resolves
// IEnumerable<TemporalFkEdge> to an EMPTY collection rather than failing. Fail loudly at composition instead.
public sealed class TemporalFkRegistry : ITemporalFkRegistry
{
    private readonly List<TemporalFkEdge> _edges;

    public TemporalFkRegistry(IEnumerable<TemporalFkEdge> edges)
    {
        _edges = [.. edges];
        if (_edges.Count == 0)
        {
            throw new ArgumentException(
                "A TemporalFkRegistry with no edges validates nothing and would silently disable every STRICT " +
                "temporal-FK check. The owning module must declare its edges (IAM: IamTemporalFkEdges).",
                nameof(edges));
        }
    }

    public void Register(TemporalFkEdge edge) => _edges.Add(edge);

    public IReadOnlyList<TemporalFkEdge> EdgesForChild(string childVersionTable) =>
        _edges.Where(e => e.ChildVersionTable == childVersionTable).ToList();

    public IReadOnlyList<TemporalFkEdge> EdgesForParent(string parentVersionTable) =>
        _edges.Where(e => e.ParentVersionTable == parentVersionTable).ToList();
}
