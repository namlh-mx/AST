using AST.Core.Data;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class FileAConnectionStringProviderTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-csp-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;
    private readonly FileConnectionConfigStore _store;
    private readonly byte[] _priv;

    public FileAConnectionStringProviderTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        _priv = priv;
        _store = new FileConnectionConfigStore(new EcdsaConfigSignature(pub), new AesConfigProtector(), _paths, true);
    }

    [Fact]
    public void Builds_connection_string_from_saved_config()
    {
        _store.Save(new ConnectionFields("db.local", 3306, "ast_db", "ast_app", "p@ss"), _priv, Pass);
        var cs = new FileAConnectionStringProvider(_store).GetConnectionString();
        Assert.Contains("db.local", cs);
        Assert.Contains("ast_db", cs);
        Assert.Contains("ast_app", cs);
    }

    [Fact]
    public void Throws_when_not_declared()
        => Assert.Throws<InvalidOperationException>(() => new FileAConnectionStringProvider(_store).GetConnectionString());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
