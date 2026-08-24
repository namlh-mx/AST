using AST.Core.Presentation;
using FluentAssertions;

namespace AST.Core.Tests.Presentation;

public class DdMmYyyySegmentEditorTests
{
    private static DdMmYyyySegmentEditor New() => new();

    private static void Type(DdMmYyyySegmentEditor e, string digits)
    {
        foreach (char c in digits)
            e.ApplyDigit(c);
    }

    private static bool TypeAllAccepted(DdMmYyyySegmentEditor e, string digits)
    {
        foreach (char c in digits)
        {
            if (!e.ApplyDigit(c))
                return false;
        }

        return true;
    }

    private static string CaptureState(DdMmYyyySegmentEditor e)
    {
        var fill = e.CaptureFillState();
        var filled = string.Concat(fill.Filled.Select(f => f ? '1' : '0'));
        var digits = new string(fill.Digits);
        return $"{e.FormatDisplay()}|{e.ActivePart}|{e.TryGetDate(out var d)}|{(d == default ? "-" : d.ToString("O"))}|{filled}|{digits}";
    }

    private static void AssertNoInvalidAllFilledDate(DdMmYyyySegmentEditor e)
    {
        var fill = e.CaptureFillState();
        if (fill.Filled.Any(f => !f))
            return;

        // Spec §5a / §7.6: all 8 filled ⇒ calendar-valid DateOnly (never a filled illegal triple).
        Assert.True(e.TryGetDate(out _), "all slots filled must yield a valid DateOnly");
    }

    // --- §8 Baseline ---

    [Fact]
    public void Pristine_DisplaysZeros_AndTryGetDateFalse()
    {
        var e = New();
        Assert.Equal("00/00/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
        Assert.Equal(DatePart.Day, e.ActivePart);
    }

    [Fact]
    public void SetDate_Null_ClearsToPristine()
    {
        var e = New();
        e.SetDate(new DateOnly(2026, 8, 9));
        e.SetDate(null);
        Assert.Equal("00/00/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void SetDate_FillsAllSlots_AndTryGetDateTrue()
    {
        var e = New();
        e.SetDate(new DateOnly(2026, 8, 9));
        Assert.Equal("09/08/2026", e.FormatDisplay());
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(2026, 8, 9), d);
    }

    // --- §8 Requester + pad/reject ---

    [Fact]
    public void Day_Pad4_Becomes04_AdvancesToMonth()
    {
        var e = New();
        Assert.True(e.ApplyDigit('4'));
        Assert.Equal("04/00/0000", e.FormatDisplay());
        Assert.Equal(DatePart.Month, e.ActivePart);
    }

    [Fact]
    public void Day_ThreeThenTwo_Rejects32()
    {
        var e = New();
        Assert.True(e.ApplyDigit('3'));
        Assert.Equal("30/00/0000", e.FormatDisplay()); // display 0 in units, not semantically 30
        Assert.False(e.TryGetDate(out _));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('2'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void Month02_ThenDayFirstDigit3_Rejects_Then03Ok() // §5 J, DM5 (R), §8.6
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('2'));
        Assert.Equal("00/02/0000", e.FormatDisplay());
        Assert.Equal(DatePart.Year, e.ActivePart);

        e.SelectPart(DatePart.Day);
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('3'));
        Assert.Equal(before, CaptureState(e));

        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('3'));
        Assert.Equal("03/02/0000", e.FormatDisplay());
    }

    [Fact]
    public void Month08_Day31_Ok_Month04_Day31_Reject() // §8.7
    {
        var e = New();
        Type(e, "3108"); // D=31 M=08
        Assert.Equal("31/08/0000", e.FormatDisplay());

        e = New();
        Type(e, "31");
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('4'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void Month_Pad4_AndReject13_Accept12() // §8.8
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('4'));
        Assert.Equal("00/04/0000", e.FormatDisplay());
        Assert.Equal(DatePart.Year, e.ActivePart);

        e = New();
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('1'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('3'));
        Assert.Equal(before, CaptureState(e));
        Assert.True(e.ApplyDigit('2'));
        Assert.Equal("00/12/0000", e.FormatDisplay());
    }

