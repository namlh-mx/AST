using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AST.Controls;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Controls;

// B1, B4–B7 need no focus. B2, B3, B8, B9 are FocusManager supporting evidence (card 330 / 342).
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
    public void B2_open_focuses_the_marked_eligible_descendant_not_a_preceding_button_or_unmarked_textbox()
        => OffscreenHost.Run(
            window =>
            {
                var opener = new Button { Name = "Opener", Content = "Thông tin bổ sung" };
                var unmarked = new UiTextBox { Name = "Unmarked", Text = "one" };
                var marked = new UiTextBox { Name = "Marked", Text = "two" };
                AstOverlayHost.SetIsDefaultFocus(marked, true);
                var host = BuildHost(
                    new Button { Content = "Lưu" },
                    unmarked,
                    marked);
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
                var marked = FindNamed<UiTextBox>(host, "Marked");
                opener.Focus();
                Sta.PumpToIdle();
                host.IsOpen = true;
                Sta.PumpToIdle();

                FocusManager.GetFocusedElement(window).Should().Be(marked,
                    "A8.1: an eligible marker beats a preceding button and an unmarked TextBoxBase");
            });

[Fact]
    public void B2_only_an_eligible_marker_wins_over_disabled_or_hidden_markers()
        => OffscreenHost.Run(
            window =>
            {
                var opener = new Button { Name = "Opener", Content = "Thông tin bổ sung" };
                var disabled = new UiTextBox { Name = "Disabled", Text = "no" };
                disabled.IsEnabled = false;
                AstOverlayHost.SetIsDefaultFocus(disabled, true);
                var hidden = new UiTextBox { Name = "Hidden", Text = "no" };
                hidden.Visibility = Visibility.Collapsed;
                AstOverlayHost.SetIsDefaultFocus(hidden, true);
                var eligible = new UiTextBox { Name = "Eligible", Text = "yes" };
                AstOverlayHost.SetIsDefaultFocus(eligible, true);
                var host = BuildHost(disabled, hidden, eligible);
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
                var eligible = FindNamed<UiTextBox>(host, "Eligible");
                opener.Focus();
                Sta.PumpToIdle();
                host.IsOpen = true;
                Sta.PumpToIdle();

                FocusManager.GetFocusedElement(window).Should().Be(eligible,
                    "A8.2: a disabled or hidden marker is skipped; the eligible marked control wins");
            });

    [Fact]
    public void B2_zero_marker_fallback_uses_ordinary_traversal_and_lands_on_a_preceding_button()
        => OffscreenHost.Run(
            window =>
            {
                var opener = new Button { Name = "Opener", Content = "Thông tin bổ sung" };
                var button = new Button { Name = "FirstButton", Content = "Lưu" };
                var host = BuildHost(
                    button,
                    new UiTextBox { Name = "Unmarked", Text = "one" });
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
                var button = FindNamed<Button>(host, "FirstButton");
                opener.Focus();
                Sta.PumpToIdle();
                host.IsOpen = true;
                Sta.PumpToIdle();

                var focused = FocusManager.GetFocusedElement(window);
                focused.Should().NotBe(opener, "A8.3: fallback must leave the opener");
                focused.Should().Be(button,
                    "A8.3: zero-marker fallback is ordinary WPF traversal, not a TextBoxBase preference");
            });

    [Fact]
    public void B3_tab_cycle_wraps_inside_the_open_host_and_never_reaches_a_sibling_outside_it()
        => OffscreenHost.Run(
            window =>
            {
                var first = new Button { Name = "First", Content = "Lưu" };
                var last = new UiTextBox { Name = "Last", Text = "one" };
                var host = BuildHost(first, last);
                var outside = new Button { Name = "Outside", Content = "Ngoài" };
                var root = new StackPanel();
                root.Children.Add(host);
                root.Children.Add(outside);
                return root;
            },
            (window, root) =>
            {
                var host = Find<AstOverlayHost>(root);
                var first = FindNamed<Button>(host, "First");
                var last = FindNamed<UiTextBox>(host, "Last");
                var outside = FindNamed<Button>(root, "Outside");
                host.IsOpen = true;
                Sta.PumpToIdle();

                last.Focus();
                Sta.PumpToIdle();
                last.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Sta.PumpToIdle();
                FocusManager.GetFocusedElement(window).Should().Be(first,
                    "B3: Cycle wraps forward from the last descendant to the first");

                first.Focus();
                Sta.PumpToIdle();
                first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                Sta.PumpToIdle();
                FocusManager.GetFocusedElement(window).Should().Be(last,
                    "B3: Cycle wraps backward from the first descendant to the last");

                var current = first as IInputElement;
                for (var i = 0; i < 6; i++)
                {
                    ((UIElement)current!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Sta.PumpToIdle();
                    current = FocusManager.GetFocusedElement(window);
                    current.Should().NotBe(outside, "B3: a sibling outside the host is never reached");
                    current.Should().BeAssignableTo<DependencyObject>();
                    IsDescendant(host, (DependencyObject)current!).Should().BeTrue();
                }
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

private static T FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T self && self.Name == name)
            return self;

        foreach (var node in Walk(root))
        {
            if (node is T match && match.Name == name)
                return match;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} named {name}");
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
