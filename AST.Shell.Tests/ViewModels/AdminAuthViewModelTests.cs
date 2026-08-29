using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Presentation;
using AST.Shell.Services;
using AST.Shell.Session;
using AST.Shell.ViewModels.Platform;
using ErrorOr;
using FluentAssertions;

namespace AST.Shell.Tests.ViewModels;

public class AdminAuthViewModelTests
{
    private sealed class FakePicker(PickedFile? result) : IFilePickerService
    {
        public PickedFile? PickPrivateKey() => result;
    }

    private sealed class FakeVerifier(ErrorOr<Success> result) : IAdminKeyVerifier
    {
        public byte[]? LastKey; public string? LastPass;
        public ErrorOr<Success> Verify(byte[] privateKey, string? passphrase)
        { LastKey = privateKey; LastPass = passphrase; return result; }
    }

    // --- minimal fakes to build the child VMs (this test does not exercise them) ---
    private sealed class FakeUser(string? name) : ICurrentWindowsUser { public string? Username { get; } = name; }

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

    private static AdminAuthViewModel Vm(
        IAdminKeyVerifier verifier, IAdminSession session, IFilePickerService picker,
        bool allowDebugSkip, ICurrentWindowsUser? user = null)
    {
        var u = user ?? new FakeUser("me");
        var breakGlass = new BreakGlassAdminViewModel(new FakeBreakGlassService(), session, u);
        var history = new ConfigAuditHistoryViewModel(new FakeLog(), session);
        return new AdminAuthViewModel(verifier, session, picker, u, breakGlass, history, allowDebugSkip);
    }

