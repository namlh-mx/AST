namespace AST.Core.Startup;

// Single home of every Startup.* code. They are minted across two assemblies -- StartupModeResolver,
// StartupOrchestrator and StartupState (AST.Core), MySqlConnectionTester (AST.Infrastructure) -- so a
// literal re-typed at each site can drift silently; a named constant cannot.
//
// Renaming a CONSTANT is free; changing a VALUE is not: consumers map these codes by literal, so a
// changed value silently sends a consumer down the wrong branch. Same rule as ConfigErrors.Codes.
// The describer is no longer future tense -- AST.Shell.Presentation.PlatformErrorDescriber ships and
// keys its catalog on DbAccessDenied and DbConnectFailed; the platform screens' own status text is
// replaced by it in spec step 3, not before.
//
// ⚠ The MINT-SITE sentence above survived this change and that is not an oversight: a prior review
// predicted it would go false once the describer added a StartupCodes.* reference in an AST.Shell
// ViewModel. Re-derived when the describer shipped -- a reference is not a MINT, and the describer
// creates no StartupStatus, so the two-assembly claim still holds. The row named the right FILE and
// the wrong SENTENCE; what actually went stale was the future tense repaired above.
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
