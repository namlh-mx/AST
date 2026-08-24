using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace AST.Controls;

/// <summary>
/// Section header: a Fluent icon plus a section title — the standard "icon + section name" block. Set
/// <see cref="Icon"/> and <see cref="Text"/>; replaces the per-screen copy-pasted icon+TextBlock composite.
/// </summary>
public partial class AstSectionHeader : UserControl
{
    public AstSectionHeader() => InitializeComponent();

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(AstSectionHeader), new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AstSectionHeader), new PropertyMetadata(null));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
