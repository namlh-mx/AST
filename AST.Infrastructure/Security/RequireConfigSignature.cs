namespace AST.Infrastructure.Security;

// Build flag living OUTSIDE the signed file (spec §5): off in Debug (dev does not need a key), on in Release (prod).
public static class RequireConfigSignature
{
#if DEBUG
    public const bool Value = false;
#else
    public const bool Value = true;
#endif
}
