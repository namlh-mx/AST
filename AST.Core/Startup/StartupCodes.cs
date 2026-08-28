namespace AST.Core.Startup;

// Single home of every Startup.* code. They are minted across two assemblies -- StartupModeResolver,
// StartupOrchestrator and StartupState (AST.Core), MySqlConnectionTester (AST.Infrastructure) -- so a
// literal re-typed at each site can drift silently; a named constant cannot.
//
// Renaming a CONSTANT is free; changing a VALUE is not: consumers map these codes by literal (the
// platform screens' status text, and the shared message describer that will replace it), so a changed
// value silently sends a consumer down the wrong branch. Same rule as ConfigErrors.Codes.
//
// All is a MANUALLY maintained list, deliberately not reflection-generated, so that StartupCodesTests
// -- which independently reflects over this class's public string fields -- fails the moment an 8th
// constant is declared here without also being added below. A generated list could not fail.
[SharedComponent]
public static class StartupCodes
{
    public const string Pending = "Startup.Pending";
    public const string Ready = "Startup.Ready";
    public const string DbUnreachable = "Startup.DbUnreachable";
    public const string SchemaMismatch = "Startup.SchemaMismatch";
    public const string DbAccessDenied = "Startup.DbAccessDenied";
    public const string DbConnectFailed = "Startup.DbConnectFailed";
    public const string Unexpected = "Startup.Unexpected";

    public static readonly IReadOnlyList<string> All =
    [
        Pending,
        Ready,
        DbUnreachable,
        SchemaMismatch,
        DbAccessDenied,
        DbConnectFailed,
        Unexpected,
    ];
}
