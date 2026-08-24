using AST.Core.Iam;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class BreakGlassRecoveryTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-rec-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Tampered_fileB_recovered_by_resigning_with_key()
    {
        Directory.CreateDirectory(_dir);
        var paths = ConfigPathResolver.Resolve(_dir);
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        var sig = new EcdsaConfigSignature(pub);
        var store = new FileBreakGlassStore(sig, paths, requireSignature: true);
        var policy = new RealBreakGlassPolicy(store);

        store.Save(new[] { "alice" }, priv, Pass);
        Assert.True(policy.IsBreakGlassAdmin(@"EXAMPLE\alice"));

        // Tamper -> fail-closed (no longer recognizes the admin)
        File.WriteAllText(paths.AdminsFile, "{\"v\":1,\"admins\":[\"attacker\"]}");
        Assert.False(policy.IsBreakGlassAdmin(@"EXAMPLE\alice"));

        // B1 recovery path: root admin loads the key, re-signs -> the app recognizes the admin again
        Assert.False(store.Save(new[] { "alice" }, priv, Pass).IsError);
        Assert.True(policy.IsBreakGlassAdmin(@"EXAMPLE\alice"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
