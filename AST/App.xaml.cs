using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AST.Controls;
using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Presentation;
using AST.Core.Security;
using AST.Core.Startup;
using AST.Core.Time;
using AST.Infrastructure.Logging;
using AST.Infrastructure.Security;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Navigation.Regions;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace AST;

public partial class App : PrismApplication
{
    // V001..V010 currently exist -> the app expects schema version 10 (re-verify when adding a migration).
    private const int ExpectedSchemaVersion = 10;

    // Last line of defense (rule-platform-infra #1): every async void handler in this app is expected to
    // try/catch itself, but there is otherwise NO app-level net -- an exception escaping any of them (or a
    // sync UI-thread call) silently kills the process with no Log.Error and no user-facing message. Subscribe
    // in the constructor (before OnStartup/OnInitialized) so the net is up for the entire dispatcher lifetime.
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // Config security chain (Slice #1) — built manually then registered as an instance (has a primitive requireSignature
        // + a string public key, so it cannot be auto-wired).
        var paths = ConfigPathResolver.Resolve(AppContext.BaseDirectory);
        var requireSignature = RequireConfigSignature.Value;
        IConfigSignature signature = new EcdsaConfigSignature(RootPublicKey.Value);
        IConfigProtector protector = new AesConfigProtector();
        IConnectionConfigStore connectionStore =
            new FileConnectionConfigStore(signature, protector, paths, requireSignature);
        IBreakGlassStore breakGlassStore =
            new FileBreakGlassStore(signature, paths, requireSignature);

        containerRegistry.RegisterInstance(signature);
        containerRegistry.RegisterInstance(protector);
        containerRegistry.RegisterInstance(connectionStore);
        containerRegistry.RegisterInstance(breakGlassStore);

        // Share-based hash-chained config-audit log (Slice A1) — built via factory (primitive public-key string
        // cannot be auto-wired); reuses the IClock + ICurrentWindowsUser singletons registered below. Fail-clear.
        containerRegistry.RegisterSingleton<IConfigAuditLog>(() => new FileConfigAuditLog(
            signature, paths,
            Container.Resolve<ICurrentWindowsUser>(),
            Container.Resolve<IClock>(),
            RootPublicKey.Value));

        // File B management service (primitive `requireSignature` -> factory, like the other config-security seams).
        containerRegistry.RegisterSingleton<AST.Core.Data.IBreakGlassAdminService>(() =>
            new AST.Infrastructure.Security.BreakGlassAdminService(
                breakGlassStore,
                Container.Resolve<IConfigAuditLog>(),
                paths,
                requireSignature));

        // Cross-cutting concerns that IamModule intentionally does NOT register itself (rule-module-boundary §B2).
        containerRegistry.RegisterSingleton<IConnectionStringProvider, FileAConnectionStringProvider>();
        containerRegistry.RegisterSingleton<IClock, SystemClock>();
        containerRegistry.RegisterSingleton<IBusinessDateProvider, SystemBusinessDateProvider>();
        containerRegistry.RegisterSingleton<IFunctionRegistry, FunctionRegistry>();
        containerRegistry.RegisterSingleton<IAuditLogWriter, AST.Infrastructure.AuditLogWriter>();

        // Sidebar menu build (Shell chrome): the data-driven menu tree reuses IFunctionRegistry + the group catalog.
        containerRegistry.RegisterSingleton<AST.Shell.Navigation.IMenuGroupCatalog, AST.Shell.Navigation.MenuGroupCatalog>();
        containerRegistry.RegisterSingleton<AST.Shell.Navigation.ISidebarMenuBuilder, AST.Shell.Navigation.SidebarMenuBuilder>();

        // Fluent modal dialogs (Shell chrome, rule-module-boundary §1c): WPF-UI's own dialog service, wired to
        // RootDialogHost in MainWindow.OnLoaded (SetDialogHost). Reuse the library service as-is (rule-prefer-existing) --
        // it is NOT a Prism region and never hosts navigated business content.
        containerRegistry.RegisterSingleton<IContentDialogService, ContentDialogService>();
        // H2-style warn+confirm for declaration-screen VMs (Screen A and future ones) -- a plain DI
        // service built on AstDialog, not tied to any one screen's View instance.
        containerRegistry.RegisterSingleton<AST.Shell.Presentation.IConfirmationPrompt, AST.Services.AstDialogConfirmationPrompt>();
        containerRegistry.RegisterSingleton<ICurrentWindowsUser, WindowsCurrentUser>();
        containerRegistry.RegisterSingleton<IConnectionTester, MySqlConnectionTester>();
        containerRegistry.RegisterSingleton<IConfigDeclarationService, ConfigDeclarationService>();

