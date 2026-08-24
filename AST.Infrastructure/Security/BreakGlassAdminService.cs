using System.Globalization;
using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class BreakGlassAdminService(
    IBreakGlassStore store,
    IConfigAuditLog audit,
    ConfigPaths paths,
    bool requireSignature) : IBreakGlassAdminService
{
    public ErrorOr<BreakGlassView> Load()
    {
        var read = store.Read();
        if (read.IsError)
        {
            var code = read.FirstError.Code;
            if (code == ConfigErrors.Codes.SignatureInvalid)
                return View(Array.Empty<string>(), BreakGlassHealth.Tampered);
            if (code == ConfigErrors.Codes.NotDeclared)
                return View(Array.Empty<string>(), BreakGlassHealth.Missing);
            if (code == ConfigErrors.Codes.ContentInvalid)
                return View(Array.Empty<string>(), BreakGlassHealth.Unreadable);
            return read.Errors;
        }

        var health = requireSignature ? BreakGlassHealth.Valid : BreakGlassHealth.UnsignedDebug;
        return View(read.Value, health);
    }

    public ErrorOr<Success> Save(IReadOnlyList<string> admins, byte[]? privateKey, string? passphrase)
    {
        var newList = BreakGlassAdminRules.NormalizeDistinct(admins);
        var validation = BreakGlassAdminRules.ValidateNonEmpty(newList);
        if (validation.IsError) return validation.Errors;

        var oldRead = store.Read();
        IReadOnlyList<string> old = oldRead.IsError ? Array.Empty<string>() : oldRead.Value;

        var save = store.Save(newList, privateKey, passphrase);
        if (save.IsError) return save.Errors;

        _ = audit.Append(
            new ConfigAuditEvent("FileB", "Update", BreakGlassAdminRules.Diff(old, newList), "Success", null),
            privateKey, passphrase);
        return save;
    }

    private BreakGlassView View(IReadOnlyList<string> names, BreakGlassHealth health)
    {
        var records = ReadAuditRecords();
        var createdMap = BuildCreatedMap(records);
        var admins = names
            .Select(u => new BreakGlassAdmin(u, createdMap.TryGetValue(u, out var t) ? t : (DateTime?)null))
            .ToList();
        return new BreakGlassView(admins, health, paths.AdminsFile, LastModifiedUtc(), LastSignerFingerprint(records));
    }

    private DateTime? LastModifiedUtc() =>
        File.Exists(paths.AdminsFile) ? File.GetLastWriteTimeUtc(paths.AdminsFile) : null;

    private IReadOnlyList<ConfigAuditRecord> ReadAuditRecords()
    {
        var records = audit.Read();
        return records.IsError ? Array.Empty<ConfigAuditRecord>() : records.Value;
    }

    // Created date per user = timestamp of the MOST RECENT audit record whose diff added that user
    // (a re-added user shows its latest add). Records are processed by ascending seq, so the last wins.
    private static Dictionary<string, DateTime> BuildCreatedMap(IReadOnlyList<ConfigAuditRecord> records)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var rec in records.OrderBy(r => r.Content.Seq))
        {
            var added = rec.Content.Diff?.Added;
            if (added is null) continue;
            if (!DateTimeOffset.TryParse(rec.Content.TsUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts)) continue;
            foreach (var u in added)
                map[u] = ts.UtcDateTime; // latest add wins
        }
        return map;
    }

    private static string? LastSignerFingerprint(IReadOnlyList<ConfigAuditRecord> records)
    {
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            if (r.Content.Target == "FileB" && r.TipSig is not null)
                return r.Content.KeyFingerprint;
        }
        return null;
    }
}
