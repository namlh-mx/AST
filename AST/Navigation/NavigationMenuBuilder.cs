using System;
using System.Collections.Generic;
using System.Windows.Input;
using AST.Shell.Navigation;
using Wpf.Ui.Controls;

namespace AST.Navigation;

// View-layer helper (exe) that projects the BCL SidebarNode tree onto WPF-UI NavigationViewItems. This is the
// single place the shell binds System.Windows / WPF-UI menu types, so MainWindowViewModel stays WPF-free.
//   Group node (TargetViewName == null): a header item with child MenuItems, no command.
//   Leaf node  (TargetViewName set)    : Command = navigateCommand, CommandParameter = the node itself (carries
//                                        the Prism target + the title shown by ComingSoonView).
internal static class NavigationMenuBuilder
{
    public static IReadOnlyList<NavigationViewItem> Build(IReadOnlyList<SidebarNode> nodes, ICommand navigateCommand)
    {
        var items = new List<NavigationViewItem>(nodes.Count);
        foreach (var node in nodes)
        {
            items.Add(ToItem(node, navigateCommand));
        }
        return items;
    }

    private static NavigationViewItem ToItem(SidebarNode node, ICommand navigateCommand)
    {
        var item = new NavigationViewItem { Content = node.Title };

        if (TryResolveIcon(node.IconKey, out var icon))
        {
            item.Icon = icon;
        }

        if (node.TargetViewName is { Length: > 0 })
        {
            item.Command = navigateCommand;
            item.CommandParameter = node;
        }

        foreach (var child in node.Children)
        {
            item.MenuItems.Add(ToItem(child, navigateCommand));
        }

        return item;
    }

    // IconKey is a Fluent SymbolRegular name (kept as a string in AST.Core to stay WPF-free). Unknown/blank keys
    // degrade to no icon rather than throwing.
    private static bool TryResolveIcon(string? iconKey, out IconElement? icon)
    {
        if (!string.IsNullOrEmpty(iconKey) && Enum.TryParse<SymbolRegular>(iconKey, out var symbol))
        {
            icon = new SymbolIcon { Symbol = symbol };
            return true;
        }
        icon = null;
        return false;
    }
}
