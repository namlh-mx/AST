using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AST.Controls;
using AST.Core.Presentation;
using FluentAssertions;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstDateBox: DP defaults/round-trips + the OnApplyTemplate wiring logic
// (LostFocus parse, calendar navigate-on-open, pick-writes-Date-and-closes). The keystroke-level masking
// (PreviewTextInput/PreviewKeyDown/paste) is WPF input-system behaviour that needs a live window to exercise
// faithfully -- that polish is the Tier-2 requester F5 gate (R3), not covered here. FrameworkElementFactory
// (not XamlReader) keeps this test independent of the real keyed style in Controls.xaml.
public class AstDateBoxTests
{
    private static ControlTemplate BuildTemplate()
    {
        var template = new ControlTemplate(typeof(AstDateBox));
        var root = new FrameworkElementFactory(typeof(Grid));
        var textBox = new FrameworkElementFactory(typeof(UiTextBox), "PART_TextBox");
        var glyphToggle = new FrameworkElementFactory(typeof(ToggleButton), "PART_GlyphToggle");
        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        // Mirrors the real keyed style's ElementName binding (Controls.xaml, TwoWay) -- open-state has
        // exactly one authority, PART_GlyphToggle.IsChecked (see the class-header invariant); the popup
        // must actually close through this binding here too, or a close assertion would test nothing.
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ToggleButton.IsChecked)) { ElementName = "PART_GlyphToggle", Mode = BindingMode.TwoWay });
        var calendar = new FrameworkElementFactory(typeof(Calendar), "PART_Calendar");
        popup.AppendChild(calendar);
        root.AppendChild(textBox);
        root.AppendChild(glyphToggle);
        root.AppendChild(popup);
        template.VisualTree = root;
        return template;
    }

    [Fact]
    public void Date_default_is_null()
        => Assert.Null(AstDateBox.DateProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void ShowCalendarGlyph_default_is_false()
        => Assert.False((bool)AstDateBox.ShowCalendarGlyphProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Today_default_is_default_date()
        => Assert.Equal(default, (DateOnly)AstDateBox.TodayProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Date_round_trips() => Sta.Run(() =>
    {
        var box = new AstDateBox { Date = new DateOnly(2026, 7, 23) };
        Assert.Equal(new DateOnly(2026, 7, 23), box.Date);
    });

    [Fact]
    public void ShowCalendarGlyph_round_trips() => Sta.Run(() =>
    {
        var box = new AstDateBox { ShowCalendarGlyph = true };
        Assert.True(box.ShowCalendarGlyph);
    });

    [Fact]
    public void Today_round_trips() => Sta.Run(() =>
    {
        var box = new AstDateBox { Today = new DateOnly(2026, 7, 23) };
        Assert.Equal(new DateOnly(2026, 7, 23), box.Today);
    });

    [Fact]
    public void Valid_text_on_lost_focus_parses_into_Date() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Text = "23/07/2026";
        textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

        Assert.Equal(new DateOnly(2026, 7, 23), box.Date);
    });

    [Fact]
    public void Valid_text_on_Enter_parses_into_Date() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Text = "23/07/2026";

        // Fix round 1: do NOT attach an HwndSource RootVisual (that path freezes Wpf.Ui brushes
        // across STA threads when the full AST.App.Tests suite runs). KeyEventArgs's ctor also
        // ArgumentNullException-throws on a null PresentationSource (observed on net10.0), so use a
        // no-op PresentationSource stub — never Measure, never resolve default styles for a window.
        using var source = new HeadlessPresentationSource();
        var args = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            source,
            timestamp: 0,
            key: System.Windows.Input.Key.Enter)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        };
        textBox.RaiseEvent(args);

        Assert.Equal(new DateOnly(2026, 7, 23), box.Date);
        Assert.True(args.Handled);
        // Keyboard.ClearFocus() after Enter = requester F5 (caret stops), not headless-asserted.
    });

    /// <summary>
    /// Minimal PresentationSource satisfying KeyEventArgs's non-null inputSource requirement
    /// without creating an HWND or attaching a RootVisual (see Valid_text_on_Enter_parses_into_Date).
    /// </summary>
    private sealed class HeadlessPresentationSource : System.Windows.PresentationSource, IDisposable
    {
        public HeadlessPresentationSource() => AddSource();

        public override System.Windows.Media.Visual RootVisual
        {
            get => null!;
            set { /* never attach — RootVisual layout is what trips Wpf.Ui Freezable races */ }
        }

        public override bool IsDisposed => _disposed;

        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;

        public void Dispose()
        {
            if (_disposed) return;
            RemoveSource();
            _disposed = true;
        }

        private bool _disposed;
    }

    [Fact]
    public void Invalid_text_on_lost_focus_leaves_Date_unchanged_and_reverts_text() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Text = "not a date";
        textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

        Assert.Equal(new DateOnly(2026, 7, 23), box.Date);
        Assert.Equal("23/07/2026", textBox.Text);
    });

    // Date set no longer preselects Calendar.SelectedDate -- only DisplayDate navigates to its month.
    // Preselecting made the first click on that SAME date a same-value reselect that Calendar's
    // SelectedDatesChanged never fires for (the reported bug: reopening an already-set date and clicking
    // it again did nothing). Leaving SelectedDate null makes every pick a genuine null->value change,
    // which Calendar always raises SelectedDatesChanged for -- one path for every date, no click/mouse
    // interception layers needed. The current Date is NOT visually highlighted in the popup (a
    // CalendarDayButtonStyle override for this was tried and reverted 2026-07-29, Controls.xaml).
    [Fact]
    public void Opening_glyph_with_Date_set_navigates_to_its_month_without_preselecting_it() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 8, 1) };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        var calendar = (Calendar)box.Template.FindName("PART_Calendar", box)!;

        toggle.IsChecked = true;

        Assert.Equal(new DateTime(2026, 8, 1), calendar.DisplayDate);
        Assert.Null(calendar.SelectedDate); // NOT preselected -- picking THIS date must still be a change
        Assert.Equal(new DateOnly(2026, 8, 1), box.Date); // unchanged -- opening must not write back
    });

    // Same rule applies when Date is empty: only DisplayDate navigates to Today's month, SelectedDate
    // stays null.
    [Fact]
    public void Opening_glyph_with_Date_empty_shows_Todays_month_without_preselecting_it() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Today = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        var calendar = (Calendar)box.Template.FindName("PART_Calendar", box)!;

        toggle.IsChecked = true;

        Assert.Equal(new DateTime(2026, 7, 23), calendar.DisplayDate);
        Assert.Null(calendar.SelectedDate); // NOT preselected -- the fix for the same-value-reselect bug
        Assert.Null(box.Date); // opening the glyph must never write Today into Date on its own
    });

    [Fact]
    public void Picking_a_calendar_date_writes_Date_and_closes_the_popup() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        var popup = (Popup)box.Template.FindName("PART_Popup", box)!;
        var calendar = (Calendar)box.Template.FindName("PART_Calendar", box)!;
        toggle.IsChecked = true; // opens the popup through the ElementName binding set up in BuildTemplate

        calendar.SelectedDate = new DateTime(2026, 9, 15);

        Assert.Equal(new DateOnly(2026, 9, 15), box.Date);
        Assert.False(popup.IsOpen);
        Assert.False(toggle.IsChecked);
    });

    // Reopening an already-set date and clicking that SAME date again now goes through the same path as
    // any other pick (see Opening_glyph_with_Date_set_navigates_to_its_month_without_preselecting_it --
    // SelectedDate is never preselected), so it needs no separate regression test: a null->value
    // transition on the exact date that used to be in Date is not distinguishable from any other pick.
    [Fact]
    public void Picking_the_date_already_in_the_field_after_reopening_still_writes_Date_and_closes_the_popup()
        => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 29) };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        var popup = (Popup)box.Template.FindName("PART_Popup", box)!;
        var calendar = (Calendar)box.Template.FindName("PART_Calendar", box)!;
        toggle.IsChecked = true; // opens the popup through the ElementName binding set up in BuildTemplate
        Assert.Null(calendar.SelectedDate); // not preselected, even though Date already holds this value

        calendar.SelectedDate = new DateTime(2026, 7, 29); // the same date Date already holds

        Assert.Equal(new DateOnly(2026, 7, 29), box.Date);
        Assert.False(popup.IsOpen);
        Assert.False(toggle.IsChecked);
    });

    // Regression lock for the 2026-07-31 reopen-flicker fix (3rd occurrence of "two writers of one
    // open-state" on this control -- see the class-header invariant). A same-gesture click on the glyph
    // while its popup is already open must not re-toggle it.
    [Fact]
    public void Opening_glyph_disables_its_own_hit_testing_while_popup_is_open() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;

        toggle.IsChecked = true;

        Assert.False(toggle.IsHitTestVisible);
    });

    // The restore must NOT be synchronous: Popup closes on PreviewMouseLeftButtonUp, so a synchronous
    // restore in Unchecked would let the SAME mouse-up gesture re-Click the now-hit-testable glyph and
    // reopen it -- exactly the round-3 regression this fix corrects (see OnGlyphUnchecked).
    [Fact]
    public void Closing_popup_restores_glyph_hit_testing_only_after_the_dispatcher_idles() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        toggle.IsChecked = true;

        toggle.IsChecked = false;
        Assert.False(toggle.IsHitTestVisible);

        PumpDispatcherToIdle();
        Assert.True(toggle.IsHitTestVisible);
    });

    // Pins "exactly one authority for open-state": a second independent writer (the old Popup.Closed
    // handler this fix removed) would double-fire Unchecked on a pick.
    [Fact]
    public void Picking_a_calendar_date_transitions_the_glyphs_checked_state_exactly_once() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var toggle = (ToggleButton)box.Template.FindName("PART_GlyphToggle", box)!;
        var calendar = (Calendar)box.Template.FindName("PART_Calendar", box)!;
        toggle.IsChecked = true;
        var uncheckedCount = 0;
        toggle.Unchecked += (_, _) => uncheckedCount++;

        calendar.SelectedDate = new DateTime(2026, 9, 15);

        Assert.Equal(1, uncheckedCount);
    });

    // Classic WPF "DoEvents": queues a callback at the given priority and pumps the dispatcher until it
    // runs, draining everything queued at or above that priority first (FIFO within a priority) -- the
    // Sta.Run STA thread never calls Dispatcher.Run(), so BeginInvoke callbacks otherwise never execute.
    private static void PumpDispatcherToIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // ---- P2: dd/MM/yyyy segment mask (DdMmYyyySegmentEditor wiring) ----
    // Keystroke-level events are raised directly on PART_TextBox (RaiseEvent), bypassing the full WPF input
    // system, the same headless strategy the Enter-key test above already uses (HeadlessPresentationSource).
    // Click is simulated via the internal SelectSegmentAt (InternalsVisibleTo AST.App.Tests) instead of a
    // real mouse event -- it is exactly what the real PreviewMouseLeftButtonUp/GotFocus handlers call.

    [Fact]
    public void Typing_digits_auto_advances_day_month_year_and_rejects_illegal_month_digit() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "5");
        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal("15/00/0000", textBox.Text);
        // Requester F5 finding (2026-08-07): completing Day must SELECT the newly active Month segment, not
        // just place a bare caret -- Click_selects_whole_segment_and_typing_overwrites_it below exercises the
        // "next digit overwrites it" consequence of this same selection.
        Assert.Equal(3, textBox.SelectionStart);
        Assert.Equal(2, textBox.SelectionLength);

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "3"); // month 13 is calendar-illegal -- rejected, stays on the '1' tens digit

        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal("15/10/0000", textBox.Text);
    });


    // Bug 1 fix (requester F5, 2026-08-07): completing Day by typing must immediately SELECT Month (not a
    // bare caret) so the very next digit overwrites Month's existing "07" without an extra click/arrow first.
    [Fact]
    public void Completing_Day_by_typing_selects_Month_so_the_next_digit_immediately_overwrites_it() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 5) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        Assert.Equal("05/07/2026", textBox.Text);

        box.SelectSegmentAt(0); // put the whole Day segment into edit (as a click on it would)
        RaiseTextInput(textBox, "3");
        RaiseTextInput(textBox, "0"); // completes Day = 30, auto-advances to Month

        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal(3, textBox.SelectionStart);
        Assert.Equal(2, textBox.SelectionLength);

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "1"); // overwrites Month's existing "07" -> 11, auto-advances to Year

        Assert.Equal("30/11/2026", textBox.Text); // day kept, month overwritten in place, year untouched
        Assert.Equal(DatePart.Year, box.ActivePart);
        Assert.Equal(6, textBox.SelectionStart);
        Assert.Equal(4, textBox.SelectionLength);
    });


    // Fix round 3 (the UI review Critical-1, case A; requester-confirmed 2026-08-07): right after typing
    // completes Day and auto-advances-and-selects Month, Month is EMPTY -- pressing Backspace there must still
    // clear it (a harmless no-op display-wise) rather than silently doing nothing. Before the P1 fast path
    // dropped its "must be fully filled" gate, SelectPart(Month) + ApplyDelete() fell through to the per-slot
    // loop, found nothing filled, and returned false -- and because failure left the selection untouched
    // (never collapsed), Backspace was a PERMANENTLY dead key in that state, not just a one-off miss: every
    // subsequent press repeated the exact same no-op until the user clicked or arrowed away, so there was no
    // keyboard-only way to backspace out of an empty auto-advanced segment back into the previous part.
    // UX consequence of this fix (follows directly from the requester's "a selected segment always clears in
    // full" rule, not a separate decision): undoing the very digit that triggered an auto-advance onto an
    // EMPTY segment now takes two Backspaces -- the first clears/collapses the (already-empty) new segment,
    // the second is the one that actually walks back into the previous part.
    [Fact]
    public void Backspace_right_after_auto_advance_onto_an_empty_segment_clears_it_instead_of_doing_nothing() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "5"); // completes Day, auto-advances to and SELECTS the (empty) Month segment
        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal(2, textBox.SelectionLength); // whole Month segment is highlighted

        RaiseKeyDown(textBox, Key.Back);

        Assert.Equal("15/00/0000", textBox.Text); // unchanged display -- Month was already empty
        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal(0, textBox.SelectionLength); // selection collapsed -- Backspace actually did something
    });

    // Fix round 3 (the UI review Critical-1, case B; requester-confirmed 2026-08-07): if the auto-advanced
    // Month is instead already FILLED (editing an existing date), Backspace must wipe the WHOLE segment, not
    // "undo the last Day digit typed" -- a selected segment always clears in full on Backspace/Delete, with no
    // special-casing for how the selection was reached (click vs auto-advance).
    // Round-2 regression test (NOT round-3-discriminating -- the UI review round 4 flagged the original
    // "case B / fix round 3" framing as overstated): Month here is FULLY filled ("07"), so this already passed
    // under round 2's fix (Year/any-part fully-filled fast path); it pins that the auto-advance-REACHED
    // selection (not just a click-reached one) also gets the requester's "a selected segment always clears in
    // full" treatment. The genuinely round-3-discriminating case (auto-advance landing on a PARTIALLY filled
    // segment) is covered separately below.
    [Fact]
    public void Backspace_right_after_auto_advance_onto_a_filled_segment_wipes_the_whole_segment() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 5) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        Assert.Equal("05/07/2026", textBox.Text);

        box.SelectSegmentAt(0); // click into Day
        RaiseTextInput(textBox, "3");
        RaiseTextInput(textBox, "0"); // completes Day = 30, auto-advances to and selects Month ("07", filled)
        Assert.Equal(DatePart.Month, box.ActivePart);

        RaiseKeyDown(textBox, Key.Back);

        Assert.Equal("30/00/2026", textBox.Text); // whole Month wiped, not just the last Day digit undone
    });

    // Fix round 3: auto-advance can also land on a segment
    // that is only PARTIALLY filled (here: Year "1900", 2 of 4 slots) -- e.g. the user started overwriting
    // Year, moved away without finishing, then retyped Day/Month and auto-advanced straight back onto that
    // same not-yet-complete Year. Under the OLD gate (CountFilled == PartLen required for the whole-clear fast
    // path), this Backspace would fall through to a per-slot delete and leave a residual "0900" behind instead
    // of clearing the whole segment -- the requester's "a selected segment always clears in full" rule applies
    // regardless of fill level, not just to fully-typed segments.
    [Fact]
    public void Backspace_right_after_auto_advance_onto_a_partially_filled_segment_wipes_the_whole_segment() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        Assert.Equal("23/07/2026", textBox.Text);

        box.SelectSegmentAt(9); // whole-select Year
        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "9"); // Year now "1900" -- only 2 of 4 slots filled, not yet complete

        Assert.True(RaiseKeyDown(textBox, Key.Left)); // Year -> Month
        Assert.True(RaiseKeyDown(textBox, Key.Left)); // Month -> Day
        Assert.Equal(DatePart.Day, box.ActivePart);

        RaiseTextInput(textBox, "3");
        RaiseTextInput(textBox, "0"); // completes Day, auto-advances to (and selects) Month
        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "1"); // completes Month, auto-advances back onto the still-partial Year
        Assert.Equal(DatePart.Year, box.ActivePart);
        Assert.Equal(6, textBox.SelectionStart);
        Assert.Equal(4, textBox.SelectionLength);

        RaiseKeyDown(textBox, Key.Back);

        Assert.Equal("30/11/0000", textBox.Text); // whole Year wiped -- not "30/11/0900" (old per-slot residue)
    });

    [Fact]
    public void Click_selects_whole_segment_and_typing_overwrites_it() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        box.SelectSegmentAt(4); // lands inside the month segment (text index 3-4)
        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal(3, textBox.SelectionStart);
        Assert.Equal(2, textBox.SelectionLength);

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "2");

        Assert.Equal("23/12/2026", textBox.Text); // day untouched, month overwritten
        Assert.Equal(DatePart.Year, box.ActivePart); // completing the month auto-advanced
        // Same requester F5 finding as the Day->Month test above: completing Month must SELECT Year, not
        // leave a bare caret -- the field's own year "2026" would otherwise need an extra click/arrow first.
        Assert.Equal(6, textBox.SelectionStart);
        Assert.Equal(4, textBox.SelectionLength);
    });

    [Fact]
    public void Arrow_keys_navigate_segments_and_select_the_whole_landed_part() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        box.SelectSegmentAt(0); // Day

        Assert.True(RaiseKeyDown(textBox, Key.Right));
        Assert.Equal(DatePart.Month, box.ActivePart);
        Assert.Equal(3, textBox.SelectionStart);
        Assert.Equal(2, textBox.SelectionLength);

        Assert.True(RaiseKeyDown(textBox, Key.Right));
        Assert.Equal(DatePart.Year, box.ActivePart);
        Assert.Equal(6, textBox.SelectionStart);
        Assert.Equal(4, textBox.SelectionLength);

        Assert.True(RaiseKeyDown(textBox, Key.Right)); // no-op past Year, still handled
        Assert.Equal(DatePart.Year, box.ActivePart);

        Assert.True(RaiseKeyDown(textBox, Key.Left));
        Assert.Equal(DatePart.Month, box.ActivePart);
    });

    [Fact]
    public void Slash_advances_the_active_segment_but_never_moves_backward() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        box.SelectSegmentAt(0); // Day

        RaiseTextInput(textBox, "/");
        Assert.Equal(DatePart.Month, box.ActivePart);

        RaiseTextInput(textBox, "/");
        Assert.Equal(DatePart.Year, box.ActivePart);

        RaiseTextInput(textBox, "/"); // no-op past Year
        Assert.Equal(DatePart.Year, box.ActivePart);
    });

    [Fact]
    public void Delete_with_whole_segment_selected_clears_only_that_segment() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        // Select the whole Day segment WITHOUT going through SelectSegmentAt/SelectSegment -- that path
        // already calls _editor.SelectPart itself, which would make the OnTextBoxPreviewKeyDown guard a
        // no-op and let this test pass even if the guard were deleted (feedback-tautological-coverage-test).
        // TextBox.Select alone leaves _editor's ActivePart=Day/_replacePart=false untouched (mirrors a real
        // selection arriving another way, e.g. Ctrl+A landing on exactly one segment).
        textBox.Select(0, 2);
        Assert.True(RaiseKeyDown(textBox, Key.Delete));

        // If the guard were absent, ApplyDelete would run its normal single-slot path (ActivePart=Day,
        // _replacePart still false) and only clear the '2' -> "03/07/2026", not "00/07/2026".
        Assert.Equal("00/07/2026", textBox.Text);
    });

    [Fact]
    public void Backspace_with_whole_segment_selected_clears_the_whole_segment_like_Delete() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        // Same non-tautological setup as the Delete test above -- TextBox.Select alone, not SelectSegmentAt.
        textBox.Select(0, 2);
        Assert.True(RaiseKeyDown(textBox, Key.Back));

        // ApplyBackspace alone (no whole-part fast path) would only clear the day's LAST filled slot ('3')
        // -> "02/07/2026". Only routing through SelectPart + ApplyDelete (this control's requester-confirmed
        // 2026-08-07 decision: Backspace over a full segment selection behaves identically to Delete on it)
        // clears both slots.
        Assert.Equal("00/07/2026", textBox.Text);
    });


    // Bug 2 fix (requester F5, 2026-08-07): Year used to be excluded from the P1 engine's whole-part-clear
    // fast path (per-slot only), so a Delete/Backspace over a whole selected Year segment only cleared one
    // digit at a time, unlike Day/Month. The engine now covers Year too -- this pins Delete and Backspace as
    // symmetric for Year, matching Delete_with_whole_segment_selected_clears_only_that_segment /
    // Backspace_with_whole_segment_selected_clears_the_whole_segment_like_Delete above for Day.
    [Fact]
    public void Delete_with_whole_Year_segment_selected_clears_all_four_slots_in_one_press() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        // Whole Year segment selected WITHOUT SelectSegmentAt (same non-tautological setup as the Day tests).
        textBox.Select(6, 4);
        Assert.True(RaiseKeyDown(textBox, Key.Delete));

        Assert.Equal("23/07/0000", textBox.Text);
    });

    [Fact]
    public void Backspace_with_whole_Year_segment_selected_clears_all_four_slots_in_one_press() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Select(6, 4);
        Assert.True(RaiseKeyDown(textBox, Key.Back));

        Assert.Equal("23/07/0000", textBox.Text);
    });

    [Fact]
    public void Delete_with_only_a_caret_removes_a_single_slot() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.CaretIndex = 0; // caret only -- no SelectSegmentAt/GotFocus involved, no whole-segment selection
        Assert.True(RaiseKeyDown(textBox, Key.Delete));

        Assert.Equal("03/07/2026", textBox.Text); // only the day's tens slot ('2') is cleared
    });

    [Fact]
    public void Typing_a_placeholder_matching_digit_mid_segment_does_not_move_the_caret_backward() => Sta.Run(() =>
    {
        // 2026-08-07 F5 regression: caret placement used to diff FormatDisplay() strings before/after a
        // keystroke, which cannot tell "no change" from "changed to the same placeholder digit '0'" -- typing
        // '0' right after '2' into an emptied Year looked textually identical to the still-empty slots and
        // sent the caret back to the segment start. Fixed by deriving caret index straight from the engine's
        // IndexInPart instead of a text diff (AST.Core.Presentation.DdMmYyyySegmentEditor.IndexInPart).
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Select(6, 4); // whole Year segment
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();
        textBox.Text.Should().Be("23/07/0000");

        RaiseTextInput(textBox, "2");
        textBox.CaretIndex.Should().Be(7); // right after the '2' just typed (Year starts at index 6)

        RaiseTextInput(textBox, "0"); // '0' matches the still-unfilled placeholder already shown there
        textBox.Text.Should().Be("23/07/2000");
        textBox.CaretIndex.Should().Be(8); // must keep advancing, not jump back to the Year segment start

        RaiseTextInput(textBox, "2");
        textBox.CaretIndex.Should().Be(9);

        RaiseTextInput(textBox, "6"); // completes Year -- caret must reach the very end, not stop short
        textBox.Text.Should().Be("23/07/2026");
        textBox.CaretIndex.Should().Be(10);
    });

    // Regression test for AST.App.Tests debt item 2 (found during AstDateBox P2, 2026-08-07; fixed
    // 2026-08-08). INVARIANT: whenever the field renders empty, ActivePart is Day -- a freshly-blank field
    // must behave identically no matter HOW it was emptied. The rule lives in three places that can empty
    // the field: SelectSegment's empty-text branch (click/GotFocus on an empty field) and the empty-render
    // branches of ApplyEditorDelete and ApplyEditorBackspace. The bug was that only the first knew the rule,
    // so clearing by keystroke left ActivePart on whatever segment was cleared LAST (typically Year) and the
    // next digit typed landed there instead of in Day.
    // Backspace twin of Clearing_the_field_down_to_empty_resets_ActivePart_to_Day (which only
    // exercises ApplyEditorDelete). A WHOLE-SEGMENT selection on Key.Back routes to ApplyEditorDelete
    // via IsWholeSegmentSelected — and SyncEditorPartFromSelection can itself re-anchor a caret that
    // sits in a DIFFERENT part into a whole-segment selection. So this test must land ActivePart on
    // the sole remaining filled part (Year), then use a PLAIN caret inside that same part, and
    // Backspace slot-by-slot — otherwise it silently becomes a second Delete test.
    [Fact]
    public void Clearing_the_field_down_to_empty_with_Backspace_resets_ActivePart_to_Day() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Select(0, 2);
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();
        textBox.Select(3, 2);
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();
        // Text is now "00/00/2026" — only Year filled. Select Year so ActivePart matches, then
        // collapse to a plain caret (empirical dump: Caret=10, SelLen=0) before Backspacing.
        box.SelectSegmentAt(8);
        textBox.CaretIndex = 10;
        textBox.SelectionLength = 0;

        RaiseKeyDown(textBox, Key.Back).Should().BeTrue();
        RaiseKeyDown(textBox, Key.Back).Should().BeTrue();
        RaiseKeyDown(textBox, Key.Back).Should().BeTrue();
        RaiseKeyDown(textBox, Key.Back).Should().BeTrue();

        textBox.Text.Should().BeEmpty("all three parts are now unfilled — FormatDisplay's all-unfilled string collapses to \"\" via RenderDisplay");
        box.ActivePart.Should().Be(DatePart.Day,
            "the field is fully empty — the very next digit typed must start at Day (matching SelectSegment's own empty-text branch), not wherever the caret happened to last be (Year)");

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "5");
        textBox.Text.Should().Be("15/00/0000", "typing after a full clear must fill Day first, exactly like typing into a never-touched blank AstDateBox does");
    });

    [Fact]
    public void Clearing_the_field_down_to_empty_resets_ActivePart_to_Day() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        textBox.Select(0, 2); // whole Day segment
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();
        textBox.Select(3, 2); // whole Month segment
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();
        textBox.Select(6, 4); // whole Year segment -- clearing this one empties the WHOLE field
        RaiseKeyDown(textBox, Key.Delete).Should().BeTrue();

        textBox.Text.Should().BeEmpty("all three parts are now unfilled -- FormatDisplay's all-unfilled string collapses to \"\" via RenderDisplay");
        box.ActivePart.Should().Be(DatePart.Day,
            "the field is fully empty -- the very next digit typed must start at Day (matching SelectSegment's own empty-text branch), not wherever the caret happened to last be (Year)");

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "5");
        textBox.Text.Should().Be("15/00/0000", "typing after a full clear must fill Day first, exactly like typing into a never-touched blank AstDateBox does");
    });

    [Fact]
    public void Typing_over_a_selection_the_control_did_not_itself_make_edits_the_selected_segment() => Sta.Run(() =>
    {
        // 2026-08-07 F5 regression, root cause: the engine's ActivePart was only ever updated by this
        // control's OWN click/keyboard handlers, and the live mouse-up handler never fired (TextBoxBase marks
        // it Handled before instance handlers -- now subscribed with handledEventsToo). So a real selection
        // could disagree with ActivePart and the digit landed in the WRONG segment: typing over a highlighted
        // Year wrote into Day. `TextBox.Select` alone here reproduces exactly that divergence -- it does NOT
        // go through SelectSegmentAt/SelectSegment, so the engine is left on Day (SetDate's reset) while Year
        // is what is visibly highlighted, which is the whole point of the test (non-tautological: without
        // SyncEditorPartFromSelection these digits overwrite the DAY segment).
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        box.ActivePart.Should().Be(DatePart.Day); // engine still on Day...
        textBox.Select(6, 4);                     // ...while the user sees the whole Year highlighted

        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "9");
        RaiseTextInput(textBox, "9");
        RaiseTextInput(textBox, "9");

        textBox.Text.Should().Be("23/07/1999"); // Day and Month untouched
    });

    [Fact]
    public void A_mouse_up_the_TextBox_already_marked_Handled_still_selects_the_clicked_segment() => Sta.Run(() =>
    {
        // Pins the `handledEventsToo: true` subscription in OnApplyTemplate. A plain `MouseLeftButtonUp += ...`
        // never receives an already-Handled event, which is exactly what TextBoxBase's TextEditor produces in a
        // live window -- so clicking a segment silently left the engine on whatever part it held before
        // (2026-08-07 F5: typing over a highlighted Year wrote into Day). Discriminating: against a `+=`
        // subscription this handler is not invoked at all and ActivePart stays Day.
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        box.ActivePart.Should().Be(DatePart.Day);
        textBox.CaretIndex = 8; // as the TextBox's own mouse handling would leave it after a click on Year

        RaiseHandledMouseLeftButtonUp(textBox);

        box.ActivePart.Should().Be(DatePart.Year);
        textBox.SelectionStart.Should().Be(6);
        textBox.SelectionLength.Should().Be(4);
    });

    [Fact]
    public void Paste_of_a_full_valid_digit_string_sets_Date() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaisePaste(textBox, "15072026");

        Assert.Equal(new DateOnly(2026, 7, 15), box.Date);
        Assert.Equal("15/07/2026", textBox.Text);
    });

    // F5 round 1 (2026-08-07): illegal complete paste must leave the field untouched — no partial mask
    // (old behaviour left "30/00/0000" after replaying until the first rejected digit).
    [Fact]
    public void Paste_of_a_calendar_illegal_digit_string_leaves_the_field_untouched() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaisePaste(textBox, "32072026"); // day 32 — calendar-illegal

        box.Date.Should().BeNull();
        textBox.Text.Should().Be(string.Empty);
    });

    [Fact]
    public void Paste_of_an_invalid_formatted_date_leaves_the_current_value_untouched() => Sta.Run(() =>
    {
        var existing = new DateOnly(2026, 7, 23);
        var box = new AstDateBox { Template = BuildTemplate(), Date = existing };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaisePaste(textBox, "44/13/2026"); // day 44 / month 13 — invalid complete date

        box.Date.Should().Be(existing);
        textBox.Text.Should().Be("23/07/2026");
    });

    [Fact]
    public void Paste_of_an_incomplete_digit_string_leaves_the_current_value_untouched() => Sta.Run(() =>
    {
        var existing = new DateOnly(2026, 7, 23);
        var box = new AstDateBox { Template = BuildTemplate(), Date = existing };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaisePaste(textBox, "1507"); // only 4 digits — not a complete date

        box.Date.Should().Be(existing);
        textBox.Text.Should().Be("23/07/2026");
    });

    // Finding 5: a paste yielding zero digits (e.g. clipboard held
    // non-numeric text) must no-op, not wipe the current value via an unconditional SetDate(null).
    [Fact]
    public void Paste_with_no_digits_leaves_the_current_value_untouched() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaisePaste(textBox, "hello");

        box.Date.Should().Be(new DateOnly(2026, 7, 23));
        textBox.Text.Should().Be("23/07/2026");
    });

    // Finding 4: with the field empty (post-Finding-1 clear), segment
    // navigation must not compute Select(3,2)/Select(6,4) against an empty string (ArgumentOutOfRangeException)
    // -- it lands on Day with a plain caret instead.
    [Fact]
    public void Navigating_segments_on_an_empty_field_does_not_throw_and_lands_on_Day() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate() };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        Assert.Equal(string.Empty, textBox.Text);

        Assert.True(RaiseKeyDown(textBox, Key.Right));
        Assert.Equal(DatePart.Day, box.ActivePart);

        RaiseTextInput(textBox, "/");
        Assert.Equal(DatePart.Day, box.ActivePart);

        box.SelectSegmentAt(0);
        Assert.Equal(DatePart.Day, box.ActivePart);
        Assert.Equal(0, textBox.CaretIndex);
    });

    [Fact]
    public void Setting_Date_to_null_externally_renders_empty_text_not_all_zero() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;
        Assert.Equal("23/07/2026", textBox.Text);

        box.Date = null;

        Assert.Equal(string.Empty, textBox.Text);
    });


    // Regression lock for Finding 1: before this fix, clearing every digit
    // via the keyboard left the box showing the engine's all-unfilled "00/00/0000" (never ""), and on blur
    // CommitTextBoxValue's TryParse("00/00/0000") failed -- so Date stayed at its old value and the display
    // silently reverted to the formatted old date, undoing the user's clear gesture with no keyboard way back
    // to Date = null.
    [Fact]
    public void Clearing_every_digit_via_keyboard_empties_the_display_and_nulls_Date_on_blur() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 7, 23) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        // Navigate to Year (Right, Right) then collapse the whole-segment selection navigation leaves behind
        // (re-assigning CaretIndex to itself always collapses to a plain caret) -- Backspace only ever clears
        // within the currently-ACTIVE part and walks backward once a part is exhausted, so starting anywhere
        // but Year can never reach Day. Collapsing first is now load-bearing, not just for clarity: a leftover
        // whole-segment selection on the first Backspace would instead be re-anchored by
        // SyncEditorPartFromSelection onto the whole-clear fast path, which wipes Year in ONE press regardless
        // of fill level (fix round 3, 2026-08-07; the reconciliation itself landed later the same day)
        // -- not the 8-step per-slot trace this test exercises. Collapsing the selection first exercises the
        // same plain repeated-Backspace path a user gets after the first keystroke moves the caret.
        Assert.True(RaiseKeyDown(textBox, Key.Right));
        Assert.True(RaiseKeyDown(textBox, Key.Right));
        Assert.Equal(DatePart.Year, box.ActivePart);
        textBox.CaretIndex = textBox.CaretIndex;

        // 8 total digit slots (Day 2 + Month 2 + Year 4); each Backspace clears exactly one, including the
        // two part-boundary crossings (Year exhausted -> Month; Month exhausted -> Day).
        for (var i = 0; i < 8; i++)
        {
            Assert.True(RaiseKeyDown(textBox, Key.Back));
        }

        // Immediately: the display must show "" (the actual regression), not "00/00/0000".
        Assert.Equal(string.Empty, textBox.Text);

        // Date itself only ever commits on blur/Enter (CommitTextBoxValue, decision 8 -- unchanged by this
        // fix); simulate that blur and confirm the fix's mapping ("" -> Date = null) actually takes effect
        // instead of reverting to the pre-clear Date.
        textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

        Assert.Null(box.Date);
        Assert.Equal(string.Empty, textBox.Text);
    });

    // Headless limitation (feedback-headless-harness-cannot-test-event-wiring): proves only that the
    // control marks Space Handled and does not mutate its own text/selection. It does NOT prove WPF
    // TextEditor's default space-insert is suppressed live — that remains a Tier-3 F5 check.
    [Fact]
    public void Space_with_segment_selected_is_handled_and_leaves_text_and_selection_unchanged() => Sta.Run(() =>
    {
        var box = new AstDateBox { Template = BuildTemplate(), Date = new DateOnly(2026, 8, 7) };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaiseGotFocus(textBox);
        RaiseKeyDown(textBox, Key.Right);
        RaiseKeyDown(textBox, Key.Right); // Year segment selected

        var textBefore = textBox.Text;
        var selStart = textBox.SelectionStart;
        var selLen = textBox.SelectionLength;

        RaiseKeyDown(textBox, Key.Space).Should().BeTrue();
        textBox.Text.Should().Be(textBefore);
        textBox.SelectionStart.Should().Be(selStart);
        textBox.SelectionLength.Should().Be(selLen);
    });

    [Fact]
    public void Escape_after_editing_restores_pre_focus_Date_and_clears_focus() => Sta.Run(() =>
    {
        var preEdit = new DateOnly(2026, 7, 23);
        var box = new AstDateBox { Template = BuildTemplate(), Date = preEdit };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaiseGotFocus(textBox);
        RaiseTextInput(textBox, "1");
        RaiseTextInput(textBox, "5"); // Day becomes 15 without committing Date
        textBox.Text.Should().Be("15/07/2026");
        box.Date.Should().Be(preEdit);

        RaiseKeyDown(textBox, Key.Escape).Should().BeTrue();

        box.Date.Should().Be(preEdit);
        textBox.Text.Should().Be("23/07/2026");
        // Keyboard.ClearFocus() after Esc = requester F5 round 1 (end edit), not headless-asserted
        // (same posture as Valid_text_on_Enter_parses_into_Date).
    });

    // Paste commits Date immediately; Esc still restores the GotFocus snapshot (not the post-paste Date).
    [Fact]
    public void Escape_after_valid_paste_restores_the_Date_captured_at_focus_enter() => Sta.Run(() =>
    {
        var preEdit = new DateOnly(2026, 7, 23);
        var box = new AstDateBox { Template = BuildTemplate(), Date = preEdit };
        box.ApplyTemplate();
        var textBox = (UiTextBox)box.Template.FindName("PART_TextBox", box)!;

        RaiseGotFocus(textBox);
        RaisePaste(textBox, "15082026");
        box.Date.Should().Be(new DateOnly(2026, 8, 15));
        textBox.Text.Should().Be("15/08/2026");

        RaiseKeyDown(textBox, Key.Escape).Should().BeTrue();

        box.Date.Should().Be(preEdit);
        textBox.Text.Should().Be("23/07/2026");
        // ClearFocus after Esc: F5, see Escape_after_editing_restores_pre_focus_Date_and_clears_focus.
    });

    private static void RaiseGotFocus(UiTextBox textBox)
    {
        textBox.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, textBox));
    }

    private static void RaiseTextInput(UiTextBox textBox, string text)
    {
        var composition = new TextComposition(InputManager.Current, textBox, text);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        };
        textBox.RaiseEvent(args);
    }

    private static bool RaiseKeyDown(UiTextBox textBox, Key key)
    {
        using var source = new HeadlessPresentationSource();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, timestamp: 0, key: key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        textBox.RaiseEvent(args);
        return args.Handled;
    }

    // Handled: true is the whole point -- TextBoxBase's own TextEditor class handler marks the real mouse-up
    // Handled before it reaches instance handlers, so this reproduces the live condition under which a plain
    // `MouseLeftButtonUp += ...` subscription silently never fires.
    private static void RaiseHandledMouseLeftButtonUp(UiTextBox textBox)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp: 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Handled = true
        };
        textBox.RaiseEvent(args);
    }

    private static void RaisePaste(UiTextBox textBox, string text)
    {
        var dataObject = new DataObject();
        dataObject.SetData(DataFormats.Text, text);
        var args = new DataObjectPastingEventArgs(dataObject, false, DataFormats.Text)
        {
            RoutedEvent = System.Windows.DataObject.PastingEvent
        };
        textBox.RaiseEvent(args);
    }
}
