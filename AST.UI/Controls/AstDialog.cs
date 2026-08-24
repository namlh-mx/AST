using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AST.Core.Presentation;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AST.Controls;

// Single home of the project's Fluent dialog shape. Build dialogs through here — never construct a
// ContentDialog ad hoc in a screen, or the shape drifts per screen.
//
// Shape: title row = kind icon + "AST" in bold at BODY size; body = one short sentence; fixed width so
// every dialog is the same size; footer buttons = icon + short label, equal width, right-aligned.
public static class AstDialog
{
    // WPF-UI only sets DialogMaxWidth and otherwise sizes a dialog to its content, so short messages each
    // produced a differently sized box. Pinning the width standardises it.
    private const double StandardWidth = 600;

    // The stock template pushes FontSize 20 onto the title ContentPresenter. The requester wants the title
    // at body size with bold as the only difference, so both the title and the body pin their size here.
    private const double BodyFontSize = 14;

    private const double ButtonMinWidth = 96;

    private const string SharedButtonSizeGroup = "AstDialogButtons";

    // A decision the operator must make. Returns true when they chose the primary action.
    public static async Task<bool> ConfirmAsync(
        IContentDialogService dialogs,
        string message,
        StatusSeverity severity,
        string primaryText,
        SymbolRegular primaryIcon,
        string closeText,
        SymbolRegular closeIcon)
    {
        var dialog = NewDialog(severity);

        var primary = NewButton(primaryText, primaryIcon, ControlAppearance.Primary);
        var close = NewButton(closeText, closeIcon, ControlAppearance.Secondary);
        primary.Click += (_, _) => dialog.Hide(ContentDialogResult.Primary);
        close.Click += (_, _) => dialog.Hide(ContentDialogResult.None);
        // The cancelling choice answers both Enter and Esc, so neither key can commit the risky action.
        // ContentDialog overrides no key handling and DefaultButton only picks among the stock footer
        // buttons (switched off below), so this has to be stated on our own button. Safe to scope to the
        // whole window: the dialog host disables every sibling while the dialog is up, and the button is
        // gone once it closes.
        close.IsDefault = true;
        close.IsCancel = true;

        dialog.Content = NewBody(message, primary, close);

        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    // An acknowledge-only notice: one button, nothing to decide.
    public static async Task NoticeAsync(IContentDialogService dialogs, string message, StatusSeverity severity)
    {
        var dialog = NewDialog(severity);

        var close = NewButton("Đóng", SymbolRegular.Dismiss24, ControlAppearance.Secondary);
        close.Click += (_, _) => dialog.Hide(ContentDialogResult.None);

        dialog.Content = NewBody(message, close);

        await dialogs.ShowAsync(dialog, CancellationToken.None);
    }

    private static ContentDialog NewDialog(StatusSeverity severity)
    {
        var dialog = new ContentDialog
        {
            Title = NewTitle(severity),
            DialogWidth = StandardWidth,
            // The stock footer stretches each button across an equal star column, which reads as oversized.
            // Switching it off is the library's own extension point for laying the buttons out ourselves —
            // cheaper and safer than copying the whole ContentDialog ControlTemplate to resize them.
            IsFooterVisible = false,
        };

        // ...but switching the footer off costs the dialog its rounded BOTTOM corners, so pay that back here.
        // The template is a 2-row Grid: row 0 (*) holds a Border that tints the body and is rounded on TOP
        // only (8,8,0,0); row 1 (Auto) is the footer, rounded on the bottom. With the footer collapsed row 1
        // is 0 high, so that body overlay stretches the full height and paints its SQUARE bottom over the
        // outer border's rounded corners. The outer border already paints the opaque ContentDialogBackground,
        // so the overlay is only a tint — dropping it restores all four corners. Its brush is a
        // DynamicResource, which is why this works from here instead of needing a ControlTemplate copy.
        dialog.Resources["ContentDialogTopOverlay"] = Brushes.Transparent;

        return dialog;
    }

    private static UIElement NewTitle(StatusSeverity severity) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new SymbolIcon
                {
                    Symbol = SymbolFor(severity),
                    FontSize = 18,
                    Foreground = BrushFor(severity),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                },
                new TextBlock
                {
                    Text = "AST",
                    FontWeight = FontWeights.Bold,
                    FontSize = BodyFontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

    private static UIElement NewBody(string message, params Button[] buttons)
    {
        // Equal, content-based button widths = the project's action-group convention (WPF-CONVENTIONS):
        // one Auto column per button, all sharing a size group, so every button takes the widest label.
        var actions = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetIsSharedSizeScope(actions, true);

        for (var i = 0; i < buttons.Length; i++)
        {
            // The gap gets its own column. Putting it in the button's Margin instead would fold it into the
            // shared-size measurement (an Auto column measures DesiredSize, which includes margin), so the
            // group would settle on widest+gap and only the gapped button would actually render that wide.
            if (i > 0)
            {
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            }

            actions.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                SharedSizeGroup = SharedButtonSizeGroup,
            });

            var button = buttons[i];
            Grid.SetColumn(button, actions.ColumnDefinitions.Count - 1);
            actions.Children.Add(button);
        }

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = BodyFontSize,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(actions);
        return body;
    }

    private static Button NewButton(string text, SymbolRegular icon, ControlAppearance appearance) =>
        new()
        {
            Content = text,
            Icon = new SymbolIcon { Symbol = icon },
            Appearance = appearance,
            MinWidth = ButtonMinWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static SymbolRegular SymbolFor(StatusSeverity severity) => severity switch
    {
        StatusSeverity.Success => SymbolRegular.CheckmarkCircle24,
        StatusSeverity.Error => SymbolRegular.ErrorCircle24,
        StatusSeverity.Warning => SymbolRegular.Warning24,
        _ => SymbolRegular.Info24,
    };

    // The in-screen status band keeps its locked convention (success = green, any other message = red).
    // A dialog is a different surface: it shows exactly one message on its own, so each kind gets its own
    // colour to read at a glance (requester 2026-07-15).
    private static Brush BrushFor(StatusSeverity severity) =>
        Application.Current?.TryFindResource(severity switch
        {
            StatusSeverity.Success => "AstSuccessBrush",
            StatusSeverity.Error => "AstErrorBrush",
            StatusSeverity.Warning => "AstWarningBrush",
            _ => "AstPrimaryBrush",
        }) as Brush ?? SystemColors.ControlTextBrush;
}
