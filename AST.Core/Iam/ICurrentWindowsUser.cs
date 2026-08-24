namespace AST.Core.Iam;

// Windows identity of the current session (spec §2.1). Implementation captures WindowsIdentity in the Shell (net10.0-windows).
[SharedComponent]
public interface ICurrentWindowsUser
{
    string? Username { get; }
}
