using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

// A7 (spec §5): a Release build with a placeholder public key -> blocks startup, reports clearly (avoids bricking prod).
public static class ConfigSecurity
{
    public static ErrorOr<Success> EnsureKeyConfigured(bool requireSignature, bool isPlaceholder) =>
        requireSignature && isPlaceholder
            ? Error.Unexpected(ConfigErrors.Codes.PublicKeyNotConfigured,
                "Bản phát hành chưa cấu hình khóa công khai root admin.")
            : Result.Success;
}
