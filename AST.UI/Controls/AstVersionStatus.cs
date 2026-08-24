using System.Windows;
using System.Windows.Controls;
using AST.Core.Presentation;

namespace AST.Controls;

// Shared version-status label (Bị hủy / Hết hiệu lực / Hiệu lực / Chờ hiệu lực); hidden when Status == None.
// No dates in the label — the effective-period block shows the dates. Lookless Control (no code-behind); the
// default look is the keyed Style x:Key="AstVersionStatus" in Controls.xaml (no Themes/Generic.xaml), same
// convention as AstField. The VM computes Status from the version row + business today; this only displays it.
public class AstVersionStatus : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(VersionStatus), typeof(AstVersionStatus),
        new PropertyMetadata(VersionStatus.None));

    public VersionStatus Status
    {
        get => (VersionStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
