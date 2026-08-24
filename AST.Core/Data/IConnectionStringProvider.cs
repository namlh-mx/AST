namespace AST.Core.Data;

// Abstracts the connection-string source -> MySqlConnectionFactory does not hardcode, does not lock
// the config-reading method (appsettings.json / secrets file / env var...) into SharedKernel.
[SharedComponent]
public interface IConnectionStringProvider
{
    string GetConnectionString();
}
