namespace AST.Infrastructure.Security;

// Root admin public key, embedded in source. IT MUST REPLACE Value with the real key (from AST.ConfigKeyGen) BEFORE the Release build.
public static class RootPublicKey
{
    public const string Placeholder = "REPLACE_WITH_REAL_ROOT_PUBLIC_KEY_BEFORE_RELEASE";
    public const string Value = Placeholder;
    public static bool IsPlaceholder => Value == Placeholder;
}
