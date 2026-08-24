using AST.Core.Iam;
using AST.Shell.Navigation;

namespace AST.Shell.Tests.Navigation;

// Written test-first (Slice 1), ahead of MenuGroupCatalog + PlaceholderMenuSeed + SidebarMenuBuilder
// to make these GREEN. Contract: `new SidebarMenuBuilder(IFunctionRegistry, IMenuGroupCatalog)` +
// `new MenuGroupCatalog()`. Menu is 2 levels (L1 group -> L2 leaf); source of truth = IFunctionRegistry + seed.
public class SidebarMenuBuilderTests
{
    private static readonly string[] ExpectedGroupTitlesInOrder =
    [
        "Kế toán giao dịch",
        "Kế toán nội bộ",
        "Tiền tệ - kho quỹ",
        "Báo cáo quản trị",
        "Kiểm tra - giám sát",
    ];

    private static SidebarMenuBuilder NewBuilder(IFunctionRegistry registry) =>
        new(registry, new MenuGroupCatalog());

    [Fact]
    public void BuildMain_Returns5GroupsInOrder_AllAreGroups()
    {
        var main = NewBuilder(new FunctionRegistry()).BuildMain();

        Assert.Equal(ExpectedGroupTitlesInOrder, main.Select(n => n.Title).ToArray());
        Assert.All(main, g =>
        {
            Assert.Null(g.TargetViewName);          // a group is not itself navigable
            Assert.False(string.IsNullOrEmpty(g.IconKey)); // every L1 group has an icon
        });
    }

    [Fact]
    public void BuildMain_EachGroupHas3PlaceholderLeaves_NamedMenu_N_M_ToComingSoon()
    {
        var main = NewBuilder(new FunctionRegistry()).BuildMain();

        for (var n = 1; n <= 5; n++)
        {
            var group = main[n - 1];
            Assert.Equal([$"Menu {n}.1", $"Menu {n}.2", $"Menu {n}.3"],
                group.Children.Select(c => c.Title).ToArray());
            Assert.All(group.Children, leaf =>
            {
                Assert.Equal("ComingSoonView", leaf.TargetViewName);
                Assert.Empty(leaf.Children);
            });
        }
    }

    [Fact]
    public void BuildMain_RegisteredFunction_AppearsAsLeafInItsGroup_BeforePlaceholders()
    {
        var registry = new FunctionRegistry();
        registry.Register(new FunctionDescriptor(
            FunctionKey: "Txn.Report.View",
            BusinessCode: "FX001",
            DisplayName: "Báo cáo bán hàng",
            MenuGroupCode: MenuGroupCodes.TransactionAccounting,
            NavTarget: "TxnReportView",
            RequiredPermission: "Txn.Report.View",
            Order: 0));

        var group1 = NewBuilder(registry).BuildMain()[0]; // "Kế toán giao dịch"

        var realLeaf = group1.Children[0]; // Order 0 -> before the placeholders (Order 1..3)
        Assert.Equal("FX001 - Báo cáo bán hàng", realLeaf.Title);
        Assert.Equal("TxnReportView", realLeaf.TargetViewName);
        Assert.Equal(4, group1.Children.Count); // 1 real + 3 placeholders
    }

    [Fact]
    public void BuildMain_DoesNotWritePlaceholdersIntoFunctionRegistry()
    {
        var registry = new FunctionRegistry();

        _ = NewBuilder(registry).BuildMain();

        Assert.Empty(registry.All); // placeholders are UI-only; they must never pollute the function catalog
    }

    [Fact]
    public void BuildFooter_HasHelpLeaf_ThenConfigStationLeaf()
    {
        var footer = NewBuilder(new FunctionRegistry()).BuildFooter();

        Assert.Equal(2, footer.Count);

        var help = footer[0];
        Assert.Equal("Trợ giúp", help.Title);
        Assert.Equal("HelpView", help.TargetViewName);
        Assert.Empty(help.Children);

        var config = footer[1];
        Assert.Equal("Cấu hình", config.Title);
        Assert.Equal("ConfigurationStationView", config.TargetViewName);
        Assert.Empty(config.Children);
    }

    [Fact]
    public void MenuGroupCatalog_UsesExpectedIconKeys()
    {
        var byCode = new MenuGroupCatalog().Groups.ToDictionary(g => g.Code, g => g.IconKey);

        Assert.Equal("ReceiptMoney24", byCode[MenuGroupCodes.TransactionAccounting]);
        Assert.Equal("ClipboardTaskListLtr24", byCode[MenuGroupCodes.InternalAccounting]);
        Assert.Equal("Money24", byCode[MenuGroupCodes.Treasury]);
        Assert.Equal("DataTrending24", byCode[MenuGroupCodes.ManagementReport]);
        Assert.Equal("ShieldTask24", byCode[MenuGroupCodes.Inspection]);
    }
}
