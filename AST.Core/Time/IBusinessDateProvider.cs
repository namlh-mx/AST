namespace AST.Core.Time;

// Business "today" — captured ONCE per operation; used for PERMISSIONS & SCOPE (D5).
[SharedComponent]
public interface IBusinessDateProvider
{
    DateOnly Today { get; }
}
