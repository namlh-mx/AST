using ErrorOr;

namespace AST.Core.Security;

// Fail-closed config error catalog (spec §3). Caller branches by Type/Code:
// NotFound = not yet declared (first-run); Failure = transient IO; Validation = tampered/corrupt/wrong key.
[SharedComponent]
public static class ConfigErrors
{
    // Single home of every Config.* error code in the product. Seven of them have a factory below;
    // three are minted elsewhere and have no factory here because their message is built at the raise
    // site: Config.Corrupt (StartupModeResolver), Config.PublicKeyNotConfigured (ConfigSecurity) and
    // Config.CurrentUserUnknown (ConfigDeclarationService). All ten are named here so no site re-types
    // a literal.
    //
    // WHY A VALUE IS LOCKED -- narrowed 2026-08-28 after review measured the two
    // rationales this comment used to state, and found BOTH wider than the code. They are stated
    // exactly now, because an overbroad reason is what lets a real one get discounted later:
    //
    //   1. PERSISTENCE -- true of ONE code, and that is enough to lock the class. Only
    //      Config.SignatureInvalid is ever written as a non-null audit reason
    //      (StartupOrchestrator -> ConfigAuditEvent.Reason -> ConfigAuditContent.Reason -> the
    //      canonical hash and the stored line). The other three production ConfigAuditEvent
    //      constructions pass null. So changing THAT value reinterprets recorded history; changing
    //      another Config.* value does not, today. Oracle:
    //      FileConfigAuditLogTests.Appended_reason_code_survives_the_round_trip_to_the_stored_record.
    //   2. CONSUMER BRANCHING -- true of two production consumers, NOT of the screens. StartupOrchestrator
    //      and BreakGlassAdminService compare against these constants; the five AST.Shell error maps do
    //      not consume the Config.*/Startup.*/BreakGlass.* families at all today -- the platform
    //      ViewModels forward Description/Message. That may change when a shared describer ships;
    //      this note tells the next reader the claim was scoped, not guessed.
    //
    // Renaming a CONSTANT is free; changing a VALUE needs one of the two reasons above to be re-checked
    // against the code, not against this comment.
    //
    // All is a MANUALLY maintained list, mirroring VersionCloseRules.Codes: ConfigErrorsCodesTests
    // independently reflects over this class's public string fields and fails the moment an 11th
    // constant is declared here without also being added below. A generated list could not fail.
    public static class Codes
    {
        public const string NotDeclared = "Config.NotDeclared";
        public const string IoError = "Config.IoError";
        public const string SignatureInvalid = "Config.SignatureInvalid";
        public const string ContentInvalid = "Config.ContentInvalid";
        public const string KeyMismatch = "Config.KeyMismatch";
        public const string KeyUnreadable = "Config.KeyUnreadable";
        public const string KeyRequired = "Config.KeyRequired";
        public const string Corrupt = "Config.Corrupt";
        public const string PublicKeyNotConfigured = "Config.PublicKeyNotConfigured";
        public const string CurrentUserUnknown = "Config.CurrentUserUnknown";

        public static readonly IReadOnlyList<string> All =
        [
            NotDeclared,
            IoError,
            SignatureInvalid,
            ContentInvalid,
            KeyMismatch,
            KeyUnreadable,
            KeyRequired,
            Corrupt,
            PublicKeyNotConfigured,
            CurrentUserUnknown,
        ];
    }

    public static Error NotDeclared(string what) => Error.NotFound(Codes.NotDeclared, $"{what} chưa được khai báo.");
    public static Error IoError(string what) => Error.Failure(Codes.IoError, $"Không đọc/ghi được {what} (kiểm tra mạng/quyền).");
    public static Error SignatureInvalid(string what) => Error.Validation(Codes.SignatureInvalid, $"{what} sai/thiếu chữ ký — có thể đã bị sửa.");
    public static Error ContentInvalid(string what) => Error.Validation(Codes.ContentInvalid, $"{what} không đọc được nội dung.");
    public static Error KeyMismatch() => Error.Validation(Codes.KeyMismatch, "Khóa bí mật không khớp khóa công khai của app.");
    public static Error KeyUnreadable() => Error.Validation(Codes.KeyUnreadable, "Không mở được khóa bí mật (sai passphrase hoặc file khóa hỏng).");
    public static Error KeyRequired() => Error.Validation(Codes.KeyRequired, "Cần nạp khóa bí mật để ký cấu hình.");
}
