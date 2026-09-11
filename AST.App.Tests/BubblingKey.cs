using System.Windows;
using System.Windows.Input;

namespace AST.App.Tests;

// Bubbling KeyDown for overlay/field-revert tests. Must not use Keyboard.PreviewKeyDownEvent
// (AstDateBoxTests.RaiseKeyDown): that tunnel cannot express B6 or C2.
internal static class BubblingKey
{
    public static KeyEventArgs Raise(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException(
                "Bubbling KeyDown requires a PresentationSource; host the visual with OffscreenHost.Run.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, timestamp: 0, key: key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        target.RaiseEvent(args);
        return args;
    }
}
