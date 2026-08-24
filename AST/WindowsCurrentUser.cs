using System.Security.Principal;
using AST.Core.Iam;

namespace AST;

// Captures the current session's Windows identity once (spec §2.3). WindowsIdentity is only available on the -windows TFM.
public sealed class WindowsCurrentUser : ICurrentWindowsUser
{
    public string? Username { get; } = WindowsIdentity.GetCurrent()?.Name;
}
