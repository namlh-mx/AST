namespace AST.Core.Time;

// First production implementation of IBusinessDateProvider. Business "today" (D5) = the LOCAL date
// derived from IClock.UtcNow via TimeZoneInfo.Local (the app is on-prem, single timezone; UTC would drift around midnight).
public sealed class SystemBusinessDateProvider(IClock clock) : IBusinessDateProvider
{
    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, TimeZoneInfo.Local));
}
