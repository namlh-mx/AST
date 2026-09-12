using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AST.Controls;

// Overlay chrome for a sub-form over a declaration card. Owns Esc-to-request-close and Tab cycling
// inside the host. It never changes IsOpen in response to Esc: the consumer's CloseRequested handler
// decides, so an unsaved-draft confirm cannot be skipped. Traversal is
// contained with KeyboardNavigationMode.Cycle; LostFocus is not vetoed, because the confirm dialog
// lives at the window root.
//
// Chrome comes from the keyed Style `AstOverlayHost` in Controls.xaml: that style owns the default
// scrim brush (#80000000) and a Border template that paints Background. The host is useless without
// it. No DefaultStyleKeyProperty override and no Themes/Generic.xaml.
public class AstOverlayHost : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(AstOverlayHost),
        new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty IsDefaultFocusProperty = DependencyProperty.RegisterAttached(
        "IsDefaultFocus",
        typeof(bool),
        typeof(AstOverlayHost),
        new PropertyMetadata(false));

    public static bool GetIsDefaultFocus(DependencyObject element) =>
        (bool)element.GetValue(IsDefaultFocusProperty);

    public static void SetIsDefaultFocus(DependencyObject element, bool value) =>
        element.SetValue(IsDefaultFocusProperty, value);

    public event EventHandler? CloseRequested;

    private IInputElement? _opener;
    private IInputElement? _lastContained;

    public AstOverlayHost()
    {
        // handledEventsToo: false — an armed field marks the first Esc handled; seeing that press
        // here would collapse the ladder into a single self-close.
        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnBubblingKeyDown), handledEventsToo: false);
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnContainedGotFocus));
        ApplyClosedState();
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public void RestoreContainedFocus()
    {
        if (_lastContained is FrameworkElement { IsLoaded: true, Focusable: true } target)
            target.Focus();
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AstOverlayHost host)
            return;

        if ((bool)e.NewValue)
            host.ApplyOpenState();
        else
            host.ApplyClosedState(restoreOpener: true);
    }

    private void ApplyOpenState()
    {
        var scope = FocusScope();
        var focused = FocusManager.GetFocusedElement(scope);
        _opener = focused is DependencyObject focusedObj && IsDescendant(focusedObj) ? null : focused;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        Focusable = true;
        IsTabStop = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);

        Dispatcher.BeginInvoke(FocusDefaultDescendant, DispatcherPriority.Loaded);
    }

    private void ApplyClosedState(bool restoreOpener = false)
    {
        // Restore predicate: null-contained-or-detached-focus.
        // Restore when the recorded opener is still a loaded, focusable FrameworkElement AND
        // keyboard focus is absent, still inside this host, or no longer attached to a presentation source.
        // WHAT THIS PREDICATE DOES NOT DISTINGUISH — declared so the claim is not read wider than the mechanism:
        //   1. Why an attached outside element holds focus. A sidebar destination the operator just clicked
        //      and a programmatic Focus() on a sibling look the same; both are spared.
        //   2. Why focus is absent or detached. A ContentDialog removed from its host, a Focus() that
        //      failed, and a disconnected test stand-in all restore. The gate cannot tell a dialog teardown
        //      from any other detach.
        //   3. WPF-UI's DispatcherPriority.Input previous-focus restore, which is queued during dialog
        //      removal and has not necessarily run when this method runs.
        var shouldRestore = restoreOpener
            && _opener is FrameworkElement { IsLoaded: true, Focusable: true }
            && FocusIsAbsentContainedOrDetached();

        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        Focusable = false;
        IsTabStop = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Continue);

        if (shouldRestore)
            ((UIElement)_opener!).Focus();

        _lastContained = null;
    }

    private bool FocusIsAbsentContainedOrDetached()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is null)
            return true;
        if (focused is DependencyObject focusedObj && IsDescendant(focusedObj))
            return true;
        return focused is Visual visual && PresentationSource.FromVisual(visual) is null;
    }

    private void OnBubblingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsOpen)
            return;

        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnContainedGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is IInputElement source && !ReferenceEquals(source, this))
            _lastContained = source;
    }

    private void FocusDefaultDescendant()
    {
        if (!IsOpen)
            return;

        foreach (var element in Walk(this))
        {
            if (element is UIElement candidate
                && GetIsDefaultFocus(candidate)
                && IsEligibleFocusTarget(candidate))
            {
                candidate.Focus();
                return;
            }
        }

        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private static bool IsEligibleFocusTarget(UIElement element) =>
        element.IsEnabled
        && element.IsVisible
        && element.Focusable
        && KeyboardNavigation.GetIsTabStop(element);

    private DependencyObject FocusScope() =>
        Window.GetWindow(this) as DependencyObject
        ?? FocusManager.GetFocusScope(this)
        ?? this;

    private bool IsDescendant(DependencyObject? candidate)
    {
        for (var current = candidate; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, this))
                return true;
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
    {
        if (element is Visual)
            return VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);

        return LogicalTreeHelper.GetParent(element);
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var visualCount = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        if (visualCount > 0)
        {
            for (var i = 0; i < visualCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var nested in Walk(child))
                    yield return nested;
            }

            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Walk(child))
                yield return nested;
        }
    }
}