        // The real break-glass — the composition root is the ONLY place that registers it (the module dropped its stopgap, Task 5.2).
        containerRegistry.RegisterSingleton<IBreakGlassPolicy, RealBreakGlassPolicy>();

        containerRegistry.RegisterSingleton<IStartupState, StartupState>();

        // §⑤ #3 Declaration UI seams
        containerRegistry.RegisterSingleton<AST.Core.Security.IAdminKeyVerifier, AdminKeyVerifier>();
        containerRegistry.RegisterSingleton<AST.Shell.Session.IAdminSession, AST.Shell.Session.AdminSession>();
        containerRegistry.RegisterSingleton<AST.Shell.Services.IFilePickerService, AST.Services.WpfFilePickerService>();

        // ViewModels
        // These VMs subscribe to the app-lifetime singleton services (IAdminSession / IStartupState) in their
        // constructors; they are registered singleton so subscriber lifetime == publisher lifetime. A transient
        // registration would leak (the singleton keeps the handler) and let a superseded VM keep reacting to
        // session/startup events.
        containerRegistry.RegisterSingleton<AST.Shell.ViewModels.StartupStatusViewModel>();
        containerRegistry.Register<AST.Shell.ViewModels.Platform.ConnectionDeclarationViewModel>();
        containerRegistry.RegisterSingleton<AST.Shell.ViewModels.Platform.BreakGlassAdminViewModel>();
        containerRegistry.RegisterSingleton<AST.Shell.ViewModels.Platform.ConfigAuditHistoryViewModel>();

        // AdminAuthViewModel has a primitive `bool allowDebugSkip` ctor arg -- DryIoc cannot auto-wire a
        // primitive, so build it via a factory (rule-platform-infra §2 primitive-ctor trap). It also subscribes
        // to its two child VMs (PropertyChanged/Saved), so it must be singleton too -- transient would just move
        // the leak up one level.
        containerRegistry.RegisterSingleton(typeof(AST.Shell.ViewModels.Platform.AdminAuthViewModel), () =>
            new AST.Shell.ViewModels.Platform.AdminAuthViewModel(
                Container.Resolve<AST.Core.Security.IAdminKeyVerifier>(),
                Container.Resolve<AST.Shell.Session.IAdminSession>(),
                Container.Resolve<AST.Shell.Services.IFilePickerService>(),
                Container.Resolve<AST.Core.Iam.ICurrentWindowsUser>(),
                Container.Resolve<AST.Shell.ViewModels.Platform.BreakGlassAdminViewModel>(),
                Container.Resolve<AST.Shell.ViewModels.Platform.ConfigAuditHistoryViewModel>(),
                allowDebugSkip: !RequireConfigSignature.Value));

        // IStartupRunner is resolved lazily inside OnInitialized (not eagerly here) because it needs the
        // RESOLVED ISchemaVersionChecker IMPLEMENTATION, which DryIoc can only provide after Prism has loaded
        // modules (IamModule.RegisterTypes registers the concrete SchemaVersionChecker) (rule-platform-infra §2).
        containerRegistry.RegisterSingleton<IStartupRunner>(() => new AST.Startup.StartupRunner(
            Container.Resolve<IConnectionConfigStore>(),
            Container.Resolve<IConnectionTester>(),
            Container.Resolve<ISchemaVersionChecker>(),
            Container.Resolve<IStartupState>(),
            Container.Resolve<IConfigAuditLog>(),
            Container.Resolve<IFunctionCatalogSync>(),
            ExpectedSchemaVersion));