    [Fact]
    public void Day30_ThenMonth02_RejectsCompletingDigit() // DM1 (R), §8.9
    {
        var e = New();
        Type(e, "30");
        Assert.Equal(DatePart.Month, e.ActivePart);
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('2'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void Day31_ThenMonth04_RejectsCompletingDigit() // DM2 (R), §8.10
    {
        var e = New();
        Type(e, "31");
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('4'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void SelectPartDay_Overwrite_OnSetDate() // §8.11
    {
        var e = New();
        e.SetDate(new DateOnly(2026, 8, 9));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('1'));
        Assert.True(e.ApplyDigit('5'));
        Assert.Equal("15/08/2026", e.FormatDisplay());
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(2026, 8, 15), d);
    }

    // --- Leap / year ---

    [Fact]
    public void Leap_29022024_TryGetDateTrue() // §8.12, LY3, PA3
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "29022024"));
        Assert.Equal("29/02/2024", e.FormatDisplay());
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(2024, 2, 29), d);
    }

    [Fact]
    public void Day29Month02_NonLeapYearCompletingDigit_Rejected() // LY2 (R), §8.13
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "2902202"));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('3')); // would be 2023
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void NonLeapYear_ThenMonth02_Day29_Rejected() // LY1 (R), §8.14
    {
        var e = New();
        e.SelectPart(DatePart.Year);
        Assert.True(TypeAllAccepted(e, "2023"));
        e.SelectPart(DatePart.Month);
        Assert.True(TypeAllAccepted(e, "02"));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('2'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('9'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void CenturyLeap2000_Ok_CenturyNonLeap1900_Reject() // LY4, §8.15
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "29022000"));
        Assert.True(e.TryGetDate(out _));

        e = New();
        Assert.True(TypeAllAccepted(e, "2902190"));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('0'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void CompletingYear0000_Rejected() // Y1, §8.16
    {
        var e = New();
        Type(e, "0101000");
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('0'));
        Assert.Equal(before, CaptureState(e));
    }

    // --- Prefix / pad / paste / order ---

    [Fact]
    public void DayPrefix3_CompleteMonth02_Rejected() // DM9, §8.17
    {
        var e = New();
        Assert.True(e.ApplyDigit('3'));
        Assert.Equal(DatePart.Day, e.ActivePart);
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('2'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void Day31_MonthPad2_Rejected() // DM12/P3 family, §8.18
    {
        var e = New();
        Type(e, "31");
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('2')); // pad → 02
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void Paste_29022024_Ok_30022024_DoesNotYieldIllegalComplete() // PA1/PA3, §8.19
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "29022024"));
        Assert.True(e.TryGetDate(out _));

        e = New();
        _ = TypeAllAccepted(e, "30022024"); // stops/rejects at illegal month completion
        Assert.False(e.TryGetDate(out _));
        // Must not sit on an illegal complete triple
        Assert.NotEqual("30/02/2024", e.FormatDisplay());
    }

    [Fact]
    public void Order_MonthThenDay_EnforcesFebNoLeading3() // O2, §8.20
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Type(e, "02");
        e.SelectPart(DatePart.Day);
        Assert.False(e.ApplyDigit('3'));
    }

    [Fact]
    public void Order_YearMonthDay_LeapKnownEarly_RejectsDay29NonLeap() // O3, §8.20
    {
        var e = New();
        e.SelectPart(DatePart.Year);
        Type(e, "2023");
        e.SelectPart(DatePart.Month);
        Type(e, "02");
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('2'));
        Assert.False(e.ApplyDigit('9'));
    }

    [Fact]
    public void PartialEdit_AfterSetDate_TryGetDateFalse_PinsAdapterLostFocusContract() // §8.21 (P2 relies on this)
    {
        var e = New();
        e.SetDate(new DateOnly(2026, 8, 9));
        Assert.True(e.TryGetDate(out _));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('1')); // only tens replaced so far mid-replace... SelectPart clears then one digit
        // After SelectPart + one digit '1', day is tens=1 only → not a complete date
        Assert.False(e.TryGetDate(out _));
        Assert.Equal("10/08/2026", e.FormatDisplay()); // units unfilled → display 0
    }

    // --- Additional §5b (R) + non-(R) coverage beyond §8 checklist ---

    [Fact]
    public void DM2_Day31_RejectMonth06() // DM2 (R) other 30-day months
    {
        var e = New();
        Type(e, "31");
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('6'));
    }

    [Fact]
    public void DM4_Day31_AcceptMonth01()
    {
        var e = New();
        Type(e, "31");
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('1'));
        Assert.Equal("31/01/0000", e.FormatDisplay());
    }

    [Fact]
    public void DM3_Day31_RejectMonth02()
    {
        var e = New();
        Type(e, "31");
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('2'));
    }

    [Fact]
    public void DM7_Month04_Day31_RejectUnits()
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Type(e, "04");
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('3'));
        Assert.False(e.ApplyDigit('1'));
    }

    [Fact]
    public void DM8_Month04_Day30_Accept()
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Type(e, "04");
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('3'));
        Assert.True(e.ApplyDigit('0'));
        Assert.Equal("30/04/0000", e.FormatDisplay());
    }

    [Fact]
    public void DM10_DayPrefix3_Month04_Accept_ThenUnitsFilter()
    {
        var e = New();
        Assert.True(e.ApplyDigit('3'));
        e.SelectPart(DatePart.Month);
        Assert.True(TypeAllAccepted(e, "04"));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('3'));
        Assert.False(e.ApplyDigit('1'));
        Assert.True(e.ApplyDigit('0'));
        Assert.Equal("30/04/0000", e.FormatDisplay());
    }

    [Fact]
    public void DM11_Day29_Month02_YearEmpty_Accept()
    {
        var e = New();
        Type(e, "2902");
        Assert.Equal("29/02/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void DM13_PadDay4_WithMonth02_Accept()
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Type(e, "02");
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('4'));
        Assert.Equal("04/02/0000", e.FormatDisplay());
    }

    [Fact]
    public void LY5_YearIncomplete_Feb_Day29_Allowed()
    {
        var e = New();
        Type(e, "2902");
        Assert.Equal("29/02/0000", e.FormatDisplay());
    }

    [Fact]
    public void LY6_OverwriteYear_LeapToNonLeap_Under2902_Reject()
    {
        var e = New();
        Type(e, "29022024");
        e.SelectPart(DatePart.Year);
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('2'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('3'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void LY7_LeapFeb_StillRejectDayLeading3()
    {
        var e = New();
        e.SelectPart(DatePart.Year);
        Type(e, "2024");
        e.SelectPart(DatePart.Month);
        Type(e, "02");
        e.SelectPart(DatePart.Day);
        Assert.False(e.ApplyDigit('3'));
    }

    [Fact]
    public void Y2_Year0001_With0101_Accept()
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "01010001"));
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(1, 1, 1), d);
    }

    [Fact]
    public void Y3_Year9999_With3112_Accept()
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "31129999"));
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(9999, 12, 31), d);
    }

    [Fact]
    public void Y4_PartialYear_TryGetDateFalse()
    {
        var e = New();
        Type(e, "010120");
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void O4_DayYearThenMonth04_WithDay31_Reject()
    {
        var e = New();
        Type(e, "31");
        e.SelectPart(DatePart.Year);
        Type(e, "2024");
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('4'));
    }

    [Fact]
    public void E4_BackspaceYear_Under2902_LeavesMaxDay29()
    {
        var e = New();
        Type(e, "29022024");
        e.SelectPart(DatePart.Year);
        // Backspace all year digits
        Assert.True(e.ApplyBackspace());
        Assert.True(e.ApplyBackspace());
        Assert.True(e.ApplyBackspace());
        Assert.True(e.ApplyBackspace());
        Assert.Equal("29/02/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void E5_E6_BackspaceMonth_ThenRetype02_WithDay31_Reject()
    {
        var e = New();
        Type(e, "3101");
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyBackspace());
        Assert.True(e.ApplyBackspace());
        Assert.Equal("31/00/0000", e.FormatDisplay());
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('2'));
    }

    [Fact]
    public void E7_SetDateNull_Pristine()
    {
        var e = New();
        Type(e, "15032024");
        e.SetDate(null);
        Assert.Equal("00/00/0000", e.FormatDisplay());
    }

    [Fact]
    public void P2_PadMonth9_WithDay31_Reject()
    {
        var e = New();
        Type(e, "31");
        Assert.False(e.ApplyDigit('9'));
    }

    [Fact]
    public void P4_PadMonth2_WithDay28_Accept()
    {
        var e = New();
        Type(e, "28");
        Assert.True(e.ApplyDigit('2'));
        Assert.Equal("28/02/0000", e.FormatDisplay());
    }

    [Fact]
    public void P5_MonthTens1_WithDay31_Rejects3to9()
    {
        var e = New();
        Type(e, "31");
        Assert.True(e.ApplyDigit('1'));
        Assert.False(e.ApplyDigit('3'));
        Assert.True(e.ApplyDigit('0')); // 10 OK for day 31
        Assert.Equal("31/10/0000", e.FormatDisplay());
    }

    [Fact]
    public void PA2_Paste29022023_NoIllegalCompleteTriple()
    {
        var e = New();
        _ = TypeAllAccepted(e, "29022023");
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void S1_DayTens3Only_DisplayShows30_ButNotADate()
    {
        var e = New();
        Assert.True(e.ApplyDigit('3'));
        Assert.Equal("30/00/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
    }

    [Fact]
    public void S2_Pristine_Vs_Entered0001_DistinguishedByFilledFlags()
    {
        var pristine = New();
        Assert.False(pristine.TryGetDate(out _));

        var entered = New();
        Assert.True(TypeAllAccepted(entered, "01010001"));
        Assert.True(entered.TryGetDate(out _));
    }

    [Fact]
    public void Reject_LeavesStateBitIdentical()
    {
        var e = New();
        Type(e, "30");
        e.ApplyDigit('0');
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('2'));
        Assert.Equal(before, CaptureState(e));
        Assert.False(e.ApplyDigit('x'));
        Assert.Equal(before, CaptureState(e));
    }

    [Fact]
    public void O1_NaturalOrder_DMY_LeapOk()
    {
        var e = New();
        Assert.True(TypeAllAccepted(e, "29022024"));
        Assert.True(e.TryGetDate(out _));
    }

    [Fact]
    public void O5_MonthYearDay_LeapKnown()
    {
        var e = New();
        e.SelectPart(DatePart.Month);
        Type(e, "02");
        e.SelectPart(DatePart.Year);
        Type(e, "2024");
        e.SelectPart(DatePart.Day);
        Assert.True(TypeAllAccepted(e, "29"));
        Assert.True(e.TryGetDate(out _));
    }

    [Fact]
    public void O6_YearDayMonth_Day31ConstrainsMonth()
    {
        var e = New();
        e.SelectPart(DatePart.Year);
        Type(e, "2024");
        e.SelectPart(DatePart.Day);
        Type(e, "31");
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('4'));
        Assert.True(e.ApplyDigit('1'));
        Assert.Equal("31/01/2024", e.FormatDisplay());
    }

    [Fact]
    public void E1_SelectPartDay_IllegalCompletingDigit_Rejected()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 4, 15));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDigit('3'));
        Assert.False(e.ApplyDigit('1'));
    }

    [Fact]
    public void E2_SelectPartMonth_IllegalGivenDay_Rejected()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 1, 31));
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        Assert.False(e.ApplyDigit('2'));
    }

    [Fact]
    public void E3_SelectPartYear_LeapToNonLeap_Rejected()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 2, 29));
        e.SelectPart(DatePart.Year);
        Assert.True(TypeAllAccepted(e, "202"));
        Assert.False(e.ApplyDigit('3'));
    }

    [Fact]
    public void P1_EmptyDay_Type9_Pads09()
    {
        var e = New();
        Assert.True(e.ApplyDigit('9'));
        Assert.Equal("09/00/0000", e.FormatDisplay());
        Assert.Equal(DatePart.Month, e.ActivePart);
    }

    [Fact]
    public void SelectPartThenApplyDelete_ClearsWholePart()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 1, 15));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDelete());
        Assert.False(e.TryGetDate(out _));
        Assert.Equal("00/01/2024", e.FormatDisplay()); // F2(a): whole day cleared on SelectPart+Delete
    }

    // --- Fix Round 1 ---

    [Fact]
    public void F1a_InteriorYearHole_CompletingToNonLeapUnder2902_Rejected()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 2, 29));
        // Reach Year with _replacePart still false (via AdvanceTo, not SelectPart) so the 3 deletes below stay
        // on the ordinary per-slot path -- SelectPart(Year) directly would now hit the P2 whole-part fast path
        // (Bug 2 fix, 2026-08-07: Year is no longer excluded from it), atomically clearing all 4 digits instead
        // of leaving the tail digit filled the way this test's interior-hole setup needs. Retyping Day/Month
        // with their own unchanged values just walks the caret to Year via the normal auto-advance, leaving
        // Year's "2024" untouched but freshly arrived at with _indexInPart=0/_replacePart=false.
        e.SelectPart(DatePart.Day);
        Assert.True(TypeAllAccepted(e, "29"));
        e.SelectPart(DatePart.Month);
        Assert.True(TypeAllAccepted(e, "02"));
        Assert.True(e.ApplyDelete());
        Assert.True(e.ApplyDelete());
        Assert.True(e.ApplyDelete());
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('3')); // would complete 2034
        Assert.Equal(before, CaptureState(e));
        Assert.False(e.TryGetDate(out _));
        AssertNoInvalidAllFilledDate(e);
    }

    [Fact]
    public void F1b_CompletingYear0000_ViaInteriorHole_Rejected_NoException()
    {
        var e = New();
        e.SetDate(new DateOnly(1000, 2, 1));
        // Same reasoning as F1a: reach Year via AdvanceTo (retyping Day/Month unchanged), not SelectPart, so
        // this single Delete stays on the per-slot path instead of the P2 whole-part fast path now covering
        // Year too (Bug 2 fix) -- the test needs exactly one digit cleared (an interior hole), not all four.
        e.SelectPart(DatePart.Day);
        Assert.True(TypeAllAccepted(e, "01"));
        e.SelectPart(DatePart.Month);
        Assert.True(TypeAllAccepted(e, "02"));
        Assert.True(e.ApplyDelete());
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('0')); // would complete 0000
        Assert.Equal(before, CaptureState(e));
        Assert.False(e.TryGetDate(out _));

        e.SelectPart(DatePart.Month);
        var ex = Record.Exception(() => e.ApplyDigit('3'));
        Assert.Null(ex);
    }

    [Fact]
    public void F2a_SelectPartThenDelete_Day_ThenDigitAccepted()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 8, 15));
        e.SelectPart(DatePart.Day);
        Assert.True(e.ApplyDelete());
        Assert.Equal("00/08/2024", e.FormatDisplay());
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('9'));
        Assert.Equal("09/08/2024", e.FormatDisplay());
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(2024, 8, 9), d);
    }

    [Fact]
    public void F2a_SelectPartThenDelete_Month_ThenDigitAccepted()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 8, 15));
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDelete());
        Assert.Equal("15/00/2024", e.FormatDisplay());
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('1'));
        Assert.Equal("15/01/2024", e.FormatDisplay());
    }


    // Bug 2 fix (the UI review + requester F5, 2026-08-07): Year now shares the exact whole-part fast
    // path Day/Month already had -- SelectPart on a fully-filled Year + one ApplyDelete clears all 4 slots in
    // a single call, matching SelectPartThenApplyDelete_ClearsWholePart/F2a_* above instead of leaving 3
    // digits behind for per-slot deletes to mop up.
    [Fact]
    public void F2a_SelectPartThenDelete_Year_ClearsWholePart()
    {
        var e = New();
        e.SetDate(new DateOnly(2024, 8, 15));
        e.SelectPart(DatePart.Year);
        Assert.True(e.ApplyDelete());
        Assert.Equal("15/08/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('5'));
        Assert.Equal("15/08/2025", e.FormatDisplay());
        Assert.True(e.TryGetDate(out var d));
        Assert.Equal(new DateOnly(2025, 8, 15), d);
    }


    // Fix round 3 (the UI review + requester, 2026-08-07): the whole-part clear no longer requires the
    // selected part to be FULLY filled -- SelectPart on a PARTIALLY filled part (here: Year with only 2 of 4
    // digits typed) must still clear the entire part in one Delete, not fall through to a per-slot removal of
    // just one digit. This is also what makes Backspace-right-after-typing-completes-and-auto-advances behave
    // correctly on a not-yet-fully-typed next segment (AstDateBoxTests covers that adapter-level scenario).
    [Fact]
    public void SelectPartThenApplyDelete_PartiallyFilledYear_StillClearsWholePart()
    {
        var e = New();
        e.SelectPart(DatePart.Year);
        Assert.True(e.ApplyDigit('1'));
        Assert.True(e.ApplyDigit('9')); // Year now "19__" -- 2 of 4 slots filled, ActivePart still Year
        e.SelectPart(DatePart.Year); // whole-select the still-partially-filled part again

        Assert.True(e.ApplyDelete());

        // "1","9" (not "2","0") is deliberate: against the OLD full-fill-only gate, the per-slot fallback
        // would clear only slot 4 ('1') and leave slot 5's '9' behind, rendering "00/00/0900" -- which is
        // NOT the same as this test's "00/00/0000" assertion, so this genuinely discriminates the fix. The
        // earlier "2","0" digits happened to leave an all-zero residue that FormatDisplay renders identically
        // to a fully-cleared part either way (a tautological-coverage-test shape, the UI review round 4).
        Assert.Equal("00/00/0000", e.FormatDisplay());
        Assert.False(e.TryGetDate(out _));

        // Follow-on typing must land as a clean new year (indexInPart/replacePart correctly reset by the
        // whole clear), not merged with any residual digit from before.
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('0'));
        Assert.True(e.ApplyDigit('2'));
        Assert.True(e.ApplyDigit('5'));
        Assert.Equal("00/00/2025", e.FormatDisplay());
    }

    [Fact]
    public void F4_Day00_AndMonth00_Rejected()
    {
        var e = New();
        Assert.True(e.ApplyDigit('0'));
        var before = CaptureState(e);
        Assert.False(e.ApplyDigit('0'));
        Assert.Equal(before, CaptureState(e));

        e = New();
        e.SelectPart(DatePart.Month);
        Assert.True(e.ApplyDigit('0'));
        before = CaptureState(e);
        Assert.False(e.ApplyDigit('0'));
        Assert.Equal(before, CaptureState(e));
    }

    // Pins IndexInPart (the caret-position source of truth adapters use, AstDateBox.cs) directly at the
    // engine, independent of the WPF adapter test harness this project's own decision-log (row 111) says
    // cannot be trusted alone for this control. 2026-08-07: the Year-completion branch used to clamp
    // IndexInPart to 3 instead of letting it reach 4 (PartLen) -- caret-past-the-end -- which stalled the
    // caret one position short of the end of text on the LAST digit typed into Year.
    [Fact]
    public void IndexInPart_advances_one_slot_per_digit_and_reaches_PartLen_on_the_completing_Year_digit()
    {
        var e = New();
        e.SelectPart(DatePart.Year);

        e.ApplyDigit('2');
        e.IndexInPart.Should().Be(1);
        e.ApplyDigit('0');
        e.IndexInPart.Should().Be(2);
        e.ApplyDigit('2');
        e.IndexInPart.Should().Be(3);
        e.ApplyDigit('6'); // completing digit -- Year never AdvanceTo()s to a following part
        e.IndexInPart.Should().Be(4);
        e.ActivePart.Should().Be(DatePart.Year);
    }

    [Fact]
    public void IndexInPart_resets_to_0_after_a_whole_part_replace_clear_via_SelectPart_then_Delete()
    {
        var e = New();
        Type(e, "23072026");
        e.SelectPart(DatePart.Year);

        e.ApplyDelete();

        e.IndexInPart.Should().Be(0);
    }

    [Fact]
    public void IndexInPart_points_at_the_cleared_slot_after_a_per_slot_Delete()
    {
        var e = New();
        Type(e, "23072026");
        e.ActivePart.Should().Be(DatePart.Year); // typing auto-advances Day->Month->Year, lands here filled

        e.SelectPart(DatePart.Year); // whole-select, then re-collapse to a plain per-slot cursor via Delete
        e.ApplyDelete(); // clears the whole part, IndexInPart -> 0 (previous test)
        e.ApplyDigit('2');
        e.ApplyDigit('0'); // Year now "20__", IndexInPart == 2, ActivePart still Year (not yet complete)

        e.ApplyDelete(); // per-slot forward scan from index 2 -- nothing filled there, falls through

        // Nothing right of the caret was filled (slots 2-3 of Year are empty) -- ApplyDelete must be a no-op,
        // leaving IndexInPart exactly where typing left it.
        e.IndexInPart.Should().Be(2);
    }

    [Fact]
    public void IndexInPart_points_at_the_cleared_slot_after_ApplyBackspace()
    {
        var e = New();
        Type(e, "23072026");

        e.ApplyBackspace(); // clears Year's last filled slot ('6' at index 3)

        e.IndexInPart.Should().Be(3);
    }
}
