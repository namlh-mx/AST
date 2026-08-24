using System.Text.Json;
using AST.Core.Iam;
using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class FileBreakGlassStore(IConfigSignature signature, ConfigPaths paths, bool requireSignature)
    : IBreakGlassStore
{
    private sealed record Dto(int V, string[] Admins);

    public ErrorOr<IReadOnlyList<string>> Read()
    {
        var raw = SignedFile.Read(paths.AdminsFile, paths.AdminsSig, signature, requireSignature);
        if (raw.IsError) return raw.Errors;

        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(raw.Value, ConfigJson.Options); }
        catch (JsonException) { return ConfigErrors.ContentInvalid("Danh sách admin gốc"); }
        if (dto?.Admins is null || dto.V != 1) return ConfigErrors.ContentInvalid("Danh sách admin gốc");

        return dto.Admins.ToList();
    }

    public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dto(1, admins.ToArray()), ConfigJson.Options);
        return SignedFile.Write(paths.Dir, paths.AdminsFile, paths.AdminsSig, json,
            signature, requireSignature, privateKey, passphrase);
    }
}
