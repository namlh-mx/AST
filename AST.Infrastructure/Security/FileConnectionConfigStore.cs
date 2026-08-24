using System.Text.Json;
using AST.Core.Data;
using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class FileConnectionConfigStore(
    IConfigSignature signature, IConfigProtector protector, ConfigPaths paths, bool requireSignature)
    : IConnectionConfigStore
{
    private sealed record Dto(int V, string Host, int Port, string Database, string User, string Password);

    public ErrorOr<ConnectionFields> Read()
    {
        var raw = SignedFile.Read(paths.ConnectionFile, paths.ConnectionSig, signature, requireSignature);
        if (raw.IsError) return raw.Errors;

        byte[] json;
        try { json = protector.Unprotect(raw.Value); }
        catch (System.Security.Cryptography.CryptographicException) { return ConfigErrors.ContentInvalid("Cấu hình kết nối"); }

        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, ConfigJson.Options); }
        catch (JsonException) { return ConfigErrors.ContentInvalid("Cấu hình kết nối"); }
        if (dto is null || dto.V != 1 || string.IsNullOrWhiteSpace(dto.Host) || string.IsNullOrWhiteSpace(dto.Database)
            || string.IsNullOrWhiteSpace(dto.User))
            return ConfigErrors.ContentInvalid("Cấu hình kết nối");

        return new ConnectionFields(dto.Host, dto.Port, dto.Database, dto.User, dto.Password);
    }

    public ErrorOr<Success> Save(ConnectionFields fields, byte[]? privateKey, string? passphrase)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new Dto(1, fields.Host, fields.Port, fields.Database, fields.User, fields.Password), ConfigJson.Options);
        var cipher = protector.Protect(json);
        return SignedFile.Write(paths.Dir, paths.ConnectionFile, paths.ConnectionSig, cipher,
            signature, requireSignature, privateKey, passphrase);
    }
}
