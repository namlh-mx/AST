using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Session;
using AST.Shell.ViewModels.Platform;
using ErrorOr;
using FluentAssertions;

namespace AST.Shell.Tests.ViewModels;

public class BreakGlassAdminViewModelTests
{
    private sealed class FakeUser(string? name) : ICurrentWindowsUser { public string? Username { get; } = name; }

    private static BreakGlassAdmin[] Bg(params string[] users)
        => users.Select(u => new BreakGlassAdmin(u, null)).ToArray();

    private sealed class FakeService : IBreakGlassAdminService
    {
        public ErrorOr<BreakGlassView> LoadResult =
            new BreakGlassView(
                new[] { new BreakGlassAdmin("a", null), new BreakGlassAdmin("b", null) },
                BreakGlassHealth.Valid, @"C:\config\admins.json", null, null);
        public ErrorOr<Success> SaveResult = Result.Success;
        public int SaveCalls;
        public IReadOnlyList<string>? SavedAdmins;
        public byte[]? SavedKey;
        public string? SavedPass;

        public ErrorOr<BreakGlassView> Load() => LoadResult;
        public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase)
        {
            SaveCalls++;
            SavedAdmins = admins;
            SavedKey = privateKey;
            SavedPass = passphrase;
            return SaveResult;
        }
    }

    private static AdminSession Authed(byte[]? key = null, string? pass = null)
    {
        var s = new AdminSession();
        s.Authenticate(key ?? new byte[] { 9 }, pass ?? "pw");
        return s;
    }

    private static BreakGlassAdminViewModel Vm(
        FakeService? service = null, IAdminSession? session = null, string? currentUser = "me")
        => new(service ?? new FakeService(), session ?? Authed(), new FakeUser(currentUser));

    // Drives the leave-confirmation through AdminAuthViewModel: only worth asking when work would be lost.
    [Fact]
    public void HasUnsavedInput_only_while_a_name_is_typed_or_a_list_change_is_unsaved()
    {
        var vm = Vm();
        vm.LoadCommand.Execute();
        Assert.False(vm.HasUnsavedInput);

        vm.NewAdmin = "someone";
        Assert.True(vm.HasUnsavedInput);       // typed but not added

        vm.AddCommand.Execute();
        Assert.True(vm.HasUnsavedInput);       // added but not signed & saved
    }

    // Clears in memory, on purpose: reloading here would silently KEEP the pending change whenever File B
    // could not be read, and a later Sign & Save would then sign a list the operator believes they dropped.
    [Fact]
    public void ClearForm_drops_the_typed_name_and_the_unsaved_list_change_even_when_FileB_is_unreadable()
    {
        var svc = new FakeService();
        var vm = Vm(svc);
        vm.LoadCommand.Execute();
        vm.NewAdmin = "someone";
        vm.AddCommand.Execute();
        svc.LoadResult = Error.Failure("BreakGlass.Io", "Không đọc được tập tin.");

        vm.ClearForm();

        Assert.Equal(string.Empty, vm.NewAdmin);
        Assert.Empty(vm.Admins);
        Assert.False(vm.HasUnsavedInput);
    }

    // Ending the session empties the grid; leaving the pending flag set would pop the leave-confirmation on
    // a visibly blank screen, which trains operators to dismiss it.
    [Fact]
    public void Ending_the_session_drops_the_pending_flag_with_the_data()
    {
        var session = Authed();
        var vm = Vm(session: session);
        vm.NewAdmin = "someone";
        vm.AddCommand.Execute();
        Assert.True(vm.HasUnsavedInput);

        session.Clear();

        Assert.Empty(vm.Admins);
        Assert.False(vm.HasUnsavedInput);
    }

    [Fact]
    public void Load_projects_users_and_created_display()
    {
        var svc = new FakeService
        {
            LoadResult = new BreakGlassView(
                new[]
                {
                    new BreakGlassAdmin("a", new DateTime(2026, 7, 12, 8, 0, 0, DateTimeKind.Utc)),
                    new BreakGlassAdmin("b", null),
                },
                BreakGlassHealth.Valid, @"C:\config\admins.json", null, "deadbeefdeadbeef"),
        };
        var vm = Vm(svc);

        vm.LoadCommand.Execute();

        Assert.Equal(new[] { "a", "b" }, vm.Admins.Select(x => x.User));
        Assert.Equal("—", vm.Admins[1].CreatedDisplay); // no created date -> dash
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", vm.Admins[0].CreatedDisplay); // yyyy-MM-dd HH:mm
        Assert.Equal(BreakGlassHealth.Valid, vm.Health);
        Assert.Equal(@"C:\config\admins.json", vm.FilePath);
    }

    [Fact]
    public void Add_normalizes_and_rejects_duplicates()
    {
        var vm = Vm();

        vm.NewAdmin = "CORP\\newguy";
        vm.AddCommand.Execute();
        vm.NewAdmin = "NEWGUY"; // same identity after normalization
        vm.AddCommand.Execute();

        Assert.Equal(new[] { "newguy" }, vm.Admins.Select(x => x.User));
    }

    [Fact]
    public void Remove_removes_the_entry()
    {
        var vm = Vm();
        vm.NewAdmin = "a"; vm.AddCommand.Execute();
        vm.NewAdmin = "b"; vm.AddCommand.Execute();

        vm.RemoveCommand.Execute("a");

        Assert.Equal(new[] { "b" }, vm.Admins.Select(x => x.User));
    }

    [Fact]
    public void Removing_current_user_surfaces_a_warning_message()
    {
        var vm = Vm(currentUser: "me");
        vm.NewAdmin = "me"; vm.AddCommand.Execute();
        vm.NewAdmin = "other"; vm.AddCommand.Execute();

        vm.RemoveCommand.Execute("me");

        Assert.Equal(StatusSeverity.Warning, vm.Severity);
        vm.StatusMessage.Should().Be("Người dùng đã xóa tài khoản của chính mình.");
    }

    [Fact]
    public void Save_disabled_when_admin_list_empty()
    {
        var vm = Vm(); // no admins added yet
        Assert.False(vm.SaveCommand.CanExecute());
    }

    [Fact]
    public void Save_disabled_when_not_authenticated()
    {
        var vm = Vm(session: new AdminSession()); // unauthenticated
        vm.NewAdmin = "a"; vm.AddCommand.Execute();
        Assert.False(vm.SaveCommand.CanExecute());
    }

    [Fact]
    public void Save_enabled_when_authenticated_and_at_least_one_admin()
    {
        var vm = Vm();
        vm.NewAdmin = "a"; vm.AddCommand.Execute();
        Assert.True(vm.SaveCommand.CanExecute());
    }

    [Fact]
    public void Save_passes_usernames_key_and_passphrase_to_the_service()
    {
        var svc = new FakeService();
        var key = new byte[] { 4, 2 };
        var vm = Vm(svc, Authed(key, "secret"));
        vm.NewAdmin = "a"; vm.AddCommand.Execute();

        vm.SaveCommand.Execute();

        Assert.Equal(1, svc.SaveCalls);
        Assert.Equal(new[] { "a" }, svc.SavedAdmins);
        Assert.Same(key, svc.SavedKey);
        Assert.Equal("secret", svc.SavedPass);
    }

    [Fact]
    public void Save_success_sets_the_final_message()
    {
        var vm = Vm();
        vm.NewAdmin = "a"; vm.AddCommand.Execute();

        vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Equal("Đã lưu.", vm.StatusMessage);
    }

    [Fact]
    public void Save_success_raises_the_Saved_event()
    {
        var vm = Vm();
        vm.NewAdmin = "a"; vm.AddCommand.Execute();
        var raised = false;
        vm.Saved += (_, _) => raised = true;

        vm.SaveCommand.Execute();

        Assert.True(raised);
    }

    [Fact]
    public void Save_without_changes_is_blocked_and_writes_nothing()
    {
        var svc = new FakeService();       // Load returns admins a,b
        var vm = Vm(svc);                  // authenticated
        vm.LoadCommand.Execute();          // populate rows, dirty reset to false

        vm.SaveCommand.Execute();          // no Add/Remove since load

        Assert.Equal(0, svc.SaveCalls);
        Assert.Equal(StatusSeverity.Warning, vm.Severity);
        Assert.Equal("Không có thông tin để ký và lưu.", vm.StatusMessage);
    }

    [Fact]
    public void Save_after_a_change_proceeds()
    {
        var svc = new FakeService();
        var vm = Vm(svc);
        vm.LoadCommand.Execute();          // dirty reset to false
        vm.NewAdmin = "c"; vm.AddCommand.Execute();   // real add -> dirty true

        vm.SaveCommand.Execute();

        Assert.Equal(1, svc.SaveCalls);
    }

    [Fact]
    public void Save_failure_sets_error_severity()
    {
        var svc = new FakeService { SaveResult = ConfigErrors.KeyMismatch() };
        var vm = Vm(svc);
        vm.NewAdmin = "a"; vm.AddCommand.Execute();

        vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    [Fact]
    public void IsAuthenticated_mirrors_the_session()
    {
        var session = new AdminSession();
        var vm = Vm(session: session);
        Assert.False(vm.IsAuthenticated);

        session.Authenticate(new byte[] { 1 }, "pw");

        Assert.True(vm.IsAuthenticated);
    }

    [Fact]
    public void Removing_the_last_admin_is_blocked_and_warns()
    {
        var vm = Vm();
        vm.NewAdmin = "only"; vm.AddCommand.Execute();
        Assert.Single(vm.Admins);

        vm.RemoveCommand.Execute("only");

        Assert.Single(vm.Admins); // not removed
        Assert.Equal("only", vm.Admins[0].User);
        Assert.Equal(StatusSeverity.Warning, vm.Severity);
        Assert.Equal("Duy trì ít nhất một người cứu hộ.", vm.StatusMessage);
    }

    [Fact]
    public void Removing_when_more_than_one_remains_still_works()
    {
        var vm = Vm();
        vm.NewAdmin = "a"; vm.AddCommand.Execute();
        vm.NewAdmin = "b"; vm.AddCommand.Execute();

        vm.RemoveCommand.Execute("a");

        Assert.Equal(new[] { "b" }, vm.Admins.Select(x => x.User));
    }

    [Fact]
    public void Load_with_tampered_health_shows_the_final_error_message()
    {
        var svc = new FakeService
        {
            LoadResult = new BreakGlassView(Bg("a"), BreakGlassHealth.Tampered, @"C:\config\admins.json", null, null),
        };
        var vm = Vm(svc);

        vm.LoadCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal("Danh sách bị sửa đổi.", vm.StatusMessage);
    }

    [Fact]
    public void Load_with_unreadable_health_shows_the_final_error_message()
    {
        var svc = new FakeService
        {
            LoadResult = new BreakGlassView(Bg("a"), BreakGlassHealth.Unreadable, @"C:\config\admins.json", null, null),
        };
        var vm = Vm(svc);

        vm.LoadCommand.Execute();

        vm.StatusMessage.Should().Be("Ứng dụng không tải được danh sách người cứu hộ.");
    }
}
