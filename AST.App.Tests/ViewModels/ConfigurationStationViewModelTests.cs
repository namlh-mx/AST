using System;
using System.Threading.Tasks;
using AST.Core.Data;
using AST.Core.Iam;
using AST.Shell.Navigation;
using AST.Shell.Session;
using AST.ViewModels.Platform;
using ErrorOr;
using FluentAssertions;
using Prism.Ioc;
using Prism.Navigation;
using Prism.Navigation.Regions;

namespace AST.App.Tests.ViewModels;

public class ConfigurationStationViewModelTests
{
    // Captures the single RequestNavigate the VM issues. The string-source RequestNavigate(regionName, source)
    // the VM calls is an IRegionManagerExtensions extension that routes to this instance method — so overriding
    // just this member is enough to observe the navigation; the rest of IRegionManager is unused here.
    private sealed class CapturingRegionManager : IRegionManager
    {
        public string? LastRegion { get; private set; }
        public string? LastTarget { get; private set; }

        public void RequestNavigate(
            string regionName, Uri target, Action<NavigationResult> navigationCallback, INavigationParameters navigationParameters)
        {
            LastRegion = regionName;
            LastTarget = target.ToString();
        }

        public IRegionCollection Regions => throw new NotSupportedException();
        public IRegionManager CreateRegionManager() => throw new NotSupportedException();
        public IRegionManager AddToRegion(string regionName, object view) => throw new NotSupportedException();
        public IRegionManager AddToRegion(string regionName, string targetName) => throw new NotSupportedException();
        public IRegionManager RegisterViewWithRegion(string regionName, Type viewType) => throw new NotSupportedException();
        public IRegionManager RegisterViewWithRegion(string regionName, string targetName) => throw new NotSupportedException();
        public IRegionManager RegisterViewWithRegion(string regionName, Func<IContainerProvider, object> getContentDelegate)
            => throw new NotSupportedException();
    }

    private sealed class FakeCurrentUser(string? username) : ICurrentWindowsUser
    {
        public string? Username { get; } = username;
    }

