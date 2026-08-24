using System;
using System.Threading.Tasks;
using AST.Core.Iam;
using AST.Shell.Navigation;
using AST.Shell.Session;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace AST.ViewModels.Platform;

// Configuration Station VM (exe — uses IRegionManager from Prism.Wpf, which AST.Shell cannot reference). The
// Execute buttons navigate the ContentRegion to the existing config screens; the DB button is enabled only
// after a successful admin authentication (the rule that used to gate the DB footer leaf, spec §2/§9).
public sealed class ConfigurationStationViewModel : BindableBase, INavigationAware
{
    private const string OrgUnitDeclareFunctionKey = "Iam.OrgUnit.Declare";
    private const string RoleDeclareFunctionKey = "Iam.Role.Declare";

    private readonly IRegionManager _regionManager;
    private readonly IAdminSession _session;
    private readonly ICurrentWindowsUser _currentUser;
    private readonly IAuthorizationService _authorization;

    public ConfigurationStationViewModel(
        IRegionManager regionManager,
        IAdminSession session,
        ICurrentWindowsUser currentUser,
        IAuthorizationService authorization)
    {
        _regionManager = regionManager;
        _session = session;
        _currentUser = currentUser;
        _authorization = authorization;
        _session.Changed += (_, _) => RaisePropertyChanged(nameof(IsDbConfigEnabled));
    }

    // Prism INavigatedAware — re-checks permission on every navigation to this screen so that
    // a user who fixed their DB connection on this same screen gets an accurate button state
    // without restarting the app (a ctor-only check would stay false for the session lifetime).
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        _ = RefreshOrgUnitDeclarePermissionAsync();
        _ = RefreshRoleDeclarePermissionAsync();
    }

    public void OnNavigatedFrom(NavigationContext navigationContext) { }

    // Gates the "Cơ sở dữ liệu" Execute button (dimmed until the admin is authenticated).
    public bool IsDbConfigEnabled => _session.IsAuthenticated;

    private DelegateCommand? _executeAdminAuthCommand;
    public DelegateCommand ExecuteAdminAuthCommand =>
        _executeAdminAuthCommand ??= new DelegateCommand(
            () => Navigate(ConfigurationStationTargets.AdminAuth));

    private DelegateCommand? _executeDbConfigCommand;
    public DelegateCommand ExecuteDbConfigCommand =>
        _executeDbConfigCommand ??= new DelegateCommand(
            () => Navigate(ConfigurationStationTargets.ConnectionDeclaration));

    private bool _isOrgUnitDeclareEnabled;

    // Gates the "Khai báo đơn vị" Execute button. Unlike IsDbConfigEnabled (admin-session gated),
    // this gate is permission-based (role_permission grant on Iam.OrgUnit.Declare) and fail-closed.
    public bool IsOrgUnitDeclareEnabled
    {
        get => _isOrgUnitDeclareEnabled;
        private set => SetProperty(ref _isOrgUnitDeclareEnabled, value);
    }

    private int _orgUnitDeclareGeneration;

    private async Task RefreshOrgUnitDeclarePermissionAsync()
    {
        // Generation-counter idiom (same shape as RoleDeclarationViewModel's _cardLoadGeneration
        // etc.): OnNavigatedTo fires this fire-and-forget on every navigation, so two rapid
        // navigations can have the OLDER check resolve LAST. Capture the generation before the
        // await and only write the property if this call still owns the latest generation --
        // otherwise a stale "true" could re-enable a tile a newer check already resolved false.
        var generation = ++_orgUnitDeclareGeneration;
        try
        {
            var username = _currentUser.Username ?? "unknown";
            var isOpen = await _authorization.IsFunctionOpenAsync(username, OrgUnitDeclareFunctionKey);
            if (generation == _orgUnitDeclareGeneration)
                IsOrgUnitDeclareEnabled = isOpen;
        }
        catch (Exception ex)
        {
            // Fail-closed even on a RE-check fault: a prior successful grant must not stay
            // enabled if a later navigation's check throws (e.g. a transient DB hiccup) --
            // otherwise the button would keep the stale "true" from the last successful check.
            // Still generation-guarded: a superseded check's failure must not clobber a newer
            // check's already-written result.
            if (generation == _orgUnitDeclareGeneration)
                IsOrgUnitDeclareEnabled = false;
            Serilog.Log.Error(ex, "Không kiểm tra được quyền Khai báo đơn vị.");
        }
    }

    private DelegateCommand? _executeOrgUnitDeclareCommand;
    public DelegateCommand ExecuteOrgUnitDeclareCommand =>
        _executeOrgUnitDeclareCommand ??= new DelegateCommand(
            () => Navigate(ConfigurationStationTargets.OrgUnitDeclaration));

    private bool _isRoleDeclareEnabled;

    // Gates the "Khai báo vai trò" Execute button -- same permission fail-closed pattern as OrgUnit.
    public bool IsRoleDeclareEnabled
    {
        get => _isRoleDeclareEnabled;
        private set => SetProperty(ref _isRoleDeclareEnabled, value);
    }

    private int _roleDeclareGeneration;

    private async Task RefreshRoleDeclarePermissionAsync()
    {
        // Same generation-counter guard as RefreshOrgUnitDeclarePermissionAsync above.
        var generation = ++_roleDeclareGeneration;
        try
        {
            var username = _currentUser.Username ?? "unknown";
            var isOpen = await _authorization.IsFunctionOpenAsync(username, RoleDeclareFunctionKey);
            if (generation == _roleDeclareGeneration)
                IsRoleDeclareEnabled = isOpen;
        }
        catch (Exception ex)
        {
            if (generation == _roleDeclareGeneration)
                IsRoleDeclareEnabled = false;
            Serilog.Log.Error(ex, "Không kiểm tra được quyền Khai báo vai trò.");
        }
    }

    private DelegateCommand? _executeRoleDeclareCommand;
    public DelegateCommand ExecuteRoleDeclareCommand =>
        _executeRoleDeclareCommand ??= new DelegateCommand(
            () => Navigate(ConfigurationStationTargets.RoleDeclaration));

    private void Navigate(string viewName) =>
        _regionManager.RequestNavigate(MainWindowViewModel.ContentRegionName, viewName);
}
