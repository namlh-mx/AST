namespace AST.Core.Iam;

// 4 scope levels — values match column role_permission_version.scope_level (1..4).
public enum ScopeLevel { Self = 1, OwnOrgUnit = 2, OwnOrgUnitAndDescendants = 3, Global = 4 }
