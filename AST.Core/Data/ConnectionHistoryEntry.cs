namespace AST.Core.Data;

// A past connection declaration for the history view / reuse. NEVER carries the password.
public sealed record ConnectionHistoryEntry(
    string TsUtc, string User, string Machine, string Host, int Port, string Database, string DbUser);
