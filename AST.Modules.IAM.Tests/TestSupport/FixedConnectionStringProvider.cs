using AST.Core.Data;

namespace AST.Modules.IAM.Tests.TestSupport;

internal sealed class FixedConnectionStringProvider(string connectionString) : IConnectionStringProvider
{
    public string GetConnectionString() => connectionString;
}
