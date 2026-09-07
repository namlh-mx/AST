using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AST.Core.Presentation;
using Calendar = System.Windows.Controls.Calendar;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace AST.Controls;

// Shared single-date atom: a masked dd/MM/yyyy text box, plus an OPT-IN Fluent popup calendar glyph
// (ShowCalendarGlyph). The masked text box is the SINGLE SOURCE OF TRUTH for Date -- the calendar only
// WRITES into Date on a genuine user pick; opening it never preselects Calendar.SelectedDate (Date set or
// empty alike -- only DisplayDate navigates to the right month, see OnGlyphChecked). This is deliberate:
// WPF's Calendar does not raise SelectedDatesChanged for a click that reselects the date already
// SelectedDate, so preselecting the current Date made re-clicking it a silent no-op (2026-07-29 fix).
// Leaving SelectedDate null makes EVERY pick a null->value change Calendar always raises for -- one path,
// no click/mouse interception layers needed. The current Date is NOT visually highlighted in the popup --
// a CalendarDayButtonStyle override for this was tried and reverted 2026-07-29 (Controls.xaml, dropped
// WPF-UI's Fluent per-cell look); do not reattempt without first inspecting WPF-UI's compiled Calendar
// template. NO hover hint, NO double-click-today (dropped 2026-07-23 -- quick
// "today" entry goes through the calendar glyph instead). Today (DateOnly) is a DP the consumer binds from
// IBusinessDateProvider.Today -- this control never reads DateTime.Now. No validation (no range check) -- the
// ViewModel owns all rules. Templated Control (parts model) because the mask + calendar need OnApplyTemplate;
// no Themes/Generic.xaml -- default look = the keyed Style x:Key="AstDateBox" in Controls.xaml, same
// convention as AstField / AstEffectivePeriod.
// PART_Popup open-state invariant (2026-07-31 fix, 3rd occurrence of this failure class on this control):
// PART_Popup.IsOpen has exactly ONE authority -- PART_GlyphToggle.IsChecked, via the TwoWay ElementName
// binding in Controls.xaml. No code may ever write Popup.IsOpen directly or hold a second imperative
// open/close path (a prior Popup.Closed handler did this and caused a reopen flicker). A re-template that
// swaps in a Popup without that exact binding silently reintroduces the bug.
[TemplatePart(Name = PartTextBox, Type = typeof(TextBox))]
[TemplatePart(Name = PartGlyphToggle, Type = typeof(ToggleButton))]
[TemplatePart(Name = PartPopup, Type = typeof(Popup))]
[TemplatePart(Name = PartCalendar, Type = typeof(Calendar))]
public class AstDateBox : Control
{
    private const string PartTextBox = "PART_TextBox";
    private const string PartGlyphToggle = "PART_GlyphToggle";
    private const string PartPopup = "PART_Popup";
    private const string PartCalendar = "PART_Calendar";
    private const string DateFormat = "dd/MM/yyyy";

    // Digit count of DateFormat's day+month+year slots (2+2+4). Tied to DateFormat so a future format
    // change is a visible two-constant edit, not a silently-stale bare "8" in the paste gate below.
    private const int PasteDigitCount = 8;

    // The engine's FormatDisplay() for a fully-unfilled state -- day/month/year 0 are all engine-rejected
    // (see DdMmYyyySegmentEditor), so this exact string can only mean "nothing is filled," never a real
    // calendar date. Used to render it as "" instead (Finding 1, 2026-08-07) and to accept it interchangeably
    // with an already-empty string on commit (CommitTextBoxValue).
    private const string AllUnfilledDisplay = "00/00/0000";

    private TextBox? _textBox;
    private ToggleButton? _glyphToggle;
    private Calendar? _calendar;
    private bool _syncingText;
    private bool _syncingCalendar;

    // Captured at TextBox GotFocus — Esc restores this (typing often never writes Date until commit;
    // a valid paste does commit mid-focus, so reading Date at Esc-time would wrongly no-op).
    private DateOnly? _dateOnFocus;

