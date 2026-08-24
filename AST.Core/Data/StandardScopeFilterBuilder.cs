using AST.Core.Iam;

namespace AST.Core.Data;

// The 3 standard conditions (§6, docs/design-effective-period.md) — generates SQL + Dapper parameters, does not execute itself.
public sealed class StandardScopeFilterBuilder : IStandardScopeFilterBuilder
{
    public ScopeFilter Build(
        DataScope scope,
        DateOnly asOf,
        DateOnly today,
        string alias,
        string orgUnitColumn,
        string ownerColumn)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["@asOf"] = asOf,
        };

        var clauses = new List<string>
        {
            $"{alias}.isactive = 1",
            $"{alias}.effective_from <= @asOf AND @asOf <= {alias}.effective_to",
        };

        switch (scope.Level)
        {
            case ScopeLevel.Self:
                clauses.Add($"{ownerColumn} = @currentUsername");
                parameters["@currentUsername"] = scope.Username;
                break;

            case ScopeLevel.OwnOrgUnit:
                clauses.Add($"{orgUnitColumn} = @rootOrgUnitId");
                parameters["@rootOrgUnitId"] = scope.RootOrgUnitId;
                break;

            case ScopeLevel.OwnOrgUnitAndDescendants:
                clauses.Add($"{orgUnitColumn} IN ({BuildSubtreeCte()})");
                parameters["@rootOrgUnitId"] = scope.RootOrgUnitId;
                parameters["@today"] = today;
                break;

            case ScopeLevel.Global:
                // No org-unit condition added.
                break;
        }

        return new ScopeFilter(string.Join(" AND ", clauses), parameters);
    }

    // Recursive CTE for the org-unit subtree (§4 docs/design-iam-schema.md, parameters @rootOrgUnitId/@today).
    private static string BuildSubtreeCte() =>
        """
        WITH RECURSIVE
        today_ou AS (
          SELECT ouv.org_unit_id, ouv.parent_id,
                 ROW_NUMBER() OVER (PARTITION BY ouv.org_unit_id
                                    ORDER BY ouv.isactive DESC, ouv.id DESC) AS rn
          FROM org_unit_version ouv
          WHERE ouv.effective_from <= @today AND @today <= ouv.effective_to
        ),
        subtree AS (
          SELECT @rootOrgUnitId AS id
          UNION ALL
          SELECT t.org_unit_id
          FROM today_ou t
          JOIN subtree s ON t.parent_id = s.id
          WHERE t.rn = 1
        )
        SELECT id FROM subtree
        """;
}
