using System.Security.Principal;
using AST.Core.Iam;
using AST.Core.Security;

namespace AST;

// Captures the current session's Windows identity once (spec §2.3). WindowsIdentity is only available on the -windows TFM.
// Normalize at the source (card 259): bare username, lowercased — same rule as RealBreakGlassPolicy /
// FileConfigAuditLog / AdminAuthViewModel / BreakGlassAdminViewModel / BreakGlassAdminRules.
public sealed class WindowsCurrentUser : ICurrentWindowsUser
{
    public string? Username { get; } =
        WindowsUsernameNormalizer.Normalize(WindowsIdentity.GetCurrent()?.Name);
}
