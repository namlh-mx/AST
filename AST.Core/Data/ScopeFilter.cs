namespace AST.Core.Data;

// Pre-built WHERE fragment + query parameters (key/value), driver-independent.
public sealed record ScopeFilter(string WhereSql, IReadOnlyDictionary<string, object?> Parameters);
