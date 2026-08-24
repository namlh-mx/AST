namespace AST.Core.EffectivePeriod;

// D7: a day gap = WARNING (not blocking) => carried in the payload, NOT an Error.
public readonly record struct GapWarning(DateOnly GapFrom, DateOnly GapTo);
