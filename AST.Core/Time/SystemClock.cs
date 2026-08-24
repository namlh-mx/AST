namespace AST.Core.Time;

// Simple real implementation of IClock — the ONLY place allowed to read the system clock directly
// (hard invariant #6). Registered via DI at Slice B; not used in AST.Core's pure logic.
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
