using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

// A7 (spec §5): a Release build with a placeholder public key -> blocks startup, reports clearly (avoids bricking prod).
public static class ConfigSecurity
{
    public static ErrorOr<Success> EnsureKeyConfigured(bool requireSignature, bool isPlaceholder) =>
        requireSignature && isPlaceholder
            ? Error.Unexpected(ConfigErrors.Codes.PublicKeyNotConfigured,
                "Ứng dụng chưa khai báo khóa công khai để xác thực.")
            : Result.Success;
}
