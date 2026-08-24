using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace AST.Controls;

/// <summary>
/// Screen header: a Fluent icon plus the screen title. When <see cref="BackCommand"/> is set the whole header
/// becomes the Back affordance (hand cursor; a left-click executes the command). The header never navigates
/// itself — the consuming view keeps navigation authority. <see cref="Icon"/> is optional (collapses when unset).
/// </summary>
public partial class AstScreenHeader : UserControl
{
    public AstScreenHeader()
    {
        InitializeComponent();
        UpdateIconVisibility();
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(AstScreenHeader),
        new PropertyMetadata(SymbolRegular.Empty, OnIconChanged));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AstScreenHeader), new PropertyMetadata(null));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty BackCommandProperty = DependencyProperty.Register(
        nameof(BackCommand), typeof(ICommand), typeof(AstScreenHeader),
        new PropertyMetadata(null, OnBackCommandChanged));

    public ICommand? BackCommand
    {
        get => (ICommand?)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AstScreenHeader)d).UpdateIconVisibility();

    private void UpdateIconVisibility() =>
        IconElement.Visibility = Icon == SymbolRegular.Empty ? Visibility.Collapsed : Visibility.Visible;

    private static void OnBackCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AstScreenHeader)d).HeaderPanel.Cursor = e.NewValue is null ? Cursors.Arrow : Cursors.Hand;

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (BackCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
