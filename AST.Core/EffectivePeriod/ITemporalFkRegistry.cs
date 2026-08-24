namespace AST.Core.EffectivePeriod;

// The edges are declared by the MODULE that owns the tables, not here -- AST.Core is the SharedKernel and knows
// no module's schema (rule-module-boundary §1), so a module ships its own set and hands it to the registry.
[SharedComponent]
public interface ITemporalFkRegistry
{
    void Register(TemporalFkEdge edge);
    IReadOnlyList<TemporalFkEdge> EdgesForChild(string childVersionTable);   // every parent must be checked when saving a child
    IReadOnlyList<TemporalFkEdge> EdgesForParent(string parentVersionTable); // reverse-FK when cutting/closing a parent
}
