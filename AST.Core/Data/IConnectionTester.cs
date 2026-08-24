using ErrorOr;

namespace AST.Core.Data;

// The "Test connection" button (spec §2.1): tries opening a database connection, does NOT save.
// Separated from the declaration service so the service can be tested headless.
[SharedComponent]
public interface IConnectionTester
{
    ErrorOr<Success> Test(ConnectionFields fields);
}