    [Fact]
    public void Authenticate_disabled_until_a_key_is_picked()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(null), allowDebugSkip: false);

        Assert.False(vm.AuthenticateCommand.CanExecute());
    }

    [Fact]
    public void BrowseKey_sets_path_and_enables_Authenticate()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1, 2 })), allowDebugSkip: false);

        vm.BrowseKeyCommand.Execute();

        Assert.Equal(@"D:\key.pem", vm.KeyFilePath);
        Assert.True(vm.AuthenticateCommand.CanExecute());
    }

    [Fact]
    public void Authenticate_success_stores_key_in_session_and_flips_flag()
    {
        var session = new AdminSession();
        var key = new byte[] { 7, 7 };
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", key)), allowDebugSkip: false);
        vm.Passphrase = "pp";
        vm.BrowseKeyCommand.Execute();

        vm.AuthenticateCommand.Execute();

        Assert.True(vm.IsAuthenticated);
        Assert.True(session.IsAuthenticated);
        Assert.Same(key, session.PrivateKey);
        Assert.Equal("pp", session.Passphrase);
    }

    [Fact]
    public void Authenticate_failure_shows_the_settled_sentence_not_the_raw_description()
    {
        var session = new AdminSession();
        var vm = Vm(
            new FakeVerifier(Error.Validation(ConfigErrors.Codes.KeyMismatch, "raw text that must not be shown")),
            session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.BrowseKeyCommand.Execute();

        vm.AuthenticateCommand.Execute();

        vm.StatusMessage.Should()
            .Be(PlatformErrorDescriber.Catalog[ConfigErrors.Codes.KeyMismatch])
            .And.NotBe("raw text that must not be shown");
        vm.Severity.Should().Be(StatusSeverity.Error);
    }

    [Fact]
    public void Authenticate_failure_shows_message_and_does_not_authenticate()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(ConfigErrors.KeyMismatch()), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.BrowseKeyCommand.Execute();

        vm.AuthenticateCommand.Execute();

        Assert.False(vm.IsAuthenticated);
        Assert.False(session.IsAuthenticated);
        vm.StatusMessage.Should().Be(PlatformErrorDescriber.Catalog[ConfigErrors.Codes.KeyMismatch]);
    }

    [Fact]
    public void SkipDebug_enabled_only_when_allowed_and_authenticates_without_key()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(null), allowDebugSkip: true);

        Assert.True(vm.SkipDebugCommand.CanExecute());
        vm.SkipDebugCommand.Execute();

        Assert.True(session.IsAuthenticated);
        Assert.Null(session.PrivateKey);
    }

    [Fact]
    public void SkipDebug_disabled_when_not_allowed()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(null), allowDebugSkip: false);

        Assert.False(vm.CanSkipDebug);
        Assert.False(vm.SkipDebugCommand.CanExecute());
    }

    [Fact]
    public void ClearForm_resets_key_and_disables_Authenticate()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.BrowseKeyCommand.Execute();
        Assert.True(vm.AuthenticateCommand.CanExecute());

        vm.ClearFormCommand.Execute();

        Assert.Null(vm.KeyFilePath);
        Assert.Equal(string.Empty, vm.Passphrase);
        Assert.False(vm.AuthenticateCommand.CanExecute());
    }

    // IDeclarationForm.Clear drives the base view's OnNavigatedFrom: the region keeps view + VM alive, so
    // nothing typed may survive leaving. The authenticated session keeps its own copy of the key.
    [Fact]
    public void Clear_as_leave_reset_drops_the_typed_key_and_passphrase_but_not_the_authenticated_session()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.BrowseKeyCommand.Execute();
        vm.Passphrase = "pp";
        vm.AuthenticateCommand.Execute();

        vm.Clear();

        Assert.Equal(string.Empty, vm.Passphrase);
        Assert.Null(vm.KeyFilePath);
        Assert.Equal("pp", session.Passphrase);
        Assert.True(vm.IsAuthenticated);
    }

    // The session holds the key from here on, so leaving it on screen only exposes a secret + a key path.
    [Fact]
    public void Authenticate_success_clears_the_key_form_but_stays_authenticated()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.BrowseKeyCommand.Execute();
        vm.Passphrase = "pp";

        vm.AuthenticateCommand.Execute();

        Assert.True(vm.IsAuthenticated);
        Assert.Null(vm.KeyFilePath);
        Assert.Equal(string.Empty, vm.Passphrase);
    }

    [Fact]
    public void HasUnsavedInput_only_while_the_key_form_still_holds_something()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        Assert.False(vm.HasUnsavedInput);          // just opened

        vm.Passphrase = "pp";
        Assert.True(vm.HasUnsavedInput);           // mid-declaration

        vm.Clear();
        Assert.False(vm.HasUnsavedInput);          // nothing left to lose
    }

    [Fact]
    public void ClearForm_enabled_only_before_authentication()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.Passphrase = "pp";
        vm.BrowseKeyCommand.Execute();
        Assert.True(vm.ClearFormCommand.CanExecute());

        vm.AuthenticateCommand.Execute();

        Assert.False(vm.ClearFormCommand.CanExecute());
        Assert.True(vm.EndSessionCommand.CanExecute());
    }

    [Fact]
    public void Authenticate_populates_key_info()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false,
            user: new FakeUser("itadmin"));
        vm.Passphrase = "pp";
        vm.BrowseKeyCommand.Execute();

        vm.AuthenticateCommand.Execute();

        Assert.Equal("itadmin", vm.Operator);
        Assert.True(vm.AuthenticatedAt.HasValue);
        Assert.Contains("itadmin", vm.AuthenticatedKeyInfo);
    }

    [Fact]
    public void AuthenticatedKeyInfo_uses_pipe_and_yyyy_MM_dd_format()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false,
            user: new FakeUser("itadmin"));
        vm.BrowseKeyCommand.Execute();

        vm.AuthenticateCommand.Execute();

        Assert.Matches(@"^itadmin \| \d{4}-\d{2}-\d{2} \d{2}:\d{2}$", vm.AuthenticatedKeyInfo);
    }

    [Fact]
    public void EndSession_clears_session_and_key_info()
    {
        var session = new AdminSession();
        var vm = Vm(new FakeVerifier(Result.Success), session,
            new FakePicker(new PickedFile(@"D:\key.pem", new byte[] { 1 })), allowDebugSkip: false);
        vm.Passphrase = "pp";
        vm.BrowseKeyCommand.Execute();
        vm.AuthenticateCommand.Execute();
        Assert.True(vm.IsAuthenticated);

        vm.EndSessionCommand.Execute();

        Assert.False(vm.IsAuthenticated);
        Assert.False(session.IsAuthenticated);
        Assert.Null(vm.Operator);
        Assert.Null(vm.AuthenticatedAt);
        Assert.Null(vm.AuthenticatedKeyInfo);
    }

    [Fact]
    public void Banner_mirrors_break_glass_status()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(null), allowDebugSkip: false);
        vm.BreakGlass.NewAdmin = "only"; vm.BreakGlass.AddCommand.Execute();

        vm.BreakGlass.RemoveCommand.Execute("only"); // delete-last guard -> a child status message

        Assert.Equal("Duy trì ít nhất một người cứu hộ.", vm.StatusMessage);
        Assert.Equal(StatusSeverity.Warning, vm.Severity);
    }

    [Fact]
    public void ViewModel_implements_IStatusBanner()
    {
        var vm = Vm(new FakeVerifier(Result.Success), new AdminSession(),
            new FakePicker(null), allowDebugSkip: false);

        Assert.IsAssignableFrom<IStatusBanner>(vm);
    }

    [Fact]
    public void ConnectionDeclarationViewModel_implements_IStatusBanner() =>
        Assert.True(typeof(IStatusBanner).IsAssignableFrom(typeof(ConnectionDeclarationViewModel)));
}
