using System.Data;
using AST.Core.Data;
using MySqlConnector;

namespace AST.Infrastructure;

public sealed class MySqlConnectionFactory(IConnectionStringProvider connectionStringProvider) : IDbConnectionFactory
{
    public IDbConnection CreateConnection() => new MySqlConnection(connectionStringProvider.GetConnectionString());
}
