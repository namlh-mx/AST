using System.Data;

namespace AST.Core.Data;

// Abstracts the connection source for the data layer (D11) — AST.Core does not depend on a concrete ORM/driver.
// The base repository (§3.6) and every Repository in a module receive this factory via DI — instead of
// opening connections scattered around on their own. The concrete implementation (MySqlConnectionFactory) lives in AST.Infrastructure.
[SharedComponent]
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
