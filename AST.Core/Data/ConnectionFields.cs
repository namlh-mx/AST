namespace AST.Core.Data;

// DB connection fields (spec §4). The app assembles them into a connection string at the Infrastructure layer.
public sealed record ConnectionFields(string Host, int Port, string Database, string User, string Password);
