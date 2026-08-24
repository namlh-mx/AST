using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class ConfigPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ast-cfgpath-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Uses_parent_config_when_it_exists()
    {
        var appDir = Path.Combine(_root, "v1.2.3");
        Directory.CreateDirectory(appDir);
        var parentConfig = Path.Combine(_root, "config");
        Directory.CreateDirectory(parentConfig);   // deploy folder-per-version case

        var paths = ConfigPathResolver.Resolve(appDir);

        Assert.Equal(parentConfig, paths.Dir);
        Assert.Equal(Path.Combine(parentConfig, "dbconn.dat"), paths.ConnectionFile);
        Assert.Equal(Path.Combine(parentConfig, "dbconn.dat.sig"), paths.ConnectionSig);
        Assert.Equal(Path.Combine(parentConfig, "admins.json"), paths.AdminsFile);
        Assert.Equal(Path.Combine(parentConfig, "admins.json.sig"), paths.AdminsSig);
    }

    [Fact]
    public void Falls_back_to_baseDirectory_config_when_no_parent_config()
    {
        var appDir = Path.Combine(_root, "solo");
        Directory.CreateDirectory(appDir);

        var paths = ConfigPathResolver.Resolve(appDir);

        Assert.Equal(Path.Combine(appDir, "config"), paths.Dir);
    }

    [Fact]
    public void Resolve_places_audit_under_config_dir()
    {
        var appDir = Path.Combine(_root, "auditcase");
        Directory.CreateDirectory(appDir);

        var paths = ConfigPathResolver.Resolve(appDir);

        Assert.Equal(Path.Combine(paths.Dir, "audit"), paths.AuditDir);
        Assert.Equal(Path.Combine(paths.AuditDir, "config-audit.jsonl"), paths.AuditFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
