using ErrorOr;

namespace AST.Core.Data;

// File A: read (decrypt+verify) / write (encrypt+sign). Read is fail-closed, distinguishing NotFound/Failure/Validation (spec §3).
public interface IConnectionConfigStore
{
    ErrorOr<ConnectionFields> Read();
    ErrorOr<Success> Save(ConnectionFields fields, byte[]? privateKey, string? passphrase);
}
