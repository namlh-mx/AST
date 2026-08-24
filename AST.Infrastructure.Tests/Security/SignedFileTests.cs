using AST.Core.Security;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class SignedFileTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-signed-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly string _sig;
    private readonly EcdsaConfigSignature _sut;
    private readonly byte[] _priv;

    public SignedFileTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "data.bin");
        _sig = _file + ".sig";
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        _sut = new EcdsaConfigSignature(pub);
        _priv = priv;
    }

    [Fact]
    public void Write_signed_then_Read_returns_payload()
    {
        var payload = "abc"u8.ToArray();
        var w = SignedFile.Write(_dir, _file, _sig, payload, _sut, requireSignature: true, _priv, Pass);
        Assert.False(w.IsError);

        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.False(r.IsError);
        Assert.Equal(payload, r.Value);
    }

    [Fact]
    public void Read_missing_file_is_NotDeclared()
    {
        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.True(r.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, r.FirstError.Type);
    }

    [Fact]
    public void Read_rejects_tampered_payload()
    {
        SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, true, _priv, Pass);
        var bytes = File.ReadAllBytes(_file); bytes[0] ^= 0xFF; File.WriteAllBytes(_file, bytes);

        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.Equal("Config.SignatureInvalid", r.FirstError.Code);
    }

    [Fact]
    public void Read_rejects_tampered_signature()
    {
        SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, true, _priv, Pass);
        File.WriteAllText(_sig, "bm90LWEtc2ln"); // "not-a-sig" base64

        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.Equal("Config.SignatureInvalid", r.FirstError.Code);
    }

    [Fact]
    public void Read_missing_sig_when_required_is_SignatureInvalid()
    {
        File.WriteAllBytes(_file, "abc"u8.ToArray()); // no .sig written
        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.Equal("Config.SignatureInvalid", r.FirstError.Code);
    }

    [Fact]
    public void Unsigned_write_and_read_when_signature_not_required()
    {
        var w = SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, requireSignature: false, privateKey: null, passphrase: null);
        Assert.False(w.IsError);
        Assert.False(File.Exists(_sig)); // no stale sig

        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: false);
        Assert.Equal("abc"u8.ToArray(), r.Value);
    }

    [Fact]
    public void Write_requires_key_when_signature_required()
    {
        var w = SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, requireSignature: true, privateKey: null, passphrase: null);
        Assert.Equal("Config.KeyRequired", w.FirstError.Code);
    }

    [Fact]
    public void Write_rejects_mismatched_key()
    {
        var (_, otherPriv) = EcdsaKeys.Generate(Pass);
        var w = SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, requireSignature: true, otherPriv, Pass);
        Assert.Equal("Config.KeyMismatch", w.FirstError.Code);
    }

    [Fact]
    public void Write_wrong_passphrase_is_KeyUnreadable()
    {
        var w = SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, requireSignature: true, _priv, "wrong-pass");
        Assert.Equal("Config.KeyUnreadable", w.FirstError.Code);
    }

    [Fact]
    public void Read_locked_file_is_IoError()
    {
        SignedFile.Write(_dir, _file, _sig, "abc"u8.ToArray(), _sut, true, _priv, Pass);
        using var _ = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = SignedFile.Read(_file, _sig, _sut, requireSignature: true);
        Assert.Equal(ErrorOr.ErrorType.Failure, r.FirstError.Type);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
