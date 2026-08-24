using System.Windows;
using System.Windows.Controls;
using AST.Core.Presentation;

namespace AST.Controls;

/// <summary>
/// Single home of the screen status band. Bind <see cref="Message"/> and <see cref="Severity"/>;
/// an empty message hides the band while its reserved height keeps the layout from reflowing.
/// </summary>
public partial class AstStatusBand : UserControl
{
    public AstStatusBand() => InitializeComponent();

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AstStatusBand), new PropertyMetadata(null));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(StatusSeverity), typeof(AstStatusBand),
        new PropertyMetadata(StatusSeverity.None));

    public StatusSeverity Severity
    {
        get => (StatusSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }
}