    private sealed class FakeAuthorizationService : IAuthorizationService
    {
        public Func<string, string, Task<bool>> IsFunctionOpenAsyncCore { get; set; } = static (_, _) => Task.FromResult(false);

        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => IsFunctionOpenAsyncCore(username, functionKey);

        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) => throw new NotSupportedException();
    }

    private static (ConfigurationStationViewModel Vm, CapturingRegionManager Nav, AdminSession Session, FakeAuthorizationService Auth)
        Build(Func<string, string, Task<bool>>? isFunctionOpenAsyncCore = null)
    {
        var nav = new CapturingRegionManager();
        var session = new AdminSession();
        var auth = new FakeAuthorizationService();
        if (isFunctionOpenAsyncCore is not null)
            auth.IsFunctionOpenAsyncCore = isFunctionOpenAsyncCore;

        var vm = new ConfigurationStationViewModel(nav, session, new FakeCurrentUser("tester"), auth);
        return (vm, nav, session, auth);
    }

    [Fact]
    public void IsDbConfigEnabled_is_false_until_the_admin_authenticates()
    {
        var (vm, _, session, _) = Build();
        Assert.False(vm.IsDbConfigEnabled);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsDbConfigEnabled)) raised = true; };
        session.Authenticate(new byte[] { 1 }, "pw");

        Assert.True(vm.IsDbConfigEnabled);
        Assert.True(raised);
    }

    [Fact]
    public void IsDbConfigEnabled_returns_to_false_when_the_session_is_cleared()
    {
        var (vm, _, session, _) = Build();
        session.Authenticate(new byte[] { 1 }, "pw");

        session.Clear();

        Assert.False(vm.IsDbConfigEnabled);
    }

    [Fact]
    public void ExecuteAdminAuthCommand_navigates_the_content_region_to_the_admin_screen()
    {
        var (vm, nav, _, _) = Build();

        vm.ExecuteAdminAuthCommand.Execute();

        Assert.Equal(MainWindowViewModel.ContentRegionName, nav.LastRegion);
        Assert.Equal("AdminAuthView", nav.LastTarget);
    }

    [Fact]
    public void ExecuteDbConfigCommand_navigates_the_content_region_to_the_connection_screen()
    {
        var (vm, nav, _, _) = Build();

        vm.ExecuteDbConfigCommand.Execute();

        Assert.Equal(MainWindowViewModel.ContentRegionName, nav.LastRegion);
        Assert.Equal("ConnectionDeclarationView", nav.LastTarget);
    }

    [Fact]
    public async Task IsOrgUnitDeclareEnabled_is_false_by_default_until_the_permission_check_resolves()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, _, _) = Build((_, _) => gate.Task);

        Assert.False(vm.IsOrgUnitDeclareEnabled);

        // Deterministic wait: the VM's own PropertyChanged is the actual signal we care about,
        // not an arbitrary delay -- avoids the flakiness of guessing how long the continuation
        // takes to run.
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsOrgUnitDeclareEnabled)) changed.TrySetResult(); };

        ((INavigationAware)vm).OnNavigatedTo(null!);
        gate.SetResult(true);
        await changed.Task;

        Assert.True(vm.IsOrgUnitDeclareEnabled);
    }

    [Fact]
    public void IsOrgUnitDeclareEnabled_stays_false_when_the_permission_check_returns_false()
    {
        var (vm, _, _, _) = Build(static (_, _) => Task.FromResult(false));

        // Task.FromResult returns an ALREADY-COMPLETED task, so the VM's `await` inside
        // RefreshOrgUnitDeclarePermissionAsync resumes synchronously -- no wait needed, and none
        // of the earlier awaited-but-unproven flakiness this test used to have.
        ((INavigationAware)vm).OnNavigatedTo(null!);

        Assert.False(vm.IsOrgUnitDeclareEnabled);
    }

    [Fact]
    public void IsOrgUnitDeclareEnabled_resets_to_false_when_a_later_check_throws_after_an_earlier_grant()
    {
        // Proves the fail-closed RESET genuinely runs (not merely "still false by default"):
        // grant true first, then fault a second navigation-triggered check, and confirm the
        // property snaps back to false rather than keeping the stale "true".
        var (vm, _, _, auth) = Build(static (_, _) => Task.FromResult(true));
        ((INavigationAware)vm).OnNavigatedTo(null!);
        Assert.True(vm.IsOrgUnitDeclareEnabled);

        auth.IsFunctionOpenAsyncCore = static (_, _) => Task.FromException<bool>(new InvalidOperationException("DB unreachable"));
        ((INavigationAware)vm).OnNavigatedTo(null!);

        // Catch branch: fail-closed reset, no exception propagates out of the fire-and-forget task.
        Assert.False(vm.IsOrgUnitDeclareEnabled);
    }

    [Fact]
    public void ExecuteOrgUnitDeclareCommand_navigates_the_content_region_to_the_orgunit_screen()
    {
        var (vm, nav, _, _) = Build();

        vm.ExecuteOrgUnitDeclareCommand.Execute();

        Assert.Equal(MainWindowViewModel.ContentRegionName, nav.LastRegion);
        Assert.Equal("OrgUnitDeclarationView", nav.LastTarget);
    }

    [Fact]
    public async Task IsOrgUnitDeclareEnabled_stays_disabled_when_a_stale_true_resolves_after_a_newer_false()
    {
        // Two rapid navigations start unguarded fire-and-forget permission checks. Prove the OLDER
        // (stale) check landing LAST with `true` cannot re-enable a tile a NEWER check already
        // resolved to `false` -- a security-relevant fail-open if unguarded (generation-counter idiom).
        var staleGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, _, auth) = Build((_, _) => staleGate.Task);

        ((INavigationAware)vm).OnNavigatedTo(null!); // older, in-flight check -- still pending

        auth.IsFunctionOpenAsyncCore = static (_, _) => Task.FromResult(false);
        ((INavigationAware)vm).OnNavigatedTo(null!); // newer check resolves false synchronously
        vm.IsOrgUnitDeclareEnabled.Should().BeFalse();

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsOrgUnitDeclareEnabled)) changed.TrySetResult(); };

        staleGate.SetResult(true); // stale older check resolves true LAST
        await Task.WhenAny(changed.Task, Task.Delay(500, TestContext.Current.CancellationToken));

        vm.IsOrgUnitDeclareEnabled.Should().BeFalse(
            "a stale in-flight permission check resolving true after a newer check already resolved false must not re-enable the tile");
    }

    [Fact]
    public async Task IsRoleDeclareEnabled_stays_disabled_when_a_stale_true_resolves_after_a_newer_false()
    {
        // Twin of the OrgUnit case above -- same unguarded fire-and-forget shape on the Role gate.
        var staleGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, _, auth) = Build((_, _) => staleGate.Task);

        ((INavigationAware)vm).OnNavigatedTo(null!); // older, in-flight check -- still pending

        auth.IsFunctionOpenAsyncCore = static (_, _) => Task.FromResult(false);
        ((INavigationAware)vm).OnNavigatedTo(null!); // newer check resolves false synchronously
        vm.IsRoleDeclareEnabled.Should().BeFalse();

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsRoleDeclareEnabled)) changed.TrySetResult(); };

        staleGate.SetResult(true); // stale older check resolves true LAST
        await Task.WhenAny(changed.Task, Task.Delay(500, TestContext.Current.CancellationToken));

        vm.IsRoleDeclareEnabled.Should().BeFalse(
            "a stale in-flight permission check resolving true after a newer check already resolved false must not re-enable the tile");
    }

    [Fact]
    public void ExecuteRoleDeclareCommand_navigates_the_content_region_to_the_role_screen()
    {
        var (vm, nav, _, _) = Build();

        vm.ExecuteRoleDeclareCommand.Execute();

        Assert.Equal(MainWindowViewModel.ContentRegionName, nav.LastRegion);
        Assert.Equal(ConfigurationStationTargets.RoleDeclaration, nav.LastTarget);
    }
}
