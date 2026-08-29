using AST.Core.Iam;
using AST.Core.Security;
using AST.Core.Startup;
using ErrorOr;

namespace AST.Shell.Presentation;

// The single home of the operator sentence for a platform error code. A screen may not author
// prose: it asks here. Sentences are requester-settled operator wording -- an agent may propose
// message content and may never decide it.
//
// Catalog answers the codes that can ARRIVE at an error.Description site (measured, 1.7f).
// NotDescribed names every other platform code with the reason it has no dedicated sentence, so
// a deliberate absence can never be read as a forgotten entry. It does NOT mean "travels only as
// StartupStatus" -- that is merely true of every member today; a code that reaches a screen and is
// deliberately answered by the catch-all belongs here too, with that as its reason.
//
// Two guards, neither redundant: PlatformErrorDescriberTests proves the two dictionaries partition
// the three Codes.All lists, and PlatformCodeLiteralAbsenceTests proves a code cannot skip becoming
// a constant -- which a partition over constants structurally cannot see.
public static class PlatformErrorDescriber
{
    // The 1.1 catch-all: returned for any code with no catalog entry, including one that does not
    // exist yet. Its home is 1.1 -- this is a copy at the point of use, not a second home.
    public const string CatchAll = "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.";

    public static IReadOnlyDictionary<string, string> Catalog { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StartupCodes.DbAccessDenied] = "Tài khoản hoặc mật khẩu không đúng.",
            [StartupCodes.DbConnectFailed] = "Ứng dụng không thể kết nối được cơ sở dữ liệu.",
            [ConfigErrors.Codes.SignatureInvalid] = "Tập tin cấu hình không toàn vẹn.",
            [ConfigErrors.Codes.ContentInvalid] = "Ứng dụng không thể đọc nội dung tập tin cấu hình.",
            [ConfigErrors.Codes.IoError] = "Ứng dụng không thể đọc hoặc ghi tập tin cấu hình.",
            [ConfigErrors.Codes.KeyMismatch] = "Khóa bí mật không đúng.",
            [ConfigErrors.Codes.KeyUnreadable] = "Ứng dụng không thể mở được khóa bí mật.",
            [ConfigErrors.Codes.KeyRequired] = "Người dùng chưa nạp khóa bí mật.",
            [ConfigErrors.Codes.CurrentUserUnknown] = "Hệ điều hành không có thông tin về danh tính người dùng.",
            [BreakGlassAdminRules.Codes.Empty] = "Danh sách người cứu hộ không thể trống.",
        };

    // Declared absence, not omission. Every member today is minted with its sentence at the source
    // and travels as StartupStatus.Message, never as an Error a screen describes -- so a catalog
    // row for one would assert an arrival that cannot happen.
    public static IReadOnlyDictionary<string, string> NotDescribed { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StartupCodes.Ready] = "startup band: minted with its sentence at StartupModeResolver",
            [StartupCodes.Pending] = "startup band: StartupState's default initializer",
            [StartupCodes.DbUnreachable] = "startup band: StartupModeResolver",
            [StartupCodes.SchemaMismatch] = "startup band: StartupModeResolver",
            [StartupCodes.Unexpected] = "startup band: StartupOrchestrator's catch arm",
            [ConfigErrors.Codes.NotDeclared] = "startup band: StartupModeResolver",
            [ConfigErrors.Codes.Corrupt] = "startup band: minted only there, via MapOutcome",
            [ConfigErrors.Codes.PublicKeyNotConfigured] = "startup band: StartupRunner's key guard",
        };

    public static string Describe(Error error) => Describe(error.Code);

    public static string Describe(string code) =>
        Catalog.TryGetValue(code, out var sentence) ? sentence : CatchAll;
}
