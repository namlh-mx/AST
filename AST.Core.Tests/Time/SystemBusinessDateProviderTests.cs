using AST.Core.Time;

namespace AST.Core.Tests.Time;

public class SystemBusinessDateProviderTests
{
    private sealed class FixedClock(DateTime utc) : IClock
    {
        public DateTime UtcNow { get; } = utc;
    }

    [Fact]
    public void Today_is_local_date_of_clock_instant()
    {
        var clock = new FixedClock(new DateTime(2026, 7, 6, 20, 0, 0, DateTimeKind.Utc));
        var expected = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, TimeZoneInfo.Local));

        var sut = new SystemBusinessDateProvider(clock);

        Assert.Equal(expected, sut.Today);
    }

    [Fact]
    public void Today_differs_from_UtcNow_date_when_local_offset_shifts_the_calendar_day()
    {
        // Regression guard: proves Today does NOT use UtcNow.Date directly (i.e. a real timezone conversion happens).
        // Only meaningful when the test machine runs in a timezone other than UTC -- a UTC machine skips it to avoid a false failure.
        var referenceUtc = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
        var offset = TimeZoneInfo.Local.GetUtcOffset(referenceUtc);
        if (offset == TimeSpan.Zero)
        {
            return;
        }

        // Picks an instant near the UTC day boundary in the direction of the offset, to guarantee the local date
        // differs from the UTC date regardless of the offset's magnitude (positive offset -> pushes to the next day; negative -> pushes back to the previous day).
        var utc = offset > TimeSpan.Zero
            ? new DateTime(2026, 7, 6, 23, 59, 0, DateTimeKind.Utc)
            : new DateTime(2026, 7, 6, 0, 1, 0, DateTimeKind.Utc);

        var clock = new FixedClock(utc);
        var sut = new SystemBusinessDateProvider(clock);

        Assert.NotEqual(DateOnly.FromDateTime(utc), sut.Today);
    }
}
