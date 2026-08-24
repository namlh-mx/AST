namespace AST.Core.Security;

// Normalizes the Windows identity for break-glass matching (spec §6): SAM "EXAMPLE\user" | UPN "user@example.local" | bare.
[SharedComponent]
public static class WindowsUsernameNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];        // SAM: after last backslash
        else
        {
            var at = s.IndexOf('@');
            if (at >= 0) s = s[..at];                // UPN: before '@'
        }
        s = s.Trim();
        return s.Length == 0 ? null : s.ToLowerInvariant();
    }
}
