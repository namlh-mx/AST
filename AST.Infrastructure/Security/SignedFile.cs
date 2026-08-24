using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

// Reads/writes 1 file with its .sig, fail-closed (spec §3/§4). Shared by File A & File B (DRY).
internal static class SignedFile
{
    public static ErrorOr<byte[]> Read(string file, string sigFile, IConfigSignature sig, bool requireSignature)
    {
        if (!File.Exists(file)) return ConfigErrors.NotDeclared(Path.GetFileName(file));
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        catch (IOException) { return ConfigErrors.IoError(Path.GetFileName(file)); }
        catch (UnauthorizedAccessException) { return ConfigErrors.IoError(Path.GetFileName(file)); }

        if (requireSignature)
        {
            if (!File.Exists(sigFile)) return ConfigErrors.SignatureInvalid(Path.GetFileName(file));
            byte[] sigBytes;
            try { sigBytes = Convert.FromBase64String(File.ReadAllText(sigFile).Trim()); }
            catch (IOException) { return ConfigErrors.IoError(Path.GetFileName(sigFile)); }
            catch (FormatException) { return ConfigErrors.SignatureInvalid(Path.GetFileName(file)); }
            if (!sig.Verify(data, sigBytes)) return ConfigErrors.SignatureInvalid(Path.GetFileName(file));
        }
        return data;
    }

    public static ErrorOr<Success> Write(string dir, string file, string sigFile, byte[] payload,
        IConfigSignature sig, bool requireSignature, byte[]? privateKey, string? passphrase)
    {
        byte[]? signature = null;
        if (requireSignature || privateKey is not null)
        {
            if (privateKey is null || passphrase is null) return ConfigErrors.KeyRequired();
            bool matches;
            try { matches = sig.KeyMatches(privateKey, passphrase); }         // wrong passphrase / corrupt key -> throws
            catch (System.Security.Cryptography.CryptographicException) { return ConfigErrors.KeyUnreadable(); }
            if (!matches) return ConfigErrors.KeyMismatch();                  // key opened fine but does not match the app
            signature = sig.Sign(payload, privateKey, passphrase);           // already imported OK at KeyMatches -> will not throw
        }

        try
        {
            Directory.CreateDirectory(dir);
            AtomicWrite(file, payload);
            if (signature is not null) AtomicWrite(sigFile, System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
            else if (File.Exists(sigFile)) File.Delete(sigFile); // A5: do not leave a stale orphaned .sig
        }
        catch (IOException) { return ConfigErrors.IoError(Path.GetFileName(file)); }
        catch (UnauthorizedAccessException) { return ConfigErrors.IoError(Path.GetFileName(file)); }
        return Result.Success;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true); // A2: write to temp then move (atomic) — readers never see a partially-written file
    }
}
