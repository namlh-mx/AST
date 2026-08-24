using ErrorOr;

namespace AST.Core.Security;

// Fail-closed config error catalog (spec §3). Caller branches by Type/Code:
// NotFound = not yet declared (first-run); Failure = transient IO; Validation = tampered/corrupt/wrong key.
[SharedComponent]
public static class ConfigErrors
{
    // Single home of the codes THESE factories emit -- not of the whole `Config.*` namespace: Config.Corrupt
    // (StartupModeResolver), Config.PublicKeyNotConfigured and Config.CurrentUserUnknown are minted elsewhere.
    // Named rather than re-typed as literals at each call site because consumers BRANCH on them: a mismatch
    // compiles cleanly and silently sends a consumer down the wrong branch -- e.g. File B tamper detection
    // degrading to a generic error. Every factory below builds its Error from these, so the two cannot drift.
    //
    // Renaming a CONSTANT is free; changing a VALUE is not. StartupOrchestrator writes these codes into the
    // hash-chained config audit log, so a value is a persisted data contract -- changing it reinterprets history.
    public static class Codes
    {
        public const string NotDeclared = "Config.NotDeclared";
        public const string IoError = "Config.IoError";
        public const string SignatureInvalid = "Config.SignatureInvalid";
        public const string ContentInvalid = "Config.ContentInvalid";
        public const string KeyMismatch = "Config.KeyMismatch";
        public const string KeyUnreadable = "Config.KeyUnreadable";
        public const string KeyRequired = "Config.KeyRequired";
    }

    public static Error NotDeclared(string what) => Error.NotFound(Codes.NotDeclared, $"{what} chưa được khai báo.");
    public static Error IoError(string what) => Error.Failure(Codes.IoError, $"Không đọc/ghi được {what} (kiểm tra mạng/quyền).");
    public static Error SignatureInvalid(string what) => Error.Validation(Codes.SignatureInvalid, $"{what} sai/thiếu chữ ký — có thể đã bị sửa.");
    public static Error ContentInvalid(string what) => Error.Validation(Codes.ContentInvalid, $"{what} không đọc được nội dung.");
    public static Error KeyMismatch() => Error.Validation(Codes.KeyMismatch, "Khóa bí mật không khớp khóa công khai của app.");
    public static Error KeyUnreadable() => Error.Validation(Codes.KeyUnreadable, "Không mở được khóa bí mật (sai passphrase hoặc file khóa hỏng).");
    public static Error KeyRequired() => Error.Validation(Codes.KeyRequired, "Cần nạp khóa bí mật để ký cấu hình.");
}
