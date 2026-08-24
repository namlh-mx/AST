using AST.Core.Iam;

namespace AST.Core.Data;

[SharedComponent]
public interface IStandardScopeFilterBuilder
{
    // Enforces 3 conditions SIMULTANEOUSLY (§6, invariant #2):
    //  (1) {alias}.isactive = 1
    //  (2) {alias}.effective_from <= @asOf AND @asOf <= {alias}.effective_to   (business date D)
    //  (3) org-unit scope per DataScope, evaluated by TODAY (today):
    //      Self  -> {ownerColumn} = @currentUsername
    //      OwnOrgUnit                 -> {orgUnitColumn} = @rootOrgUnitId
    //      OwnOrgUnitAndDescendants   -> {orgUnitColumn} IN (subtree CTE @rootOrgUnitId,@today)  (§4)
    //      Global -> (no org-unit condition added)
    ScopeFilter Build(DataScope scope, DateOnly asOf, DateOnly today,
                      string alias, string orgUnitColumn, string ownerColumn);
}
