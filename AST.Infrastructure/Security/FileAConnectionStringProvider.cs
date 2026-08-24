using AST.Core.Data;

namespace AST.Infrastructure.Security;

// Plugs IConnectionStringProvider from File A. Error -> throws (Shell slice #2 itself distinguishes first-run vs tampered via store.Read()).
public sealed class FileAConnectionStringProvider(IConnectionConfigStore store) : IConnectionStringProvider
{
    public string GetConnectionString()
    {
        var read = store.Read();
        if (read.IsError) throw new InvalidOperationException(read.FirstError.Description);
        return MySqlConnectionStringFactory.Build(read.Value);
    }
}
