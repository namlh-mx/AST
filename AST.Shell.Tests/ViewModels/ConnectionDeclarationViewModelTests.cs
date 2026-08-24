using AST.Core.Data;
using AST.Core.Startup;
using AST.Shell.Session;
using AST.Shell.ViewModels.Platform;
using ErrorOr;

namespace AST.Shell.Tests.ViewModels;

public class ConnectionDeclarationViewModelTests
{
    private static readonly ConnectionFields Sample = new("db.local", 3306, "ast_db", "ast_app", "p@ss");

    private sealed class FakeDeclaration(ErrorOr<ConnectionFields> current) : IConfigDeclarationService
    {
        public ConnectionFields? Saved; public byte[]? SavedKey; public string? SavedPass;
        public ErrorOr<Success> SaveResult = Result.Success;
        public ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> History = new List<ConnectionHistoryEntry>();
        public ErrorOr<ConnectionFields> GetCurrent() => current;
        public ErrorOr<Success> SaveConnection(ConnectionFields fields, byte[]? privateKey, string? passphrase)
        { Saved = fields; SavedKey = privateKey; SavedPass = passphrase; return SaveResult; }
        public ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> GetHistory() => History;
    }

    private sealed class FakeTester(ErrorOr<Success> result) : IConnectionTester
    {
        public ConnectionFields? Tested; public int Calls;
        public ErrorOr<Success> Test(ConnectionFields fields) { Tested = fields; Calls++; return result; }
    }

    private sealed class FakeRunner : IStartupRunner
    {
        public int Calls;
        public StartupMode Mode = StartupMode.Connected;
        public StartupStatus Rerun() { Calls++; return new StartupStatus(Mode, "Startup.Ok", "Đã kết nối."); }
    }

    private static ConnectionDeclarationViewModel Build(
        FakeDeclaration decl, FakeTester? tester = null, IAdminSession? session = null, FakeRunner? runner = null)
        => new(decl, tester ?? new FakeTester(Result.Success), session ?? new AdminSession(), runner ?? new FakeRunner());

    private static void FillValid(ConnectionDeclarationViewModel vm)
    { vm.Host = "db.local"; vm.Port = "3306"; vm.Database = "ast_db"; vm.User = "ast_app"; vm.Password = "secret"; }

