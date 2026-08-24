using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class FileBreakGlassStoreTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-bg-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;
    private readonly EcdsaConfigSignature _sig;
    private readonly byte[] _priv;

    public FileBreakGlassStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        _sig = new EcdsaConfigSignature(pub);
        _priv = priv;
    }

    private FileBreakGlassStore Store(bool requireSig) => new(_sig, _paths, requireSig);

    [Fact]
    public void Save_signed_then_Read_roundtrips()
    {
        var admins = new[] { "alice", "bob" };
        Assert.False(Store(true).Save(admins, _priv, Pass).IsError);
        var r = Store(true).Read();
        Assert.Equal(admins, r.Value);
    }

    [Fact]
    public void Read_missing_is_NotDeclared()
        => Assert.Equal(ErrorOr.ErrorType.NotFound, Store(true).Read().FirstError.Type);

    [Fact]
    public void Read_rejects_tampered_list()
    {
        Store(true).Save(new[] { "alice" }, _priv, Pass);
        File.WriteAllText(_paths.AdminsFile, "{\"v\":1,\"admins\":[\"attacker\"]}"); // tampers the content, the old sig no longer matches
        Assert.Equal("Config.SignatureInvalid", Store(true).Read().FirstError.Code);
    }

    [Fact]
    public void Empty_list_is_valid()
    {
        Assert.False(Store(true).Save(Array.Empty<string>(), _priv, Pass).IsError);
        Assert.Empty(Store(true).Read().Value);
    }

    [Fact]
    public void Persisted_json_uses_camelcase_keys_matching_spec()
    {
        Store(true).Save(new[] { "alice" }, _priv, Pass);
        var text = File.ReadAllText(_paths.AdminsFile);
        Assert.Contains("\"v\":1", text);         // spec §4: lowercase version flag
        Assert.Contains("\"admins\":", text);     // spec §4: camelCase key
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
