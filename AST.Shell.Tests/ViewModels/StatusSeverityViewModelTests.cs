using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Startup;
using AST.Core.Presentation;
using AST.Shell.Services;
using AST.Shell.Session;
using AST.Shell.ViewModels.Platform;
using ErrorOr;

namespace AST.Shell.Tests.ViewModels;

// Verifies the shared status contract: each screen VM sets `Severity` alongside `StatusMessage`
// (drives the P2 colored-text status line). Fakes mirror the existing per-VM test fakes.
public class StatusSeverityViewModelTests
{
    private static readonly ConnectionFields Sample = new("db.local", 3306, "ast_db", "ast_app", "p@ss");

    private sealed class FakeDeclaration(ErrorOr<ConnectionFields> current) : IConfigDeclarationService
    {
        public ErrorOr<Success> SaveResult = Result.Success;
        public ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> History = new List<ConnectionHistoryEntry>();
        public ErrorOr<ConnectionFields> GetCurrent() => current;
        public ErrorOr<Success> SaveConnection(ConnectionFields fields, byte[]? privateKey, string? passphrase) => SaveResult;
        public ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> GetHistory() => History;
    }

    private sealed class FakeTester(ErrorOr<Success> result) : IConnectionTester
    {
        public ErrorOr<Success> Test(ConnectionFields fields) => result;
    }

    private sealed class FakeRunner : IStartupRunner
    {
        public StartupStatus Rerun() => new(StartupMode.Connected, "Startup.Ok", "Đã kết nối.");
    }

    private sealed class FailVerifier : IAdminKeyVerifier
    {
        public ErrorOr<Success> Verify(byte[] privateKey, string? passphrase)
            => Error.Unauthorized("Auth.Bad", "Sai passphrase.");
    }

    private sealed class StubPicker(PickedFile? file) : IFilePickerService
    {
        public PickedFile? PickPrivateKey() => file;
    }

    // Minimal fakes so AdminAuthViewModel's 7-arg ctor can be built (severity tests do not exercise children).
    private sealed class FakeUser : ICurrentWindowsUser { public string? Username => "me"; }

    private sealed class FakeBreakGlassService : IBreakGlassAdminService
    {
        public ErrorOr<BreakGlassView> Load() =>
            new BreakGlassView(new[] { new BreakGlassAdmin("a", null) }, BreakGlassHealth.Valid, @"C:\config\admins.json", null, null);
        public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase) => Result.Success;
    }

    private sealed class FakeLog : IConfigAuditLog
    {
        public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase) => Result.Success;
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read() => (ErrorOr<IReadOnlyList<ConfigAuditRecord>>)Array.Empty<ConfigAuditRecord>();
        public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity() => new ConfigAuditIntegrity(true, null, true);
    }

    private static AdminAuthViewModel BuildAdmin(IAdminKeyVerifier verifier, IFilePickerService picker)
    {
        var session = new AdminSession();
        var user = new FakeUser();
        var breakGlass = new BreakGlassAdminViewModel(new FakeBreakGlassService(), session, user);
        var history = new ConfigAuditHistoryViewModel(new FakeLog(), session);
        return new AdminAuthViewModel(verifier, session, picker, user, breakGlass, history, allowDebugSkip: false);
    }

    private static void FillValid(ConnectionDeclarationViewModel vm)
    { vm.Host = "db.local"; vm.Port = "3306"; vm.Database = "ast_db"; vm.User = "ast_app"; vm.Password = "secret"; }

    private static ConnectionDeclarationViewModel BuildConnection(ErrorOr<Success> testResult)
        => new(new FakeDeclaration(Sample), new FakeTester(testResult), new AdminSession(), new FakeRunner());

    [Fact]
    public void Connection_severity_none_by_default()
        => Assert.Equal(StatusSeverity.None, BuildConnection(Result.Success).Severity);

    [Fact]
    public async Task Connection_test_success_sets_success_severity()
    {
        var vm = BuildConnection(Result.Success);
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Connection_test_failure_sets_error_severity()
    {
        var vm = BuildConnection(Error.Failure("Startup.DbConnectFailed", "Không kết nối được."));
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    [Fact]
    public void Admin_severity_none_by_default()
    {
        var vm = BuildAdmin(new FailVerifier(), new StubPicker(null));
        Assert.Equal(StatusSeverity.None, vm.Severity);
    }

    [Fact]
    public void Admin_auth_failure_sets_error_severity()
    {
        var vm = BuildAdmin(new FailVerifier(), new StubPicker(new PickedFile("k.pem", new byte[] { 1 })));
        vm.BrowseKeyCommand.Execute();   // sets the picked key so Authenticate can run
        vm.Passphrase = "wrong";
        vm.AuthenticateCommand.Execute(); // verify fails -> status + Severity=Error
        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }
}
