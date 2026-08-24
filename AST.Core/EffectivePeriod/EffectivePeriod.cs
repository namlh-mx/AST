namespace AST.Core.EffectivePeriod;

// Closed period [From, To]; open period => To = OpenEnd (D4).
[SharedComponent]
public readonly record struct EffectivePeriod(DateOnly From, DateOnly To)
{
    public static readonly DateOnly OpenEnd = new(9999, 12, 31);
    public bool IsOpen => To == OpenEnd;
    public bool Contains(DateOnly d) => From <= d && d <= To;
    public bool Overlaps(EffectivePeriod other) => From <= other.To && other.From <= To;

    // The 9999-12-31 boundary = "infinity": no overflow arithmetic at the boundary (§4).
    public static DateOnly? NextDay(DateOnly d) => d == OpenEnd ? null : d.AddDays(1);

    public static DateOnly? PreviousDay(DateOnly d) => d == DateOnly.MinValue ? null : d.AddDays(-1);

    // The uncovered day span between two adjacent periods — the left one ending at leftTo, the right one
    // starting at rightFrom — or null when they touch/overlap or a boundary is open (D7). One home for the
    // boundary handling, shared by both gap-warning callers (coverage reduction + period-edit neighbours).
    public static GapWarning? GapBetween(DateOnly leftTo, DateOnly rightFrom)
    {
        var gapStart = NextDay(leftTo);
        var gapEnd = PreviousDay(rightFrom);
        return gapStart is not null && gapEnd is not null && gapStart.Value <= gapEnd.Value
            ? new GapWarning(gapStart.Value, gapEnd.Value)
            : null;
    }
}