        containerRegistry.RegisterForNavigation<Views.DashboardView>();
        containerRegistry.RegisterForNavigation<Views.ComingSoonView, AST.Shell.ViewModels.ComingSoonViewModel>();
        // ConfigurationStationViewModel subscribes to IAdminSession.Changed in its ctor (see the ViewModels
        // block above) -- singleton so subscriber lifetime == publisher lifetime. RegisterForNavigation only
        // registers the view + a ViewModelLocationProvider map, not the VM itself, so this is the sole DI
        // registration for the VM; the locator resolves the singleton.
        containerRegistry.RegisterSingleton<AST.ViewModels.Platform.ConfigurationStationViewModel>();
        containerRegistry.RegisterForNavigation<Views.Platform.ConfigurationStationView, AST.ViewModels.Platform.ConfigurationStationViewModel>();
        containerRegistry.RegisterForNavigation<Views.HelpView>();
        containerRegistry.RegisterForNavigation<Views.Platform.AdminAuthView, AST.Shell.ViewModels.Platform.AdminAuthViewModel>();
        containerRegistry.RegisterForNavigation<Views.Platform.ConnectionDeclarationView, AST.Shell.ViewModels.Platform.ConnectionDeclarationViewModel>();
        containerRegistry.RegisterForNavigation<Views.Iam.OrgUnit.OrgUnitDeclarationView, AST.Shell.ViewModels.Iam.OrgUnitDeclarationViewModel>();
        containerRegistry.RegisterForNavigation<Views.Iam.Role.RoleDeclarationView, AST.Shell.ViewModels.Iam.Role.RoleDeclarationViewModel>();
    }

    // rule-module-boundary §1b (plug-in mechanism, Task X): the Shell does not know the concrete module list
    // ahead of time -- DirectoryModuleCatalog scans Modules/ next to the exe at startup and loads whatever
    // business module DLL is found there (staged by AST.csproj's CopyIamModule* targets). No
    // `moduleCatalog.AddModule<T>()` call and no compile-time reference to any AST.Modules.* assembly anywhere
    // in this file.
    protected override IModuleCatalog CreateModuleCatalog() =>
        new DirectoryModuleCatalog { ModulePath = Path.Combine(AppContext.BaseDirectory, "Modules") };

    protected override void OnInitialized()
    {
        // F1 (qa §⑤ #2): technical logs MUST be written locally to %LOCALAPPDATA%\AST\logs\, NOT to the network
        // share the app is running from (§⑧.4 — avoids congestion/file contention when ~30 users write concurrently).
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AST", "logs");
        // F2 (qa §⑤ #2): fail-safe logger initialization -- BootstrapLoggerFactory swallows errors creating the
        // directory/sink (permissions, contention) on its own and falls back to a no-op logger, does NOT throw and crash the app at this edge.
        Log.Logger = BootstrapLoggerFactory.Create(logDir);

        // UI standardization: switch the WPF-UI accent to the Agribank Red brand color #ae1c3e (= AstPrimaryBrush).
        // Applied here (edge) inside a try/catch so a theming failure never crashes app startup (rule-platform-infra §1).
        try
        {
            ApplicationAccentColorManager.Apply(Color.FromRgb(0xAE, 0x1C, 0x3E), ApplicationTheme.Light);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply brand accent color; falling back to default WPF-UI accent.");
        }

        // [Platform] First production IFunctionRegistry.Register call -- gives IAuthorizationService /
        // FunctionCatalogSyncService a real function to grant/sync (previously registry.All was always
        // empty). MenuGroupCode reuses ConfigSecurity, which is never listed in MenuGroupCatalog.Groups, so
        // SidebarMenuBuilder never renders this as a left-sidebar leaf -- Trạm cấu hình's "Khai báo đơn vị"
        // card (ConfigurationStationViewModel) is the real entry point, gated by role_permission, not this
        // registration. B3 (user declaration) will register the same way when built.
        var functionRegistry = Container.Resolve<IFunctionRegistry>();
        functionRegistry.Register(new FunctionDescriptor(
            "Iam.OrgUnit.Declare", "FX001", "Khai báo đơn vị",
            MenuGroupCodes.ConfigSecurity, ViewNames.OrgUnitDeclaration,
            "Iam.OrgUnit.Declare", Order: 1));

        // Iam.Role.Declare matches RoleDeclarationService's FunctionKey constant exactly
        // (case-sensitive) so IAuthorizationService.AuthorizeAsync can resolve it once
        // FunctionCatalogSyncService.SyncAsync() syncs this descriptor into the `function` table.
        // Same ConfigSecurity-group / no-sidebar-leaf reasoning as OrgUnit above -- the entry point
        // is Trạm cấu hình's "Khai báo vai trò" card (ConfigurationStationViewModel), gated by
        // role_permission, not this registration. RoleDeclarationView IS now registered for
        // navigation (above) and reachable from that card.
        functionRegistry.Register(new FunctionDescriptor(
            "Iam.Role.Declare", "FX002", "Khai báo vai trò",
            MenuGroupCodes.ConfigSecurity, ViewNames.RoleDeclaration,
            "Iam.Role.Declare", Order: 2));

        // Seal the registry here, NOT earlier: by this point in PrismApplicationBase.Initialize(),
        // InitializeModules() has ALREADY run (it executes between CreateShell() and this OnInitialized()
        // override -- verified against Prism's own source, not assumed), so every IModule's own function
        // registrations are done, and the line above is the app's own last registration. Any Register() call
        // reaching the registry after this point is a genuine late-registration bug (or a module registering on
        // some deferred/async path) -- Seal() turns that into a loud diagnostic (FunctionRegistry.Register) instead
        // of a silently-missing sidebar item. Must run BEFORE base.OnInitialized() below, which Shows the window
        // and synchronously fires MainWindow.Loaded -- the first (and only) read of MainMenu/FooterMenu.
        if (functionRegistry is FunctionRegistry sealable)
        {
            sealable.Seal();
        }

        // F3 (qa §⑤ #2): the orchestration logic (read File A -> connect DB -> check schema -> catch-all) lives in
        // AST.Core.Startup.StartupOrchestrator via AST.Startup.StartupRunner (rule-prefer-existing -- do not
        // duplicate the resolve logic); Rerun() sets IStartupState + logs the mode line + (on Connected)
        // fires the function-catalog sync so the registration above actually reaches the DB.
        Container.Resolve<IStartupRunner>().Rerun();

        base.OnInitialized();

        // Navigate the default screen so ContentRegion is not blank on first show (modules are loaded and the
        // region is available only after base.OnInitialized()). Dashboard is the landing screen.
        Container.Resolve<IRegionManager>().RequestNavigate(MainWindowViewModel.ContentRegionName, ViewNames.Dashboard);
    }

    // The dispatcher net: fires for any exception raised on the UI thread that escaped every catch below it
    // (e.g. an async void handler that forgot its own try/catch). Mark Handled so WPF's own unhandled-exception
    // crash dialog never appears, log the full trace, then fail CLOSED via a controlled shutdown -- an
    // exception this deep means app/DI/view state may be corrupted, so "carry on" is not a safe option
    // (rule-platform-infra #1).
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error(e.Exception, "Unhandled exception reached the dispatcher; the app will shut down.");

        try
        {
            var dialogs = Container.Resolve<IContentDialogService>();
            _ = ShowFatalErrorAndShutdownAsync(dialogs);
        }
        catch (Exception secondary)
        {
            // Resolving/showing the dialog is itself best-effort -- if IT throws (container torn down,
            // no dialog host attached yet...), still fail closed instead of leaving the process hanging in an
            // unknown state.
            Log.Error(secondary, "Failed to show the fatal-error dialog; shutting down without it.");
            Shutdown(1);
        }
    }

    private async Task ShowFatalErrorAndShutdownAsync(IContentDialogService dialogs)
    {
        try
        {
            await AstDialog.NoticeAsync(
                dialogs,
                // Wording settled by the requester. Applied by the Main Agent rather than the
                // Executor Agent: this file is the composition root, which is excluded from
                // delegation regardless of how small the edit is.
                "Ứng dụng tự động đóng do phát sinh yêu cầu xử lý về kỹ thuật. Người dùng mở lại ứng dụng sau.",
                StatusSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to display the fatal-error dialog.");
        }
        finally
        {
            Shutdown(1);
        }
    }

    // AppDomain-level net: catches exceptions on a NON-UI thread that were never caught (e.g. inside
    // Task.Run without try/catch). IsTerminating is already true by the time this fires and .NET gives no way
    // to cancel that -- the process is going down regardless, so the only thing left to do at this edge is
    // record why (rule-platform-infra #1: Log.Error, never a silent crash) before it happens.
    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(e.ExceptionObject as Exception, "Unhandled exception on a non-UI thread; the process is terminating.");
    }

    // TaskScheduler-level net: fires when a faulted Task's exception was never observed (awaited/caught)
    // before the Task was garbage-collected. Unlike the two above this one does NOT crash the process by
    // default on current .NET -- but a swallowed Task fault would otherwise never reach Serilog at all, so log
    // it and mark it observed to prevent a duplicate report on a later GC pass.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "An unobserved Task exception was garbage-collected without ever being handled.");
        e.SetObserved();
    }
}
