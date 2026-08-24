namespace AST.Core;

// Marks a backend type as an intentional SHARED COMPONENT: reuse-facing surface that an agent must find
// in docs/shared-components.md before building a new one (rule-shared-components / rule-prefer-existing).
// Inert at runtime — it exists only so AST.Meta.Tests can reconcile the marked set against the registry.
// Apply to contracts / base classes / shared value types, NOT to pure data carriers or DI-impl classes
// whose interface is already marked.
[AttributeUsage(
    AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum,
    Inherited = false)]
public sealed class SharedComponentAttribute : Attribute;
