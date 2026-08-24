namespace AST.Core.EffectivePeriod;

// Every version DTO implementing this marker => the shared engine can operate on it.
public interface IVersionRow
{
    long Id { get; }              // version id (used for FREEZE)
    long IdentityId { get; }      // <name>_id (IDENTITY)
    DateOnly EffectiveFrom { get; }
    DateOnly EffectiveTo { get; }
    bool IsActive { get; }
}
