using System.Windows;
using System.Windows.Controls.Primitives;
using AST.Controls;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Controls;

// Smoke test for the OffscreenHost harness: unlike AstDateBoxTests (which hand-builds a stand-in template via
// FrameworkElementFactory on purpose), this resolves the REAL keyed style out of
// AST.UI/Resources/DesignSystem/Controls.xaml through the same 7-entry flat merge AST/App.xaml uses, in a real
// shown window. Its job is to prove that path works end to end -- so every assertion below is chosen to be
// unsatisfiable unless the merge is complete and correctly ordered:
//   * FindResource("AstDateBox")            -> needs Controls.xaml present
//   * the real template's PART_TextBox      -> needs the real ControlTemplate, not the test stand-in
//   * that ui:TextBox having a resolved Template with TargetType ui:TextBox
//                                           -> needs ui:ControlsDictionary present (Wpf.Ui ships no
//                                              Themes/Generic.xaml fallback for its controls)
// Verified red/green 2026-08-07 by temporarily removing entries from OffscreenHost.BuildApplicationResources
// (removing Controls.xaml or ui:ControlsDictionary each turns this red). It does NOT police merge ORDER -- see
// the note on OffscreenHost.BuildApplicationResources.
public class AstDateBoxRealStyleTests
{
    [Fact]
    public void Real_keyed_style_from_Controls_xaml_builds_the_real_WpfUi_template()
        => OffscreenHost.Run(
            window => new AstDateBox
            {
                Style = (Style)window.FindResource("AstDateBox"),
                ShowCalendarGlyph = true,
            },
            (_, box) =>
            {
                Assert.NotNull(box.Template);

                var textBox = box.Template.FindName("PART_TextBox", box) as UiTextBox;
                Assert.NotNull(textBox);
                // Values that exist only in the real Controls.xaml template (the hand-built stand-in sets neither).
                Assert.Equal(94d, textBox!.Width);
                Assert.Equal("00/00/0000", textBox.PlaceholderText);

                // The Fluent chrome itself: an implicit {x:Type ui:TextBox} style from ui:ControlsDictionary.
                Assert.NotNull(textBox.Template);
                Assert.Equal(typeof(UiTextBox), textBox.Template.TargetType);

                Assert.NotNull(box.Template.FindName("PART_GlyphToggle", box) as ToggleButton);
            });
}
