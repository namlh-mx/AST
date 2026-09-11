using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AST.Controls;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Controls;

// B1, B4–B7 need no focus. B2, B3, B8, B9 are FocusManager supporting evidence (card 330).
public class AstOverlayHostTests
{
    [Fact]
    public void B1_closed_host_is_collapsed_with_no_hit_testing_focus_or_tab_stop() => Sta.Run(() =>
    {
        var host = new AstOverlayHost();

        host.IsOpen.Should().BeFalse();
        host.Visibility.Should().Be(Visibility.Collapsed);
        host.IsHitTestVisible.Should().BeFalse();
        host.Focusable.Should().BeFalse();
        host.IsTabStop.Should().BeFalse();
    });

    [Fact]
    public void B5_B6_escape_that_reaches_the_host_raises_close_requested_and_does_not_self_close()
        => OffscreenHost.Run(
            _ => BuildHost(new Button { Content = "Close" }, new UiTextBox { Text = "a" }),
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();
                var closes = 0;
                host.CloseRequested += (_, _) => closes++;
                var button = Find<Button>(host);

                var args = BubblingKey.Raise(button, Key.Escape);

                closes.Should().Be(1);
                host.IsOpen.Should().BeTrue("B5: the host never self-closes");
                args.Handled.Should().BeTrue();
            });

    [Fact]
    public void B6_already_handled_escape_does_not_raise_close_requested()
        => OffscreenHost.Run(
            _ => BuildHost(new Button { Content = "Close" }, new UiTextBox { Text = "a" }),
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();
                var closes = 0;
                host.CloseRequested += (_, _) => closes++;
                var button = Find<Button>(host);
                var source = PresentationSource.FromVisual(button)!;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, timestamp: 0, key: Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                    Handled = true
                };

                button.RaiseEvent(args);

                closes.Should().Be(0, "handledEventsToo must be false so a field-handled Esc never reaches the host");
            });

    [Fact]
    public void B7_setting_IsOpen_false_collapses_without_raising_close_requested()
        => OffscreenHost.Run(
            _ => BuildHost(new Button { Content = "Close" }, new UiTextBox { Name = "Editor", Text = "keep" }),
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();
                var editor = Find<UiTextBox>(host);
                editor.Focus();
                Sta.PumpToIdle();
                var closes = 0;
                host.CloseRequested += (_, _) => closes++;

                host.IsOpen = false;
                Sta.PumpToIdle();

                closes.Should().Be(0);
                host.Visibility.Should().Be(Visibility.Collapsed);
                host.IsHitTestVisible.Should().BeFalse();
            });

    [Fact]
    public void B4_focus_may_leave_the_host_for_an_element_outside_it()
        => OffscreenHost.Run(window =>
            {
                var outside = new Button { Name = "Outside", Content = "Out" };
                var host = BuildHost(new Button { Content = "In" }, new UiTextBox());
                var root = new DockPanel();
                root.Children.Add(outside);
                root.Children.Add(host);
                window.Tag = outside;
                return root;
            },
            (window, root) =>
            {
                var host = Find<AstOverlayHost>(root);
                var outside = (Button)window.Tag;
                host.IsOpen = true;
                Sta.PumpToIdle();

                outside.Focus();
                Sta.PumpToIdle();

                FocusManager.GetFocusedElement(window).Should().Be(outside,
                    "supporting evidence B4: containment is traversal-scoped, not a lost-focus veto");
            });

    [Fact]
    public void B2_open_focuses_the_first_enabled_textbox_not_a_preceding_button()
        => OffscreenHost.Run(
            _ => BuildHost(
                new Button { Content = "Lưu" },
                new Button { Content = "Đóng" },
                new UiTextBox { Name = "FirstEditor", Text = "one" }),
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();

                var editor = Find<UiTextBox>(host);
                FocusManager.GetFocusedElement(window).Should().Be(editor,
                    "supporting evidence B2: first enabled revert-enabled editor, not the action buttons");
            });

    [Fact]
    public void B3_tab_cycle_stays_inside_the_open_host()
        => OffscreenHost.Run(
            _ => BuildHost(
                new Button { Content = "Lưu" },
                new UiTextBox { Text = "one" },
                new UiTextBox { Text = "two" }),
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();
                var start = FocusManager.GetFocusedElement(window) as UIElement;
                start.Should().NotBeNull();

                IInputElement? current = start;
                for (var i = 0; i < 6; i++)
                {
                    ((UIElement)current!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Sta.PumpToIdle();
                    current = FocusManager.GetFocusedElement(window);
                    current.Should().BeAssignableTo<DependencyObject>();
                    IsDescendant(host, (DependencyObject)current!).Should().BeTrue(
                        "B3: MoveFocus(Next) must not land behind the scrim");
                }

                current = start;
                start!.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                Sta.PumpToIdle();
                current = FocusManager.GetFocusedElement(window);
                IsDescendant(host, (DependencyObject)current!).Should().BeTrue(
                    "B3: MoveFocus(Previous) must not land behind the scrim");
            });

    [Fact]
    public void B8_restore_contained_focus_returns_to_the_descendant_that_had_it()
        => OffscreenHost.Run(window =>
            {
                var outside = new Button { Name = "Outside", Content = "Out" };
                var host = BuildHost(new Button { Content = "Đóng" }, new UiTextBox { Name = "Editor" });
                var root = new DockPanel();
                root.Children.Add(outside);
                root.Children.Add(host);
                window.Tag = outside;
                return root;
            },
            (window, root) =>
            {
                var host = Find<AstOverlayHost>(root);
                var outside = (Button)window.Tag;
                host.IsOpen = true;
                Sta.PumpToIdle();
                var editor = Find<UiTextBox>(host);
                editor.Focus();
                Sta.PumpToIdle();

                outside.Focus();
                Sta.PumpToIdle();

                host.RestoreContainedFocus();
                Sta.PumpToIdle();

                FocusManager.GetFocusedElement(window).Should().Be(editor,
                    "supporting evidence B8: restore the descendant that had focus, not the outside confirm");
            });

    [Fact]
    public void B9_closing_returns_focus_to_the_opener()
        => OffscreenHost.Run(window =>
            {
                var opener = new Button { Name = "Opener", Content = "Thông tin bổ sung" };
                var host = BuildHost(new UiTextBox { Text = "x" });
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
                opener.Focus();
                Sta.PumpToIdle();
                host.IsOpen = true;
                Sta.PumpToIdle();

                host.IsOpen = false;
                Sta.PumpToIdle();

                FocusManager.GetFocusedElement(window).Should().Be(opener,
                    "supporting evidence B9: focus returns to the element that opened the host");
            });

    private static AstOverlayHost BuildHost(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return new AstOverlayHost { Content = panel };
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var node in Walk(root))
        {
            if (node is T match)
                return match;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} under {root.GetType().Name}");
    }

    private static bool IsDescendant(DependencyObject root, DependencyObject candidate)
    {
        for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
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
