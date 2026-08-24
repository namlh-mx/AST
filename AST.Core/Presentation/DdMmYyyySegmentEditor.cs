namespace AST.Core.Presentation;

public enum DatePart
{
    Day,
    Month,
    Year
}

/// <summary>
/// Headless dd/MM/yyyy segment editor: pad + reject calendar-illegal completing digits.
/// Business/range rules (From≤To, Today) stay VM-owned. No System.Windows.
/// </summary>
public sealed class DdMmYyyySegmentEditor
{
    // Slots 0-1 day, 2-3 month, 4-7 year. Digit meaningful only when _filled[i].
    private readonly char[] _digits = ['0', '0', '0', '0', '0', '0', '0', '0'];
    private readonly bool[] _filled = new bool[8];

    private DatePart _activePart = DatePart.Day;
    private int _indexInPart;
    private bool _replacePart;

    public DatePart ActivePart => _activePart;

    // Authoritative next-edit slot within ActivePart (0-based). Adapters (AstDateBox) use this
    // directly for caret placement instead of diffing FormatDisplay() strings before/after an
    // edit -- a diff cannot distinguish "no visible change" from "changed to the same placeholder
    // digit '0'", which silently misplaced the caret (2026-08-07 F5 finding).
    public int IndexInPart => _indexInPart;

    public string FormatDisplay()
    {
        char D(int i) => _filled[i] ? _digits[i] : '0';
        return string.Create(10, 0, (span, _) =>
        {
            span[0] = D(0);
            span[1] = D(1);
            span[2] = '/';
            span[3] = D(2);
            span[4] = D(3);
            span[5] = '/';
            span[6] = D(4);
            span[7] = D(5);
            span[8] = D(6);
            span[9] = D(7);
        });
    }

    public bool TryGetDate(out DateOnly date)
    {
        date = default;
        if (!_filled.AsSpan().Contains(false))
        {
            int day = DigitValue(0, 1);
            int month = DigitValue(2, 3);
            int year = DigitValue(4, 7);
            if (year is >= 1 and <= 9999
                && month is >= 1 and <= 12
                && day >= 1
                && day <= DateTime.DaysInMonth(year, month))
            {
                date = new DateOnly(year, month, day);
                return true;
            }
        }

        return false;
    }

    public void SetDate(DateOnly? date)
    {
        ClearAll();
        _activePart = DatePart.Day;
        _indexInPart = 0;
        _replacePart = false;
        if (date is null)
            return;

        var d = date.Value;
        WriteTwo(0, d.Day);
        WriteTwo(2, d.Month);
        WriteFour(4, d.Year);
    }

    public bool ApplyDigit(char digit)
    {
        if (digit is < '0' or > '9')
            return false;

        var snap = Capture();
        if (!TryApplyDigitCore(digit))
        {
            Restore(snap);
            return false;
        }

        return true;
    }

    public bool ApplyBackspace()
    {
        if (ClearLastFilledInPart(_activePart))
        {
            _replacePart = false;
            return true;
        }

        if (_activePart == DatePart.Day)
            return false;

        _activePart = _activePart == DatePart.Year ? DatePart.Month : DatePart.Day;
        _replacePart = false;
        if (!ClearLastFilledInPart(_activePart))
        {
            _indexInPart = 0;
            return true; // moved focus even if previous part empty
        }

        return true;
    }

