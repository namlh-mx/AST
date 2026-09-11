using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AST.Core;

namespace AST.Behaviors;

// One-shot focus-edit session for a TextBoxBase: Esc while armed restores the focus-entry snapshot
// and disarms; Esc while disarmed is not handled so it can bubble to AstOverlayHost. Captures and
// restores only — canonicalisation belongs to the draft model (N1). SessionEnding is raised on
// LostFocus before the session is cleared so a consumer can write the canonical value, let the
// notification chain return, and then this type ends the session in the same handler.
[SharedComponent]
public static class AstFieldRevert
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(AstFieldRevert),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly RoutedEvent SessionEndingEvent = EventManager.RegisterRoutedEvent(
        "SessionEnding",
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(AstFieldRevert));

    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session",
        typeof(Session),
        typeof(AstFieldRevert),
        new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static void AddSessionEndingHandler(DependencyObject element, RoutedEventHandler handler)
        => (element as UIElement)?.AddHandler(SessionEndingEvent, handler);

    public static void RemoveSessionEndingHandler(DependencyObject element, RoutedEventHandler handler)
        => (element as UIElement)?.RemoveHandler(SessionEndingEvent, handler);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBoxBase field)
            return;

        if ((bool)e.NewValue)
        {
            field.GotFocus += OnGotFocus;
            field.LostFocus += OnLostFocus;
            field.KeyDown += OnKeyDown;
            field.TextChanged += OnTextChanged;
        }
        else
        {
            field.GotFocus -= OnGotFocus;
            field.LostFocus -= OnLostFocus;
            field.KeyDown -= OnKeyDown;
            field.TextChanged -= OnTextChanged;
            field.ClearValue(SessionProperty);
        }
    }

    private static void OnGotFocus(object sender, RoutedEventArgs e)
    {
        var field = (TextBoxBase)sender;
        if (!IsThisField(field, e.OriginalSource))
            return;
        if (field.GetValue(SessionProperty) is Session)
            return;

        field.SetValue(SessionProperty, new Session(CurrentText(field)));
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        var field = (TextBoxBase)sender;
        if (!ReferenceEquals(e.OriginalSource, field) && field.IsKeyboardFocusWithin)
            return;

        field.RaiseEvent(new RoutedEventArgs(SessionEndingEvent, field));
        field.ClearValue(SessionProperty);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBoxBase field)
            return;
        if (field.GetValue(SessionProperty) is not Session { Armed: true } session)
            return;

        session.Restoring = true;
        SetText(field, session.Snapshot);
        session.Restoring = false;
        session.Armed = false;
        e.Handled = true;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBoxBase field)
            return;
        if (field.GetValue(SessionProperty) is not Session session || session.Restoring)
            return;

        session.Armed = true;
    }

    private static bool IsThisField(TextBoxBase field, object? originalSource)
    {
        if (ReferenceEquals(originalSource, field))
            return true;

        for (var current = originalSource as DependencyObject;
             current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                       ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, field))
                return true;
        }

        return false;
    }

    private static string CurrentText(TextBoxBase field) => field is TextBox textBox ? textBox.Text : string.Empty;

    private static void SetText(TextBoxBase field, string value)
    {
        if (field is TextBox textBox)
            textBox.SetCurrentValue(TextBox.TextProperty, value);
    }

    private sealed class Session(string snapshot)
    {
        public string Snapshot { get; } = snapshot;
        public bool Armed { get; set; } = true;
        public bool Restoring { get; set; }
    }
}
