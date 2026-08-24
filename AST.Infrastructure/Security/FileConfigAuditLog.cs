using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Time;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class FileConfigAuditLog(
    IConfigSignature signature, ConfigPaths paths, ICurrentWindowsUser currentUser, IClock clock, string publicKeyBase64)
    : IConfigAuditLog
{
    // Line format is camelCase; reads are case-insensitive so a hand-inspected/rewritten file still parses.
    // The stored line's JSON shape does NOT feed the hash (that is ConfigAuditChain's own canonical bytes),
    // so this option cannot affect chain/tip verification.
    private static readonly JsonSerializerOptions LineJson =
        new(JsonSerializerDefaults.General) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase)
    {
        return WithLock<Success>(() =>
        {
            var existing = ReadRecords();
            if (existing.IsError) return existing.Errors;
            var records = existing.Value;

            var prevHash = records.Count == 0 ? ConfigAuditChain.GenesisPrevHash : records[^1].Hash;
            var user = WindowsUsernameNormalizer.Normalize(currentUser.Username) ?? "(unknown)";
            // KeyFingerprint and TipSig are set on the SAME condition (spec §3.3: both present only on key-signed records).
            var hasKey = privateKey is not null && passphrase is not null;
            var fingerprint = hasKey ? Fingerprint() : null;

            var content = new ConfigAuditContent(
                records.Count + 1,
                clock.UtcNow.ToString("O"),
                new ConfigAuditActor(user, Environment.MachineName),
                evt.Target, evt.Action, evt.Diff, evt.Result, evt.Reason, fingerprint, prevHash, evt.Snapshot);

            var hash = ConfigAuditChain.ComputeHash(content);
            string? tipSig = null;
            if (hasKey)
            {
                try { tipSig = Convert.ToBase64String(signature.Sign(Encoding.UTF8.GetBytes(hash), privateKey!, passphrase!)); }
                catch (CryptographicException) { return ConfigErrors.KeyUnreadable(); }
            }

            try
            {
                Directory.CreateDirectory(paths.AuditDir);
                var line = JsonSerializer.Serialize(new ConfigAuditRecord(content, hash, tipSig), LineJson);
                File.AppendAllText(paths.AuditFile, line + "\n");
            }
            catch (IOException) { return ConfigErrors.IoError("nhật ký cấu hình"); }
            catch (UnauthorizedAccessException) { return ConfigErrors.IoError("nhật ký cấu hình"); }
            return Result.Success;
        });
    }

    public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read() => WithLock(ReadRecords);

    public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity()
    {
        var read = Read();
        if (read.IsError) return read.Errors;
        var records = read.Value;
        var broken = ConfigAuditChain.FirstBrokenSeq(records);

        var lastSigned = records.LastOrDefault(r => r.TipSig is not null);
        var tipValid = lastSigned is null || VerifyTip(lastSigned);

        return new ConfigAuditIntegrity(broken is null, broken, tipValid);
    }

    // A malformed/garbage TipSig is a FAILED signature, not a crash (this runs on a possibly-tampered share file).
    private bool VerifyTip(ConfigAuditRecord record)
    {
        try { return signature.Verify(Encoding.UTF8.GetBytes(record.Hash), Convert.FromBase64String(record.TipSig!)); }
        catch (FormatException) { return false; }
    }

    private ErrorOr<IReadOnlyList<ConfigAuditRecord>> ReadRecords()
    {
        // Return a concrete (class) type: ErrorOr's user-defined implicit conversion is not applied from an interface-typed source.
        if (!File.Exists(paths.AuditFile)) return Array.Empty<ConfigAuditRecord>();
        try
        {
            var list = new List<ConfigAuditRecord>();
            foreach (var line in File.ReadAllLines(paths.AuditFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var r = JsonSerializer.Deserialize<ConfigAuditRecord>(line, LineJson);
                // Reject a JSON-parseable but semantically-incomplete line (missing content/hash) as a clear
                // ContentInvalid error, so the verify path never dereferences a null on a tampered file.
                if (r is null || r.Content is null || r.Hash is null) return ConfigErrors.ContentInvalid("nhật ký cấu hình");
                list.Add(r);
            }
            return list;
        }
        catch (IOException) { return ConfigErrors.IoError("nhật ký cấu hình"); }
        catch (JsonException) { return ConfigErrors.ContentInvalid("nhật ký cấu hình"); }
    }

    private string Fingerprint()
        => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKeyBase64)))[..16];

    // Cross-machine mutual exclusion for the read-tail -> append critical section (spec §3.5). Transient-IO retry.
    private ErrorOr<T> WithLock<T>(Func<ErrorOr<T>> body)
    {
        var lockPath = paths.AuditFile + ".lock";
        Directory.CreateDirectory(paths.AuditDir);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var _ = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return body();
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
            catch (IOException) { return ConfigErrors.IoError("nhật ký cấu hình"); }
            catch (UnauthorizedAccessException) { return ConfigErrors.IoError("nhật ký cấu hình"); }
        }
    }
}
