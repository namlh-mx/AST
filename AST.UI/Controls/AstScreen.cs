using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AST.Core.Presentation;
using Wpf.Ui.Controls;

namespace AST.Controls;

// Standard screen-frame container: composes the three top blocks every screen shares — the screen header
// (AstScreenHeader), the status band (AstStatusBand), and the body slot (this ContentControl's Content) — so
// a view only supplies its body. Lookless templated ContentControl (NOT a UserControl) so the body is a real
// Content passthrough, the SAME pattern as AstField. Every DP is a loss-less passthrough to a composed child
// (types mirror AstScreenHeader/AstStatusBand exactly); the keyed style binds them via TemplateBinding, so no
// PropertyChanged callbacks are needed here. AstScreen introduces NO navigation authority — BackCommand is a
// dumb passthrough to AstScreenHeader, which never navigates itself.
//
// No DefaultStyleKeyProperty override and no Themes/Generic.xaml: the default look is the keyed
// Style x:Key="AstScreen" in AST.UI/Resources/DesignSystem/Controls.xaml, matching the AstField /
// AstPasswordBox keyed-style convention. Consumers opt in explicitly: Style="{StaticResource AstScreen}".
public class AstScreen : ContentControl
{
    // → AstScreenHeader.Title.
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AstScreen), new PropertyMetadata(null));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    // → AstScreenHeader.Icon; collapses in the header when SymbolRegular.Empty.
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(AstScreen), new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    // → AstScreenHeader.BackCommand; a dumb passthrough (no navigation logic lives here).
    public static readonly DependencyProperty BackCommandProperty = DependencyProperty.Register(
        nameof(BackCommand), typeof(ICommand), typeof(AstScreen), new PropertyMetadata(null));

    public ICommand? BackCommand
    {
        get => (ICommand?)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    // → AstStatusBand.Message; an empty message hides the band (its reserved height keeps the layout stable).
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AstScreen), new PropertyMetadata(null));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    // → AstStatusBand.Severity.
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(StatusSeverity), typeof(AstScreen),
        new PropertyMetadata(StatusSeverity.None));

    public StatusSeverity Severity
    {
        get => (StatusSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }
}
