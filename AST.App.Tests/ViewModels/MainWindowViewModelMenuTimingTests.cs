using AST.Core.Iam;
using AST.Core.Startup;
using AST.Shell.Navigation;
using AST.Shell.ViewModels;
using FluentAssertions;
using Moq;
using Prism.Navigation.Regions;

namespace AST.App.Tests.ViewModels;

// Regression for the "plug-in business module functions never appear on the sidebar" bug: MainWindowViewModel's
// ctor used to build MainMenu/FooterMenu eagerly, freezing whatever IFunctionRegistry held at CreateShell() time
// -- BEFORE modules load. Uses the REAL FunctionRegistry + MenuGroupCatalog + SidebarMenuBuilder pipeline (not a
// mocked ISidebarMenuBuilder) so a test that only registered before the VM's ctor would NOT catch the bug: the
// registration below happens strictly AFTER the ctor runs, simulating a module's OnInitialized().
public sealed class MainWindowViewModelMenuTimingTests
{
    [Fact]
    public void FunctionRegistered_AfterCtor_ButBeforeFirstMenuRead_AppearsOnSidebar()
    {
        var registry = new FunctionRegistry();
        var builder = new SidebarMenuBuilder(registry, new MenuGroupCatalog());
        var vm = new MainWindowViewModel(
            new Mock<IRegionManager>().Object,
            new StartupStatusViewModel(new Mock<IStartupState>().Object),
            builder);

        // Simulates a plug-in module registering its function during InitializeModules(), which Prism runs
        // AFTER CreateShell() (i.e. after the VM above was already constructed) and BEFORE the sidebar is ever
        // read/rendered.
        registry.Register(new FunctionDescriptor(
            FunctionKey: "Txn.Report.View",
            BusinessCode: "FX001",
            DisplayName: "Báo cáo bán hàng",
            MenuGroupCode: MenuGroupCodes.TransactionAccounting,
            NavTarget: "TxnReportView",
            RequiredPermission: "Txn.Report.View",
            Order: 0));

        // First read of MainMenu -- must reflect the registration that happened after the ctor.
        var group = vm.MainMenu.Single(g => g.Title == "Kế toán giao dịch");
        group.Children.Should().Contain(leaf => leaf.Title == "FX001 - Báo cáo bán hàng");
    }

    [Fact]
    public void MainMenu_BuiltOnlyOnce_SecondRegistration_AfterFirstRead_IsNotPickedUp()
    {
        var registry = new FunctionRegistry();
        var builder = new SidebarMenuBuilder(registry, new MenuGroupCatalog());
        var vm = new MainWindowViewModel(
            new Mock<IRegionManager>().Object,
            new StartupStatusViewModel(new Mock<IStartupState>().Object),
            builder);

        _ = vm.MainMenu; // first read -- builds + caches

        registry.Register(new FunctionDescriptor(
            FunctionKey: "Txn.Report.View2",
            BusinessCode: "FX002",
            DisplayName: "Báo cáo mới",
            MenuGroupCode: MenuGroupCodes.TransactionAccounting,
            NavTarget: "TxnReportView2",
            RequiredPermission: "Txn.Report.View2",
            Order: 0));

        var group = vm.MainMenu.Single(g => g.Title == "Kế toán giao dịch");
        group.Children.Should().NotContain(leaf => leaf.Title == "FX002 - Báo cáo mới");
    }

    // Acceptance criterion 2: Iam.OrgUnit.Declare (ConfigSecurity group, never listed in MenuGroupCatalog.Groups)
    // must stay absent from the sidebar even after the timing fix -- its real entry point is the "Khai báo đơn
    // vị" card inside Trạm cấu hình (ConfigurationStationViewModel), not a top-level sidebar leaf.
    [Fact]
    public void ConfigSecurityGroupedFunction_NeverAppearsOnSidebar()
    {
        var registry = new FunctionRegistry();
        var builder = new SidebarMenuBuilder(registry, new MenuGroupCatalog());
        var vm = new MainWindowViewModel(
            new Mock<IRegionManager>().Object,
            new StartupStatusViewModel(new Mock<IStartupState>().Object),
            builder);

        registry.Register(new FunctionDescriptor(
            FunctionKey: "Iam.OrgUnit.Declare",
            BusinessCode: "FX001",
            DisplayName: "Khai báo đơn vị",
            MenuGroupCode: MenuGroupCodes.ConfigSecurity,
            NavTarget: "OrgUnitDeclarationView",
            RequiredPermission: "Iam.OrgUnit.Declare",
            Order: 1));

        var allTitles = vm.MainMenu
            .SelectMany(g => g.Children)
            .Select(c => c.Title)
            .Concat(vm.MainMenu.Select(g => g.Title));

        allTitles.Should().NotContain(t => t.Contains("Khai báo đơn vị"));
    }
}
