using AST.Core.Data;
using AST.Core.Security;
using ErrorOr;

namespace AST.Core.Startup;

// Startup orchestration (chain: read File A -> try DB connection -> check schema -> StartupStatus),
// separated from App.xaml.cs (Shell) so it can be tested headless (rule-testing invariant #6; qa §⑤
// #2 F3). RECEIVES every dependency via parameter/delegate instead of resolving DI itself -- checkSchema
// is a SYNCHRONOUS delegate (Shell is responsible for wrapping Task.Run/GetAwaiter().GetResult() safely
// for the dispatcher, see App.xaml.cs) so this class does not need to depend on the CONCRETE schema-check
// mechanism (whose implementation only DI can resolve after Prism loads modules) and stays testable headless
// with a fake/stub delegate.
//
// Fail-safe wrapper at the startup BOUNDARY: any unexpected error touching the DB (schema_version not
// yet existing = MySQL 1146 when the DB is reachable-but-not-yet-migrated, transient errors, errors
// inside the delegates...) must become a clear-fail StartupStatus, and must NOT escape and crash the
// app (spec §5, rule-platform-infra invariant #1). onUnexpectedError is where Shell writes Log.Error --
// this catch-all DOES report the error, it does not swallow it.
public sealed class StartupOrchestrator(
    IConnectionConfigStore configStore,
    IConnectionTester connectionTester,
    Func<SchemaCheckOutcome> checkSchema,
    Action<Exception> onUnexpectedError,
    IConfigAuditLog audit)
{
    public StartupStatus Resolve()
    {
        try
        {
            var read = configStore.Read();
            if (read.IsError && read.FirstError.Code == ConfigErrors.Codes.SignatureInvalid)
            {
                // Best-effort: no key at startup → hash-chain only, no tipSig. Never change Resolve outcome.
                _ = audit.Append(
                    new ConfigAuditEvent("FileA", "SignatureVerifyFailed", null, "Failure", ConfigErrors.Codes.SignatureInvalid),
                    privateKey: null, passphrase: null);
            }

            var outcome = read.IsError ? MapOutcome(read.FirstError) : FileAOutcome.Ok;

            var dbReachable = false;
            var schemaMatch = false;
            string? schemaMessage = null;
            if (outcome == FileAOutcome.Ok)
            {
                dbReachable = !connectionTester.Test(read.Value).IsError;
                if (dbReachable)
                {
                    var result = checkSchema();
                    schemaMatch = result.IsMatch;
                    schemaMessage = result.BlockMessage;
                }
            }

            return StartupModeResolver.Resolve(outcome, dbReachable, schemaMatch, schemaMessage);
        }
        catch (Exception ex)
        {
            onUnexpectedError(ex);
            return new StartupStatus(StartupMode.NotConnected, StartupCodes.Unexpected,
                "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.");
        }
    }

    private static FileAOutcome MapOutcome(Error error) => error.Type switch
    {
        ErrorType.NotFound => FileAOutcome.NotDeclared,
        ErrorType.Failure => FileAOutcome.IoError,
        _ => FileAOutcome.Corrupt,
    };
}
