using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AST.Behaviors;
using AST.Controls;
using AST.Converters;
using AST.Core.Iam;
using AST.Views.Iam.OrgUnit;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Views;

// A8 consumer proof for the org-unit supplemental dialog: exactly one locally set marker,
// eligibility, revert-enablement, and opening focus. Card 342.
public class OrgUnitSupplementalDefaultFocusTests
{
    [Fact]
    public void Unlocked_open_focuses_the_single_eligible_marked_business_number_editor()
        => OffscreenHost.Run(
            window =>
            {
                var opener = new Button { Name = "Opener", Content = "Thông tin bổ sung" };
                var dialog = CreateDialog(window);
                var host = new AstOverlayHost { Content = dialog };
                var root = new DockPanel();
                root.Children.Add(opener);
                root.Children.Add(host);
                window.Tag = opener;
                return root;
            },
            (window, root) =>
            {
                var opener = (Button)window.Tag;
                var host = Find<AstOverlayHost>(root);
                var dialog = (OrgUnitSupplementalDialog)host.Content;
                dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
                opener.Focus();
                Sta.PumpToIdle();
                host.IsOpen = true;
                Sta.PumpToIdle();

                var marked = LocallyMarked(dialog).ToList();
                marked.Should().ContainSingle(
                    "A8 consumer: exactly one descendant has a locally set true IsDefaultFocus");
                var target = marked[0];
                target.Should().BeAssignableTo<UIElement>();
                var element = (UIElement)target;
                element.IsEnabled.Should().BeTrue();
                element.IsVisible.Should().BeTrue();
                element.Focusable.Should().BeTrue();
                KeyboardNavigation.GetIsTabStop(element).Should().BeTrue();
                target.Should().BeAssignableTo<UiTextBox>();
                AstFieldRevert.GetIsEnabled((UiTextBox)target).Should().BeTrue(
                    "A8 consumer: the marked target is revert-enabled after Loaded");
                FocusManager.GetFocusedElement(window).Should().Be(target,
                    "A8 consumer: opening the host focuses the marked Mã số kinh doanh editor");
            });

    private static OrgUnitSupplementalDialog CreateDialog(Window window)
    {
        _ = window.FindResource("AstLabelMediumText");
        _ = window.FindResource("AstFieldLabel");
        Application.Current.Resources = window.Resources;
        if (!window.Resources.Contains("InverseBool"))
            window.Resources["InverseBool"] = new InverseBooleanConverter();
        return new OrgUnitSupplementalDialog();
    }

    private static IEnumerable<DependencyObject> LocallyMarked(DependencyObject root)
    {
        foreach (var node in InclusiveWalk(root))
        {
            if (!ReferenceEquals(node.ReadLocalValue(AstOverlayHost.IsDefaultFocusProperty),
                    DependencyProperty.UnsetValue)
                && AstOverlayHost.GetIsDefaultFocus(node))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<DependencyObject> InclusiveWalk(DependencyObject root)
    {
        yield return root;
        foreach (var child in Walk(root))
            yield return child;
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var node in InclusiveWalk(root))
        {
            if (node is T match)
                return match;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} under {root.GetType().Name}");
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        if (count == 0)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Walk(child))
                    yield return nested;
            }

            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Walk(child))
                yield return nested;
        }
    }
}
