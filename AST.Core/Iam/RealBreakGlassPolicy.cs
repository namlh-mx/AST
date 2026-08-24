using AST.Core.Security;

namespace AST.Core.Iam;

// Real break-glass implementation (spec §6): matches the (normalized) Windows username against a verified File B. Store error -> false (fail-closed).
// [Note] DI registration is done at the composition root (Shell) in SLICE #2 (once Shell captures the Windows identity + has the config path).
public sealed class RealBreakGlassPolicy(IBreakGlassStore store) : IBreakGlassPolicy
{
    public bool IsBreakGlassAdmin(string username)
    {
        var target = WindowsUsernameNormalizer.Normalize(username);
        if (target is null) return false;

        var admins = store.Read();
        if (admins.IsError) return false; // missing (first-run) or tampered -> recognize no one

        foreach (var a in admins.Value)
            if (WindowsUsernameNormalizer.Normalize(a) == target) return true;
        return false;
    }
}