    // P2 (2026-08-07): drives the mask -- one engine instance per AstDateBox. Replaces the old free-caret
    // digit-buffer mask (R3); see the "dd/MM/yyyy segment mask (P2)" region below.
    private readonly DdMmYyyySegmentEditor _editor = new();

    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateOnly?), typeof(AstDateBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDateChanged));

    // The entered date; null = empty. The masked text box is the single source of truth.
    public DateOnly? Date
    {
        get => (DateOnly?)GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    public static readonly DependencyProperty ShowCalendarGlyphProperty = DependencyProperty.Register(
        nameof(ShowCalendarGlyph), typeof(bool), typeof(AstDateBox), new PropertyMetadata(false));

    // Default false = a pure masked text field. Screen A sets true on every date field it has (EP From/To
    // AND the org-unit tree "Ngày cụ thể" as-of box, 2026-07-23).
    public bool ShowCalendarGlyph
    {
        get => (bool)GetValue(ShowCalendarGlyphProperty);
        set => SetValue(ShowCalendarGlyphProperty, value);
    }

    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateOnly), typeof(AstDateBox), new PropertyMetadata(default(DateOnly)));

    // Business "today" (IBusinessDateProvider.Today) -- the consumer binds this; the control never reads
    // DateTime.Now. Used only to navigate the calendar to Today's month when Date is empty on open
    // (not preselected -- see OnGlyphChecked).
    public DateOnly Today
    {
        get => (DateOnly)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_textBox is not null)
        {
            _textBox.LostFocus -= OnTextBoxLostFocus;
            _textBox.GotFocus -= OnTextBoxGotFocus;
            _textBox.RemoveHandler(UIElement.MouseLeftButtonUpEvent, (MouseButtonEventHandler)OnTextBoxMouseLeftButtonUp);
            _textBox.PreviewTextInput -= OnTextBoxPreviewTextInput;
            _textBox.PreviewKeyDown -= OnTextBoxPreviewKeyDown;
            DataObject.RemovePastingHandler(_textBox, OnTextBoxPaste);
        }
        if (_glyphToggle is not null)
        {
            _glyphToggle.Checked -= OnGlyphChecked;
            _glyphToggle.Unchecked -= OnGlyphUnchecked;
            _glyphToggle.SetCurrentValue(UIElement.IsHitTestVisibleProperty, true);
        }
        if (_calendar is not null) _calendar.SelectedDatesChanged -= OnCalendarSelectedDatesChanged;

        _textBox = GetTemplateChild(PartTextBox) as TextBox;
        _glyphToggle = GetTemplateChild(PartGlyphToggle) as ToggleButton;
        _calendar = GetTemplateChild(PartCalendar) as Calendar;

        if (_textBox is not null)
        {
            // A masked field has no legitimate undo -- Ctrl+Z could otherwise resurrect a stale digit buffer
            // that has already desynced from _editor. Same precedent as AstPasswordBox/IsUndoEnabled=false
            // (there set on the control itself; here on the template-part TextBox it wraps).
            _textBox.IsUndoEnabled = false;
            _textBox.LostFocus += OnTextBoxLostFocus;
            _textBox.GotFocus += OnTextBoxGotFocus;
            // handledEventsToo: true is load-bearing, not defensive. TextBoxBase's own TextEditor class
            // handler marks the mouse-up HANDLED before the event reaches instance handlers, so a plain
            // `_textBox.MouseLeftButtonUp += ...` subscription never fires in a live window -- clicking a
            // segment then silently left _editor.ActivePart pointing at whatever part it held before, while
            // the TextBox showed the user's own selection somewhere else (2026-08-07 F5: typing over a
            // highlighted Year wrote into Day). Headless tests never caught it because they call
            // SelectSegmentAt directly instead of raising a real mouse event.
            _textBox.AddHandler(
                UIElement.MouseLeftButtonUpEvent,
                (MouseButtonEventHandler)OnTextBoxMouseLeftButtonUp,
                handledEventsToo: true);
            _textBox.PreviewTextInput += OnTextBoxPreviewTextInput;
            _textBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
            DataObject.AddPastingHandler(_textBox, OnTextBoxPaste);
        }
        if (_glyphToggle is not null)
        {
            _glyphToggle.Checked += OnGlyphChecked;
            _glyphToggle.Unchecked += OnGlyphUnchecked;
        }
        if (_calendar is not null) _calendar.SelectedDatesChanged += OnCalendarSelectedDatesChanged;

        SyncTextFromDate();
    }

    private static void OnDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AstDateBox)d).SyncTextFromDate();

    private void SyncTextFromDate()
    {
        if (_syncingText || _textBox is null) return;
        _syncingText = true;
        try
        {
            // Keep the engine in sync with every Date change (external sets + the commit path below) so
            // it is ready for the next keystroke. Date == null must render "" (empty), NOT the engine's
            // all-zero FormatDisplay() -- preserves the existing empty-field UX.
            _editor.SetDate(Date);
            _textBox.Text = Date is null ? string.Empty : _editor.FormatDisplay();
        }
        finally { _syncingText = false; }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitTextBoxValue();
    }

    // Parse-or-clear the typed text into Date, then SyncTextFromDate (revert garbage / format valid).
    // Shared by LostFocus and Enter so both commit paths stay identical.
    private void CommitTextBoxValue()
    {
        if (_syncingText || _textBox is null) return;

        // Empty text is a deliberate clear (-> Date = null); a genuine dd/MM/yyyy parse sets Date; anything
        // else is unparseable garbage that must NOT overwrite an already-valid Date (TryParse returning null
        // for garbage is not the same as the user clearing the field) -- leave Date untouched and just revert
        // the text via SyncTextFromDate below.
        // "00/00/0000" (the engine's all-unfilled FormatDisplay) is treated the same as empty -- it is never a
        // valid calendar date (day/month/year 0 are all engine-rejected, see RenderDisplay), so this mapping
        // is unambiguous. Without it, clearing every digit via Backspace/Delete had no keyboard path back to
        // Date = null: TryParse("00/00/0000") fails, so Date stayed untouched and the display silently reverted.
        var text = _textBox.Text;
        if (string.IsNullOrEmpty(text) || text == AllUnfilledDisplay)
        {
            Date = null;
        }
        else if (TryParse(text) is { } parsed)
        {
            Date = parsed;
        }

        SyncTextFromDate(); // revert to the formatted value (or clear) on an invalid/partial entry
    }

    private void OnGlyphChecked(object sender, RoutedEventArgs e)
    {
        if (_calendar is null) return;

        _syncingCalendar = true;
        try
        {
            // Never preselect SelectedDate (Date set or empty alike) -- only navigate to the right month.
            // Preselecting made the first click on that same date a same-value reselect, which Calendar's
            // SelectedDatesChanged never fires for (see class remarks). The current Date is NOT visually
            // highlighted in the popup (see class remarks -- the CalendarDayButtonStyle attempt was reverted).
            _calendar.DisplayDate = (Date ?? Today).ToDateTime(TimeOnly.MinValue);
            _calendar.SelectedDate = null;
        }
        finally { _syncingCalendar = false; }

        // While open, ignore hits on the glyph so StaysOpen's outside-close is not also a ToggleButton
        // Click. Restore must wait until ApplicationIdle (OnGlyphUnchecked) -- a synchronous restore in
        // Unchecked is too early: Popup closes on PreviewMouseLeftButtonUp (MS docs), TwoWay unchecks,
        // and restoring hit-test then lets the same Up Click the glyph and reopen. SetCurrentValue (not a
        // plain setter) so this timing guard never wins a permanent local-value fight against a future
        // style/trigger on the same property.
        if (_glyphToggle is not null) _glyphToggle.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
    }

    private void OnGlyphUnchecked(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (_glyphToggle is not null && _glyphToggle.IsChecked != true)
                    _glyphToggle.SetCurrentValue(UIElement.IsHitTestVisibleProperty, true);
            },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void OnCalendarSelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingCalendar || _calendar?.SelectedDate is not { } picked) return;

        Date = DateOnly.FromDateTime(picked);
        // Flip the toggle only -- open-state has exactly one authority, PART_GlyphToggle.IsChecked (see the
        // class-header invariant); the TwoWay-bound Popup.IsOpen follows automatically. Never write
        // Popup.IsOpen directly.
        if (_glyphToggle is not null) _glyphToggle.IsChecked = false;
    }

    private static DateOnly? TryParse(string? text)
        => DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    // ---- dd/MM/yyyy segment mask (P2) ----
    // Drives _editor (DdMmYyyySegmentEditor, AST.Core P1) instead of hand-rolling digit-buffer parsing --
    // the engine is the single source of truth for what is a legal partial/complete date; this region only
    // adapts WPF keystroke/click/paste events to the engine's ApplyDigit/ApplyBackspace/ApplyDelete/
    // SelectPart surface and reflects the result (FormatDisplay + ActivePart) back onto PART_TextBox.
    // Click / focus-enter selects the whole segment landed in (SelectPart + TextBox.Select) so the next
    // digit overwrites it; Left/Right and typing '/' move the active segment the same way.
    // Every edit and navigation branch first calls SyncEditorPartFromSelection, which re-anchors the engine on
    // whatever is VISIBLY selected -- that single method is the only owner of "which segment am I editing."
    // Consequence for Delete/Backspace: they clear the whole active segment at any fill level (via the
    // engine's replace-part fast path in ApplyDelete) whenever that reconciliation resolves to a whole
    // segment -- a real whole-segment selection, one just landed on via typing's own auto-advance, OR a plain
    // caret that had drifted into a part other than the engine's last-known ActivePart. Only a plain caret
    // already inside the current part falls through to the engine's normal per-slot Backspace/Delete.

    private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (_textBox is null) return;
        // Pre-edit anchor for Esc (requester 2026-08-07) — must snapshot here, not at Esc time.
        _dateOnFocus = Date;
        SelectSegmentAt(_textBox.CaretIndex);
    }

    private void OnTextBoxMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Bubbling (not Preview): PreviewMouseLeftButtonUp fires BEFORE the TextBox's own TextEditor finishes
        // its mouse-up caret/selection handling, so a Select() call there risked being clobbered right after
        // by the control's own default behaviour (WPF's classic tunneling-vs-bubbling trap). Bubbling runs
        // after that default handling, so CaretIndex already reflects the click when we read it here.
        // Consequence of the handledEventsToo subscription (see OnApplyTemplate), deliberate: this runs on
        // EVERY left mouse-up, including the one ending a drag-select and a double-click's word-select -- so
        // a mouse gesture in this field always snaps to exactly one whole segment and can never leave a
        // partial or cross-segment selection. That is the intended masked-field rule, not a side effect.
        if (_textBox is null) return;
        SelectSegmentAt(_textBox.CaretIndex);
    }

    private void OnTextBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_textBox is null || e.Text.Length != 1)
        {
            e.Handled = true;
            return;
        }

        var ch = e.Text[0];
        if (ch == '/')
        {
            // Advances only, never backward; no-op past Year (NextPart returns null there). Re-anchored first
            // so the move is relative to the segment the user can SEE selected, not a stale engine part.
            SyncEditorPartFromSelection();
            if (NextPart(_editor.ActivePart) is { } next) SelectSegment(next);
            e.Handled = true;
            return;
        }

        if (!char.IsDigit(ch))
        {
            e.Handled = true;
            return;
        }

        SyncEditorPartFromSelection();
        ApplyEditorDigit(ch);
        e.Handled = true;
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textBox is null) return;

        // Enter commits typed text the same way LostFocus does (without requiring Tab-away / click-away),
        // then clears keyboard focus so the caret stops blinking — Enter must feel executed.
        if (e.Key == Key.Enter)
        {
            CommitTextBoxValue();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Esc restores the Date captured at focus-enter (requester 2026-08-07) — undoes typing and any
        // in-session paste commit — then clears focus/selection so editing ends (F5 round 1: same "done"
        // feel as Enter; user re-focuses to edit again).
        if (e.Key == Key.Escape)
        {
            Date = _dateOnFocus;
            // Unconditional: typing never writes Date until commit, so Date is often already equal to
            // _dateOnFocus and the DP raises no change notification for OnDateChanged → SyncTextFromDate.
            SyncTextFromDate();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Space must do nothing (requester 2026-08-07): WPF does not raise PreviewTextInput for Space on a
        // TextBox, so without this branch the TextBox default would insert a literal space into the selection.
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            // Same reason as the '/' branch: move relative to what is visibly selected, not a stale part.
            SyncEditorPartFromSelection();
            var next = e.Key == Key.Right ? NextPart(_editor.ActivePart) : PreviousPart(_editor.ActivePart);
            if (next is { } part) SelectSegment(part);
            e.Handled = true;
            return;
        }

        // SyncEditorPartFromSelection re-anchors the engine on what is actually highlighted first, so a
        // whole-segment selection (however it was produced) marks replace-part and ApplyDelete then takes its
        // whole-part-clear fast path -- requester decision 2026-08-07: Backspace over a full segment selection
        // must behave identically to Delete on that selection, not a single-slot removal.
        // A plain caret falls through to Backspace's normal ApplyBackspace / Delete's normal single-slot
        // ApplyDelete ONLY while it sits inside the part the engine is already on (the normal mid-typing
        // state). A caret in a DIFFERENT part is re-anchored by the Sync call above into a whole-segment
        // selection, so it whole-clears -- consistent with "a selected segment always clears in full", and
        // reachable via keys this control deliberately does not intercept (Home/End/Ctrl+A/Ctrl+arrows).
        if (e.Key == Key.Back)
        {
            SyncEditorPartFromSelection();
            if (IsWholeSegmentSelected(out _))
            {
                ApplyEditorDelete();
            }
            else
            {
                ApplyEditorBackspace();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            SyncEditorPartFromSelection();
            ApplyEditorDelete();
            e.Handled = true;
        }
    }

    private void OnTextBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (_textBox is null)
        {
            e.CancelCommand();
            return;
        }

        var pasted = e.SourceDataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        var digits = ExtractDigits(pasted);

        // Requester F5 2026-08-07: only a complete valid date may paste (PasteDigitCount digits that
        // form a calendar date, from "dd/MM/yyyy" or a raw digit string). Anything else — 0 digits,
        // wrong length, or calendar-illegal — cancels with the field left untouched (no partial mask
        // left behind).
        if (digits.Length != PasteDigitCount)
        {
            e.CancelCommand();
            return;
        }

        // Validate on a scratch engine instance, never the live _editor -- a rejected paste then has
        // nothing to roll back (Date/_editor/text were never touched), instead of wiping _editor via
        // SetDate(null) and rebuilding it from Date afterward on failure.
        var probe = new DdMmYyyySegmentEditor();
        var allAccepted = true;
        foreach (var digit in digits)
        {
            if (!probe.ApplyDigit(digit))
            {
                allAccepted = false;
                break;
            }
        }

        if (allAccepted && probe.TryGetDate(out var pastedDate))
        {
            // A full valid paste is a deliberate whole-value entry -- commit immediately (the Date setter
            // re-syncs _editor/text via SyncTextFromDate), unlike incremental typing which only commits on
            // LostFocus/Enter (CommitTextBoxValue).
            Date = pastedDate;
        }

        e.CancelCommand();
    }

    private static string ExtractDigits(string text) => new([.. text.Where(char.IsDigit)]);

    private void ApplyEditorDigit(char digit)
    {
        if (_textBox is null) return;

        var partBeforeDigit = _editor.ActivePart;
        if (!_editor.ApplyDigit(digit)) return; // calendar-illegal or buffer full -- leave text/caret as-is

        var after = _editor.FormatDisplay();
        var partAfterDigit = _editor.ActivePart;
        var rendered = RenderDisplay(after);
        var advanced = partAfterDigit != partBeforeDigit;

        _syncingText = true;
        try
        {
            _textBox.Text = rendered;

            if (advanced && rendered.Length > 0)
            {
                // Requester-corrected 2026-08-07 (post-P2 F5): completing a segment must SELECT the newly
                // active one exactly like a click/arrow/`/` would (SelectSegment does both _editor.SelectPart
                // -- marks replace-part so the very next digit overwrites -- and the visual TextBox.Select
                // highlight), not just place a bare caret. Text must already be rendered before this call --
                // SelectSegment only selects, it never (re)writes Text.
                SelectSegment(partAfterDigit);
                return;
            }

            // Same-part, not-yet-complete case (e.g. the first digit of a 2-digit day that isn't 4-9
            // auto-fill): plain caret right after the digit just typed, no selection. Derived straight from
            // the engine's authoritative IndexInPart (see its doc comment) rather than diffing display
            // strings. rendered.Length == 0 IS reachable by typing (not defense-only): a whole-selected empty
            // Day accepting '0' renders FormatDisplay()'s all-unfilled "00/00/0000", which RenderDisplay
            // collapses to "" (AllUnfilledDisplay, pre-existing since fix round 1) -- caret 0 is correct there.
            _textBox.CaretIndex = rendered.Length == 0 ? 0 : SegmentRange(_editor.ActivePart).Start + _editor.IndexInPart;
        }
        finally { _syncingText = false; }
    }

    private void ApplyEditorBackspace()
    {
        if (_textBox is null) return;

        if (!_editor.ApplyBackspace()) return; // already at the very start -- nothing to remove

        var rendered = RenderDisplay(_editor.FormatDisplay());
        _syncingText = true;
        try
        {
            _textBox.Text = rendered;
            // Derived straight from the engine's authoritative IndexInPart (see its doc comment) rather
            // than diffing display strings.
            if (rendered.Length == 0)
            {
                // Mirror SelectSegment's empty-field branch: reset ActivePart to Day so the next digit
                // lands in Day, not whatever part was cleared last.
                _editor.SelectPart(DatePart.Day);
                _textBox.CaretIndex = 0;
            }
            else
            {
                _textBox.CaretIndex = SegmentRange(_editor.ActivePart).Start + _editor.IndexInPart;
            }
        }
        finally { _syncingText = false; }
    }

    private void ApplyEditorDelete()
    {
        if (_textBox is null) return;

        if (!_editor.ApplyDelete()) return; // nothing left to clear from the caret forward

        var rendered = RenderDisplay(_editor.FormatDisplay());
        _syncingText = true;
        try
        {
            _textBox.Text = rendered;
            if (rendered.Length == 0)
            {
                // Mirror SelectSegment's empty-field branch: reset ActivePart to Day so the next digit
                // lands in Day, not whatever part was cleared last.
                _editor.SelectPart(DatePart.Day);
                _textBox.CaretIndex = 0;
            }
            else
            {
                _textBox.CaretIndex = SegmentRange(_editor.ActivePart).Start + _editor.IndexInPart;
            }
        }
        finally { _syncingText = false; }
    }

    // Click (post default caret placement) / GotFocus land here with the caret's raw text-index; resolve it
    // to a DatePart and select that whole segment. Internal (not private) so headless tests can simulate a
    // "click at index N" without needing a live-window mouse event.
    internal void SelectSegmentAt(int caretIndex) => SelectSegment(PartFromCaret(caretIndex));

    internal DatePart ActivePart => _editor.ActivePart;

    private void SelectSegment(DatePart part)
    {
        if (_textBox is null) return;

        if (_textBox.Text.Length == 0)
        {
            // Nothing typed yet (an empty field, post-Finding-1 clear) -- segment text coordinates don't exist
            // to Select() against. Land on Day with a plain caret instead. (TextBox.Select clamps positive
            // out-of-range values rather than throwing -- the guard is needed for the ENGINE's sake: without
            // it an empty field would be anchored on Month/Year instead of Day.)
            _editor.SelectPart(DatePart.Day);
            _textBox.CaretIndex = 0;
            return;
        }

        _editor.SelectPart(part);
        var (start, length) = SegmentRange(part);
        _textBox.Select(start, length);
    }

    // Makes the VISIBLE selection authoritative over the engine's ActivePart, immediately before any edit
    // applies. The user can produce a selection through paths this control never observes -- drag-select and
    // double-click word-select are handled entirely inside TextBoxBase's TextEditor -- so an ActivePart
    // updated only from our own click/keyboard handlers can silently disagree with the highlight the user is
    // looking at, and the edit then lands in the wrong segment (2026-08-07 F5). Reconciling here means every
    // edit path gets the same guarantee without each one having to know which gesture produced the selection.
    // A plain caret already inside ActivePart is left alone -- that is the normal mid-typing state and must
    // NOT be turned into a replace-part selection.
    // Scope limits, deliberate: (a) an EMPTY field returns early -- there are no segment coordinates to anchor
    // against; the "ActivePart can stay stale after a clear-to-empty" item (memory
    // a known follow-up) is NOT addressed here -- it is closed in ApplyEditorBackspace/ApplyEditorDelete,
    // not in this method; (b) a selection that spans part of a segment or several segments (reachable via
    // Ctrl+A / Shift+arrows, which this control does not intercept) is treated as its start position only --
    // it does NOT become a replace-that-text edit.
    private void SyncEditorPartFromSelection()
    {
        if (_textBox is null || _textBox.Text.Length == 0) return;

        if (IsWholeSegmentSelected(out var selected))
        {
            // Re-asserting SelectPart is deliberate even when it already matches ActivePart: it (re)marks
            // replace-part, which is what makes the next digit overwrite the whole segment.
            _editor.SelectPart(selected);
            return;
        }

        // Not a whole-segment highlight (plain caret, or a partial/multi-segment selection): anchor on the
        // selection's START (WPF always reports the lower index, so a right-to-left drag anchors on its far
        // end). Act ONLY when that part disagrees with the engine, and then select that whole segment, matching
        // what a click on it would have done; when they already agree, both the engine and the selection are
        // left exactly as they are.
        var anchored = PartFromCaret(_textBox.SelectionStart);
        if (anchored != _editor.ActivePart) SelectSegment(anchored);
    }

    private bool IsWholeSegmentSelected(out DatePart part)
    {
        part = _editor.ActivePart;
        if (_textBox is null) return false;

        foreach (var candidate in AllParts)
        {
            var (start, length) = SegmentRange(candidate);
            if (_textBox.SelectionStart != start || _textBox.SelectionLength != length) continue;
            part = candidate;
            return true;
        }

        return false;
    }

    private static readonly DatePart[] AllParts = [DatePart.Day, DatePart.Month, DatePart.Year];

    private static DatePart PartFromCaret(int caretIndex) => caretIndex switch
    {
        <= 2 => DatePart.Day,
        <= 5 => DatePart.Month,
        _ => DatePart.Year
    };

    // Fixed dd/MM/yyyy layout (FormatDisplay's contract): 0-1 day, 2 '/', 3-4 month, 5 '/', 6-9 year.
    private static (int Start, int Length) SegmentRange(DatePart part) => part switch
    {
        DatePart.Day => (0, 2),
        DatePart.Month => (3, 2),
        _ => (6, 4)
    };

    private static DatePart? NextPart(DatePart part) => part switch
    {
        DatePart.Day => DatePart.Month,
        DatePart.Month => DatePart.Year,
        _ => null
    };

    private static DatePart? PreviousPart(DatePart part) => part switch
    {
        DatePart.Year => DatePart.Month,
        DatePart.Month => DatePart.Day,
        _ => null
    };

    // Collapses the engine's all-unfilled FormatDisplay() ("00/00/0000") down to "" for display -- see
    // AllUnfilledDisplay. Every direct Text-write in this region routes through this instead of
    // FormatDisplay() straight, so a keyboard clear (Backspace/Delete down to nothing) shows an empty field,
    // matching the pre-existing empty-Date UX (SyncTextFromDate) instead of reverting to all-zero.
    private static string RenderDisplay(string display) => display == AllUnfilledDisplay ? string.Empty : display;
}