    public bool ApplyDelete()
    {
        // SelectPart marks whole-part selection: Delete on a selected part clears it entirely, regardless of
        // fill level (empty, partially, or fully filled all take this path -- ClearPart is safe on any of them).
        if (_replacePart)
        {
            ClearPart(_activePart);
            _replacePart = false;
            _indexInPart = 0;
            return true;
        }

        int start = PartStart(_activePart);
        int len = PartLen(_activePart);
        for (int i = start + _indexInPart; i < start + len; i++)
        {
            if (_filled[i])
            {
                _filled[i] = false;
                _digits[i] = '0';
                _indexInPart = i - start;
                _replacePart = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>Test-only fill/digit snapshot for reject bit-identity (not for AstDateBox/P2).</summary>
    internal FillSnapshot CaptureFillState() => new(_filled.ToArray(), _digits.ToArray());

    internal readonly record struct FillSnapshot(bool[] Filled, char[] Digits);

    public void SelectPart(DatePart part)
    {
        _activePart = part;
        _indexInPart = 0;
        _replacePart = true;
    }

    private bool TryApplyDigitCore(char digit)
    {
        if (_replacePart)
        {
            ClearPart(_activePart);
            _replacePart = false;
            _indexInPart = 0;
        }

        return _activePart switch
        {
            DatePart.Day => TryApplyDayDigit(digit),
            DatePart.Month => TryApplyMonthDigit(digit),
            DatePart.Year => TryApplyYearDigit(digit),
            _ => false
        };
    }

    private bool TryApplyDayDigit(char digit)
    {
        int dig = digit - '0';
        int filled = CountFilled(DatePart.Day);

        if (filled == 0)
        {
            if (dig is >= 4 and <= 9)
            {
                if (dig > MaxDay())
                    return false;
                WriteTwo(0, dig);
                AdvanceTo(DatePart.Month);
                return true;
            }

            if (dig == 3)
            {
                if (IsMonthFullyFilled() && MaxDay() < 30)
                    return false;
                SetSlot(0, digit);
                _indexInPart = 1;
                return true;
            }

            // 0–2: tens only
            SetSlot(0, digit);
            _indexInPart = 1;
            return true;
        }

        if (filled == 1 && _filled[0] && !_filled[1])
        {
            int candidate = (_digits[0] - '0') * 10 + dig;
            if (candidate < 1 || candidate > MaxDay())
                return false;
            SetSlot(1, digit);
            AdvanceTo(DatePart.Month);
            return true;
        }

        return false;
    }

    private bool TryApplyMonthDigit(char digit)
    {
        int dig = digit - '0';
        int filled = CountFilled(DatePart.Month);

        if (filled == 0)
        {
            if (dig is >= 2 and <= 9)
            {
                if (!CanAcceptMonth(dig))
                    return false;
                WriteTwo(2, dig);
                AdvanceTo(DatePart.Year);
                return true;
            }

            if (dig is 0 or 1)
            {
                SetSlot(2, digit);
                _indexInPart = 1;
                return true;
            }

            return false;
        }

        if (filled == 1 && _filled[2] && !_filled[3] && _digits[2] == '1')
        {
            if (dig is < 0 or > 2)
                return false;
            int month = 10 + dig;
            if (!CanAcceptMonth(month))
                return false;
            SetSlot(3, digit);
            AdvanceTo(DatePart.Year);
            return true;
        }

        // tens was '0' — second digit completes 01–09
        if (filled == 1 && _filled[2] && !_filled[3] && _digits[2] == '0')
        {
            if (dig == 0)
                return false; // 00 rejected
            if (!CanAcceptMonth(dig))
                return false;
            SetSlot(3, digit);
            AdvanceTo(DatePart.Year);
            return true;
        }

        return false;
    }

    private bool TryApplyYearDigit(char digit)
    {
        int start = PartStart(DatePart.Year);
        int next = -1;
        for (int i = 0; i < 4; i++)
        {
            if (!_filled[start + i])
            {
                next = i;
                break;
            }
        }

        if (next < 0)
            return false;

        // Tentatively set; validate whenever the year becomes fully filled (any completing hole),
        // not only when slot index 3 is the one written (F1 — interior Delete holes).
        // No clamp here (unlike the old code) -- the completing digit (next == 3) must advance
        // _indexInPart to 4 (PartLen), the caret-past-the-end position; Year never AdvanceTo()s to a
        // following part, so this is the only place that position is reachable. ApplyDelete's
        // per-slot loop (`start + _indexInPart` as its lower bound) is unaffected: at _indexInPart ==
        // PartLen the loop range is empty, correctly reporting "nothing right of the caret to delete."
        SetSlot(start + next, digit);
        _indexInPart = next + 1;

        if (!IsYearFullyFilled())
            return true;

        int year = DigitValue(4, 7);
        if (year is < 1 or > 9999)
            return false;

        if (IsDayFullyFilled() && IsMonthFullyFilled())
        {
            int day = DigitValue(0, 1);
            int month = DigitValue(2, 3);
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                return false;
        }

        return true;
    }

    private bool CanAcceptMonth(int month)
    {
        if (month is < 1 or > 12)
            return false;

        int max = MaxDayFor(month);
        if (IsDayFullyFilled())
        {
            int day = DigitValue(0, 1);
            return day >= 1 && day <= max;
        }

        // Day tens=3 only → completions are only 30/31.
        if (_filled[0] && !_filled[1] && _digits[0] == '3')
            return max >= 30;

        return true;
    }

    private int MaxDay() =>
        IsMonthFullyFilled() ? MaxDayFor(DigitValue(2, 3)) : 31;

    private int MaxDayFor(int month)
    {
        if (month is < 1 or > 12)
            return 31;

        if (month == 2)
        {
            if (!IsYearFullyFilled())
                return 29;
            int febYear = DigitValue(4, 7);
            // Defense: never call IsLeapYear with year 0 / out of DateOnly range (F1b).
            if (febYear is < 1 or > 9999)
                return 29;
            return DateTime.IsLeapYear(febYear) ? 29 : 28;
        }

        int year = 2024;
        if (IsYearFullyFilled())
        {
            int y = DigitValue(4, 7);
            if (y is >= 1 and <= 9999)
                year = y;
        }

        return DateTime.DaysInMonth(year, month);
    }

    private void AdvanceTo(DatePart part)
    {
        _activePart = part;
        _indexInPart = 0;
        _replacePart = false;
    }

    private bool IsDayFullyFilled() => _filled[0] && _filled[1];
    private bool IsMonthFullyFilled() => _filled[2] && _filled[3];
    private bool IsYearFullyFilled() => _filled[4] && _filled[5] && _filled[6] && _filled[7];

    private int CountFilled(DatePart part)
    {
        int start = PartStart(part);
        int len = PartLen(part);
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            if (_filled[start + i])
                n++;
        }

        return n;
    }

    private static int PartStart(DatePart part) => part switch
    {
        DatePart.Day => 0,
        DatePart.Month => 2,
        DatePart.Year => 4,
        _ => 0
    };

    private static int PartLen(DatePart part) => part == DatePart.Year ? 4 : 2;

    private int DigitValue(int from, int toInclusive)
    {
        int v = 0;
        for (int i = from; i <= toInclusive; i++)
            v = v * 10 + (_digits[i] - '0');
        return v;
    }

    private void SetSlot(int index, char digit)
    {
        _digits[index] = digit;
        _filled[index] = true;
    }

    private void WriteTwo(int start, int value)
    {
        SetSlot(start, (char)('0' + value / 10));
        SetSlot(start + 1, (char)('0' + value % 10));
    }

    private void WriteFour(int start, int value)
    {
        SetSlot(start, (char)('0' + (value / 1000) % 10));
        SetSlot(start + 1, (char)('0' + (value / 100) % 10));
        SetSlot(start + 2, (char)('0' + (value / 10) % 10));
        SetSlot(start + 3, (char)('0' + value % 10));
    }

    private void ClearPart(DatePart part)
    {
        int start = PartStart(part);
        int len = PartLen(part);
        for (int i = 0; i < len; i++)
        {
            _filled[start + i] = false;
            _digits[start + i] = '0';
        }
    }

    private void ClearAll()
    {
        for (int i = 0; i < 8; i++)
        {
            _filled[i] = false;
            _digits[i] = '0';
        }
    }

    private bool ClearLastFilledInPart(DatePart part)
    {
        int start = PartStart(part);
        int len = PartLen(part);
        for (int i = start + len - 1; i >= start; i--)
        {
            if (_filled[i])
            {
                _filled[i] = false;
                _digits[i] = '0';
                _indexInPart = i - start;
                return true;
            }
        }

        return false;
    }

    private Snapshot Capture() => new(
        _digits.ToArray(),
        _filled.ToArray(),
        _activePart,
        _indexInPart,
        _replacePart);

    private void Restore(Snapshot s)
    {
        Array.Copy(s.Digits, _digits, 8);
        Array.Copy(s.Filled, _filled, 8);
        _activePart = s.ActivePart;
        _indexInPart = s.IndexInPart;
        _replacePart = s.ReplacePart;
    }

    private readonly record struct Snapshot(
        char[] Digits,
        bool[] Filled,
        DatePart ActivePart,
        int IndexInPart,
        bool ReplacePart);
}
