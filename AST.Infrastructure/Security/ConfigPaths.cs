namespace AST.Infrastructure.Security;

public sealed record ConfigPaths(
    string Dir, string ConnectionFile, string ConnectionSig, string AdminsFile, string AdminsSig,
    string AuditDir, string AuditFile);

// A1 (spec §4): config must live in a stable directory OUTSIDE the version folder, so an app update does not lose File A/B.
public static class ConfigPathResolver
{
    public static ConfigPaths Resolve(string baseDirectory)
    {
        var parent = Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parentConfig = parent is null ? null : Path.Combine(parent.FullName, "config");
        var dir = parentConfig is not null && Directory.Exists(parentConfig)
            ? parentConfig
            : Path.Combine(baseDirectory, "config");
        var auditDir = Path.Combine(dir, "audit");
        return new ConfigPaths(
            dir,
            Path.Combine(dir, "dbconn.dat"),
            Path.Combine(dir, "dbconn.dat.sig"),
            Path.Combine(dir, "admins.json"),
            Path.Combine(dir, "admins.json.sig"),
            auditDir,
            Path.Combine(auditDir, "config-audit.jsonl"));
    }
}