    // The form opens blank even when File A holds a readable declaration: the configuration in force is
    // shown as the newest history row, and the operator brings it back explicitly with Reuse.
    [Fact]
    public void Load_opens_a_blank_form_even_when_FileA_is_readable()
    {
        var vm = Build(new FakeDeclaration(Sample));
        vm.Load();
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Port);
        Assert.Equal(string.Empty, vm.Database);
        Assert.Equal(string.Empty, vm.User);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Load_leaves_fields_empty_when_FileA_absent_or_corrupt()
    {
        var vm = Build(new FakeDeclaration(Error.NotFound("Config.NotDeclared", "chưa khai báo")));
        vm.Load();
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Port);
        Assert.Equal(string.Empty, vm.Password);
    }

    [Fact]
    public void Test_requires_password_even_when_FileA_readable()
    {
        var vm = Build(new FakeDeclaration(Sample));
        vm.Load();
        vm.Host = "db.local"; vm.Port = "3306"; vm.Database = "ast_db"; vm.User = "ast_app";
        Assert.False(vm.TestCommand.CanExecute());
        vm.Password = "secret";
        Assert.True(vm.TestCommand.CanExecute());
    }

    [Fact]
    public void Test_disabled_when_a_field_missing_or_port_out_of_range()
    {
        var vm = Build(new FakeDeclaration(Error.NotFound("Config.NotDeclared", "x")));
        Assert.False(vm.TestCommand.CanExecute());
        FillValid(vm);
        Assert.True(vm.TestCommand.CanExecute());
        vm.Port = "0";
        Assert.False(vm.TestCommand.CanExecute());
        vm.Port = "70000";
        Assert.False(vm.TestCommand.CanExecute());
        vm.Port = "12x";
        Assert.False(vm.TestCommand.CanExecute());
    }

    [Fact]
    public void IsTestPassed_false_by_default()
    {
        var vm = Build(new FakeDeclaration(Sample));
        Assert.False(vm.IsTestPassed);
    }

    [Fact]
    public async Task Save_disabled_until_test_succeeds_then_enabled()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success));
        FillValid(vm);
        Assert.False(vm.SaveCommand.CanExecute());
        await vm.TestCommand.Execute();
        Assert.True(vm.IsTestPassed);
        Assert.True(vm.SaveCommand.CanExecute());
    }

    [Fact]
    public async Task Editing_a_field_after_test_disables_save_again()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success));
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.True(vm.SaveCommand.CanExecute());
        vm.Host = "other.host";
        Assert.False(vm.IsTestPassed);
        Assert.False(vm.SaveCommand.CanExecute());
    }

    [Fact]
    public async Task Save_disabled_again_after_successful_save()
    {
        var decl = new FakeDeclaration(Sample);
        var vm = Build(decl, new FakeTester(Result.Success));
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.True(vm.SaveCommand.CanExecute());
        await vm.SaveCommand.Execute();
        Assert.False(vm.IsTestPassed);
        Assert.False(vm.SaveCommand.CanExecute());
        Assert.NotNull(decl.Saved);
    }

    [Fact]
    public async Task Test_success_sets_status_and_gate()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success));
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.True(vm.IsTestPassed);
        Assert.Equal("Kết nối database thành công.", vm.StatusMessage);
    }

    [Fact]
    public async Task Test_failure_sets_status_and_keeps_save_disabled()
    {
        var vm = Build(new FakeDeclaration(Sample),
            new FakeTester(Error.Failure("Startup.DbConnectFailed", "Không kết nối được máy chủ database.")));
        FillValid(vm);
        await vm.TestCommand.Execute();
        Assert.False(vm.IsTestPassed);
        Assert.False(vm.SaveCommand.CanExecute());
        Assert.Equal("Không kết nối được máy chủ database.", vm.StatusMessage);
    }

    [Fact]
    public async Task Test_uses_typed_password_without_substitution()
    {
        var tester = new FakeTester(Result.Success);
        var vm = Build(new FakeDeclaration(Sample), tester);
        vm.Load();
        vm.Password = "typed";
        await vm.TestCommand.Execute();
        Assert.Equal("typed", tester.Tested!.Password);
    }

    [Fact]
    public async Task Save_uses_typed_password_and_trims_fields()
    {
        var decl = new FakeDeclaration(Sample);
        var vm = Build(decl, new FakeTester(Result.Success));
        vm.Host = " db.local "; vm.Port = "3306"; vm.Database = " ast_db "; vm.User = " ast_app "; vm.Password = "typed";
        await vm.TestCommand.Execute();
        await vm.SaveCommand.Execute();
        Assert.Equal(new ConnectionFields("db.local", 3306, "ast_db", "ast_app", "typed"), decl.Saved);
    }

    [Fact]
    public async Task Save_re_tests_and_aborts_when_test_fails()
    {
        var decl = new FakeDeclaration(Sample);
        var runner = new FakeRunner();
        var vm = Build(decl,
            new FakeTester(Error.Validation("Startup.DbAccessDenied", "Sai tài khoản hoặc mật khẩu database.")),
            runner: runner);
        FillValid(vm);
        await vm.SaveCommand.Execute();
        Assert.Null(decl.Saved);
        Assert.Equal(0, runner.Calls);
        Assert.False(vm.IsTestPassed);
        Assert.Equal("Sai tài khoản hoặc mật khẩu database.", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_passes_session_key_saves_then_reruns_startup()
    {
        var decl = new FakeDeclaration(Sample);
        var session = new AdminSession();
        var key = new byte[] { 5, 5 };
        session.Authenticate(key, "pp");
        var runner = new FakeRunner();
        var vm = Build(decl, session: session, runner: runner);
        FillValid(vm);
        await vm.SaveCommand.Execute();
        Assert.Same(key, decl.SavedKey);
        Assert.Equal("pp", decl.SavedPass);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Save_failure_sets_status_and_does_not_rerun_startup()
    {
        var decl = new FakeDeclaration(Sample)
        { SaveResult = Error.Validation("Config.KeyRequired", "Cần nạp khóa bí mật để ký cấu hình.") };
        var runner = new FakeRunner();
        var vm = Build(decl, runner: runner);
        FillValid(vm);
        await vm.SaveCommand.Execute();
        Assert.Equal("Cần nạp khóa bí mật để ký cấu hình.", vm.StatusMessage);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Load_fills_history_from_service()
    {
        var decl = new FakeDeclaration(Sample)
        { History = new List<ConnectionHistoryEntry> { new("t", "me", "PC", "h1", 3306, "d1", "u1") } };
        var vm = Build(decl);
        vm.Load();
        Assert.Single(vm.History);
    }

    [Fact]
    public async Task Reuse_loads_fields_except_password_and_resets_gate()
    {
        var vm = Build(new FakeDeclaration(Sample));
        FillValid(vm);
        await vm.TestCommand.Execute();
        vm.ReuseCommand.Execute(new ConnectionHistoryEntry("t", "me", "PC", "h9", 3399, "d9", "u9"));
        Assert.Equal("h9", vm.Host);
        Assert.Equal("3399", vm.Port);
        Assert.Equal("d9", vm.Database);
        Assert.Equal("u9", vm.User);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.IsTestPassed);
    }

    [Fact]
    public void Clear_empties_all_fields_and_resets_gate_and_status()
    {
        var vm = Build(new FakeDeclaration(Sample));
        FillValid(vm);
        vm.ClearCommand.Execute();
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Database);
        Assert.Equal(string.Empty, vm.User);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.IsTestPassed);
        Assert.Null(vm.StatusMessage);
    }

    // Clear() doubles as the leave-reset the view calls from OnNavigatedFrom: the view + VM are reused by
    // Prism, so the whole form — password first — must not outlive the screen.
    [Fact]
    public async Task Clear_as_leave_reset_drops_every_field_including_the_password_and_the_test_gate()
    {
        var vm = Build(new FakeDeclaration(Sample));
        FillValid(vm);
        await vm.TestCommand.Execute();

        vm.Clear();

        Assert.Equal(string.Empty, vm.Password);
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Database);
        Assert.Equal(string.Empty, vm.User);
        Assert.Equal(string.Empty, vm.Port);
        Assert.False(vm.IsTestPassed);
    }

    [Fact]
    public async Task Save_drops_the_password_once_the_connection_is_proven()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success),
            runner: new FakeRunner { Mode = StartupMode.Connected });
        FillValid(vm);
        await vm.TestCommand.Execute();

        await vm.SaveCommand.Execute();

        Assert.Equal(string.Empty, vm.Password);
        // The non-secret fields stay so the operator can see what was just declared.
        Assert.Equal("db.local", vm.Host);
        Assert.Equal("ast_db", vm.Database);
    }

    [Fact]
    public async Task Save_keeps_the_password_when_the_connection_fails_so_the_operator_can_retry()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success),
            runner: new FakeRunner { Mode = StartupMode.NotConnected });
        FillValid(vm);
        await vm.TestCommand.Execute();

        await vm.SaveCommand.Execute();

        Assert.Equal("secret", vm.Password);
    }

    // Drives the leave-confirmation: only worth asking when there is work to lose.
    [Fact]
    public async Task HasUnsavedInput_only_while_the_form_is_both_touched_and_non_empty()
    {
        var vm = Build(new FakeDeclaration(Sample), new FakeTester(Result.Success));
        vm.Load();
        Assert.False(vm.HasUnsavedInput);        // just opened, nothing typed

        FillValid(vm);
        Assert.True(vm.HasUnsavedInput);         // mid-declaration

        vm.Clear();
        Assert.False(vm.HasUnsavedInput);        // touched, but nothing left to lose

        FillValid(vm);
        await vm.TestCommand.Execute();
        await vm.SaveCommand.Execute();
        Assert.False(vm.HasUnsavedInput);        // saved -- not dirty any more
    }

    // Regression (leave-confirm): the port is the operator's raw text like every other field, so ANY entry —
    // even an invalidly-formatted one that cannot be tested — marks the form dirty and is worth confirming on
    // leave. Before the fix Port was int?, so invalid text failed the binding conversion, the setter never ran,
    // the form looked clean, and leaving skipped the confirmation (unlike the string fields).
    [Fact]
    public void Any_port_entry_marks_the_form_as_having_unsaved_input()
    {
        var vm = Build(new FakeDeclaration(Sample));
        vm.Load();
        Assert.False(vm.HasUnsavedInput);
        vm.Port = "8080";
        Assert.True(vm.HasUnsavedInput);
    }

    [Fact]
    public void An_invalidly_formatted_port_still_counts_as_unsaved_input_but_is_not_testable()
    {
        var vm = Build(new FakeDeclaration(Sample));
        vm.Load();
        vm.Port = "12x";
        Assert.True(vm.HasUnsavedInput);
        Assert.False(vm.TestCommand.CanExecute());
    }

    [Fact]
    public async Task IsDirty_false_after_load_and_after_save_true_after_edit_or_clear()
    {
        var decl = new FakeDeclaration(Sample);
        var vm = Build(decl, new FakeTester(Result.Success));
        vm.Load();
        Assert.False(vm.IsDirty);
        FillValid(vm);
        Assert.True(vm.IsDirty);
        await vm.TestCommand.Execute();
        await vm.SaveCommand.Execute();
        Assert.False(vm.IsDirty);
        vm.ClearCommand.Execute();
        Assert.True(vm.IsDirty);
    }
}
