using ErrorOr;

namespace AST.Core.Iam;

// File B: list of root admin usernames (signed). Read is fail-closed (spec §3). Empty is valid (§10 recommends keeping >=1).
public interface IBreakGlassStore
{
    ErrorOr<IReadOnlyList<string>> Read();
    ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase);
}
