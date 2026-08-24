using AST.Core.Data;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class FileConnectionConfigStoreTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-conn-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;
    private readonly EcdsaConfigSignature _sig;
    private readonly byte[] _priv;
    private static readonly ConnectionFields Sample = new("db.local", 3306, "ast_db", "ast_app", "p@ss");

    public FileConnectionConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        _sig = new EcdsaConfigSignature(pub);
        _priv = priv;
    }

    private FileConnectionConfigStore NewStore(bool requireSig)
        => new(_sig, new AesConfigProtector(), _paths, requireSig);

    [Fact]
    public void Save_signed_then_Read_roundtrips()
    {
        Assert.False(NewStore(true).Save(Sample, _priv, Pass).IsError);
        var r = NewStore(true).Read();
        Assert.False(r.IsError);
        Assert.Equal(Sample, r.Value);
    }

    [Fact]
    public void Read_missing_is_NotDeclared()
        => Assert.Equal(ErrorOr.ErrorType.NotFound, NewStore(true).Read().FirstError.Type);

    [Fact]
    public void Read_rejects_tampered_ciphertext()
    {
        NewStore(true).Save(Sample, _priv, Pass);
        var b = File.ReadAllBytes(_paths.ConnectionFile); b[^1] ^= 0xFF; File.WriteAllBytes(_paths.ConnectionFile, b);
        Assert.Equal("Config.SignatureInvalid", NewStore(true).Read().FirstError.Code);
    }

    [Fact]
    public void Unsigned_debug_mode_roundtrips()
    {
        Assert.False(NewStore(false).Save(Sample, null, null).IsError);
        Assert.Equal(Sample, NewStore(false).Read().Value);
    }

    [Fact]
    public void Read_rejects_garbage_content_as_ContentInvalid()
    {
        // Valid signature but the payload is not valid JSON after decryption -> ContentInvalid (not SignatureInvalid).
        var protector = new AesConfigProtector();
        var badCipher = protector.Protect("not-json"u8.ToArray());
        var sigBytes = _sig.Sign(badCipher, _priv, Pass);
        Directory.CreateDirectory(_paths.Dir);
        File.WriteAllBytes(_paths.ConnectionFile, badCipher);
        File.WriteAllText(_paths.ConnectionSig, Convert.ToBase64String(sigBytes));
        Assert.Equal("Config.ContentInvalid", NewStore(true).Read().FirstError.Code);
    }

    [Fact]
    public void Read_rejects_unsupported_version_as_ContentInvalid()
    {
        // Validly signed content but v != 1 -> ContentInvalid (forward-compat spec §4/A6).
        var protector = new AesConfigProtector();
        var cipher = protector.Protect(
            "{\"v\":2,\"host\":\"h\",\"port\":3306,\"database\":\"d\",\"user\":\"u\",\"password\":\"p\"}"u8.ToArray());
        var sigBytes = _sig.Sign(cipher, _priv, Pass);
        Directory.CreateDirectory(_paths.Dir);
        File.WriteAllBytes(_paths.ConnectionFile, cipher);
        File.WriteAllText(_paths.ConnectionSig, Convert.ToBase64String(sigBytes));
        Assert.Equal("Config.ContentInvalid", NewStore(true).Read().FirstError.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
