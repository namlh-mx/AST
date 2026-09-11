using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AST.Behaviors;
using AST.Controls;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Controls;

// C1–C7 on a real Wpf.Ui.Controls.TextBox descendant of AstOverlayHost. Logical focus plus bubbling
// KeyDown. N3's Tab-out-then-refocus is included so a stale armed session cannot hide itself.
public class AstFieldRevertTests
{
    [Fact]
    public void C1_to_C7_two_press_ladder_on_wpfui_textbox_including_tab_out_refocus()
        => OffscreenHost.Run(
            _ =>
            {
                var editor = new UiTextBox { Name = "Editor" };
                AstFieldRevert.SetIsEnabled(editor, true);
                editor.SetBinding(
                    TextBox.TextProperty,
                    new Binding(nameof(DraftProbe.Phone))
                    {
                        Source = new DraftProbe { Phone = "keep" },
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    });
                var host = new AstOverlayHost
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new Button { Content = "Lưu" },
                            editor,
                            new UiTextBox { Name = "Other" }
                        }
                    }
                };
                AstFieldRevert.SetIsEnabled((UiTextBox)((StackPanel)host.Content).Children[2], true);
                return host;
            },
            (window, host) =>
            {
                host.IsOpen = true;
                Sta.PumpToIdle();
                var editor = FindNamed<UiTextBox>(host, "Editor");
                var other = FindNamed<UiTextBox>(host, "Other");
                var probe = (DraftProbe)BindingOperations.GetBinding(editor, TextBox.TextProperty)!.Source;
                var closes = 0;
                host.CloseRequested += (_, _) => closes++;

                editor.Focus();
                Sta.PumpToIdle();
                editor.Text = "typed";
                probe.Phone.Should().Be("typed");

                var first = BubblingKey.Raise(editor, Key.Escape);
                first.Handled.Should().BeTrue("C2: armed Esc restores and is handled");
                editor.Text.Should().Be("keep");
                probe.Phone.Should().Be("keep", "C6/C7: SetCurrentValue must flow the restored value");
                BindingOperations.GetBinding(editor, TextBox.TextProperty).Should().NotBeNull("C6: binding stays attached");
                closes.Should().Be(0);

                var second = BubblingKey.Raise(editor, Key.Escape);
                second.Handled.Should().BeTrue("host consumes the disarmed press");
                closes.Should().Be(1, "C3: disarmed Esc is not handled by the field and reaches the host");
                host.IsOpen.Should().BeTrue("B5: host does not self-close");

                // N3: Tab out (C5 clears), refocus (new snapshot), edit, first Esc restores, second bubbles.
                closes = 0;
                editor.Focus();
                Sta.PumpToIdle();
                editor.Text = "  dirty  ";
                editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Sta.PumpToIdle();
                editor.Focus();
                Sta.PumpToIdle();
                editor.Text = "again";
                BubblingKey.Raise(editor, Key.Escape).Handled.Should().BeTrue();
                editor.Text.Should().Be("  dirty  ",
                    "N3: the new focus-entry snapshot is whatever the field held when it was refocused");
                BubblingKey.Raise(editor, Key.Escape);
                closes.Should().Be(1);

                // C4: after a restore, typing re-arms against the same snapshot.
                other.Focus();
                Sta.PumpToIdle();
                editor.Focus();
                Sta.PumpToIdle();
                var snapshot = editor.Text;
                editor.Text = "temp";
                BubblingKey.Raise(editor, Key.Escape);
                editor.Text.Should().Be(snapshot);
                editor.Text = "temp-again";
                BubblingKey.Raise(editor, Key.Escape);
                editor.Text.Should().Be(snapshot, "C4: re-arm uses the focus-entry snapshot, not the restored value");
            });

    private sealed class DraftProbe
    {
        public string Phone { get; set; } = string.Empty;
    }

    private static T FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var node in Walk(root))
        {
            if (node is T match && match.Name == name)
                return match;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} named {name}");
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
