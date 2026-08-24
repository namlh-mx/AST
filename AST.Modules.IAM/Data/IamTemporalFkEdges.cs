using AST.Core.EffectivePeriod;

namespace AST.Modules.IAM.Data;

// The 5 IAM temporal-FK edges (§1.6 docs/design-iam-schema.md). They live in the IAM module, not in AST.Core:
// the SharedKernel must not carry any module's schema, otherwise a second module with temporal FKs would mean
// editing AST.Core to add it (rule-module-boundary §1).
internal static class IamTemporalFkEdges
{
    public static IReadOnlyList<TemporalFkEdge> All { get; } =
    [
        new TemporalFkEdge("user_version", "org_unit_id", "org_unit_version", "org_unit_id"),
        new TemporalFkEdge("user_version", "role_id", "role_version", "role_id"),
        new TemporalFkEdge("role_permission_version", "role_id", "role_version", "role_id"),
        new TemporalFkEdge("role_permission_version", "function_id", "function_version", "function_id"),
        // [Q1 settled] self-ref: root org units (parent_id IS NULL) are exempt from checking -- the caller
        // does not call ValidateChildCoverage for this edge when parent_id is null (the validator has no way to know NULL).
        new TemporalFkEdge("org_unit_version", "parent_id", "org_unit_version", "org_unit_id"),
    ];

    public static TemporalFkRegistry CreateRegistry() => new(All);
}
