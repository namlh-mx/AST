namespace AST.Core.Iam;

// Permission-resolution result for a (user, function) pair AT TODAY.
public sealed record DataScope(ScopeLevel Level, long? RootOrgUnitId, string Username);
//  RootOrgUnitId: the user's root org unit today (null when Global). Username: for the Self level.
