using System.Windows;
using Wpf.Ui.Controls;

namespace AST.Controls;

// Project password field. Works around a WPF-UI 4.3 defect in the base PasswordBox: while the
// password is REVEALED, its UpdateTextContents routes every Password change through
// HandleRevealedModeUpdate, which only ever syncs Text -> Password. A programmatic write (a
// ViewModel clearing the field on Reuse/Clear/navigate-away) is therefore reverted from the still
// visible Text, and — because the binding is TwoWay — the stale secret is pushed BACK into the
// ViewModel. That makes a revealed password impossible to wipe, so the fix belongs here on the
// control rather than in each screen. Masked mode already syncs Password -> Text correctly.
public class AstPasswordBox : PasswordBox
{
    static AstPasswordBox()
    {
        // Inherit the base control's Fluent template instead of looking for a theme style keyed to
        // this subclass (which does not exist) — an untemplated WPF-UI control renders as a notdef box.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(AstPasswordBox),
            new FrameworkPropertyMetadata(typeof(PasswordBox)));
    }

    public AstPasswordBox()
    {
        // While revealed, the plaintext lives in the inherited TextBox's Text, and TextBox records even
        // programmatic Text writes as undo units — so Ctrl+Z would resurrect a wiped password and (through
        // the base's Text -> Password sync) push it back into the ViewModel, defeating every wipe below.
        // A password field has no legitimate undo. Set as a local value, not a Style setter: this is a
        // security invariant of the control and must not be overridable per screen.
        IsUndoEnabled = false;
    }

    protected override void OnPasswordChanged()
    {
        // Text out of sync here means Password was written from outside: the base's own typing cycle
        // assigns Password from Text before re-entering, so the two are already equal on that path.
        var password = Password ?? string.Empty;
        if (IsPasswordRevealed && Text != password)
        {
            SetCurrentValue(TextProperty, password);
            return;
        }

        base.OnPasswordChanged();
    }
}
