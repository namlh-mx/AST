using System.Windows;
using System.Windows.Controls;

namespace AST.Controls;

// Form-field container: a Label above an input slot (the ContentControl's Content), with an optional
// Helper hint and an Error line beneath. Lookless templated ContentControl (NOT a UserControl) so the
// input slot is a real Content passthrough — it can host any input (ui:TextBox, AstPasswordBox, ComboBox,
// ui:NumberBox) without hard-coding a type. Validation is external (the ViewModel via INotifyDataErrorInfo);
// AstField performs NO validation, it only DISPLAYS Error/HasError.
//
// No DefaultStyleKeyProperty override and no Themes/Generic.xaml: the default look is the keyed
// Style x:Key="AstField" in AST.UI/Resources/DesignSystem/Controls.xaml, matching the AstPasswordBox /
// AstCard / AstDataGrid keyed-style convention. Consumers opt in explicitly: Style="{StaticResource AstField}".
public class AstField : ContentControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(AstField),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HelperProperty =
        DependencyProperty.Register(
            nameof(Helper),
            typeof(string),
            typeof(AstField),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ErrorProperty =
        DependencyProperty.Register(
            nameof(Error),
            typeof(string),
            typeof(AstField),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.Register(
            nameof(HasError),
            typeof(bool),
            typeof(AstField),
            new PropertyMetadata(false));

    // The field label shown above the input.
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    // Optional hint under the input; hidden when null/empty and suppressed while HasError is true.
    public string Helper
    {
        get => (string)GetValue(HelperProperty);
        set => SetValue(HelperProperty, value);
    }

    // Error message text; shown only while HasError is true.
    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    // Drives error styling/visibility. Set by the consumer from the ViewModel's validation state.
    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }
}
