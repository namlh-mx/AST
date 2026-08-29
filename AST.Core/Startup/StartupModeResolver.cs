using AST.Core.Security;

namespace AST.Core.Startup;

// PURE function: (File A result + DB connection + schema) -> mode + message. No I/O (spec §2.1).
public static class StartupModeResolver
{
    public static StartupStatus Resolve(FileAOutcome fileA, bool dbReachable, bool schemaMatch, string? schemaMessage)
    {
        switch (fileA)
        {
            case FileAOutcome.NotDeclared:
                return new(StartupMode.NotConnected, ConfigErrors.Codes.NotDeclared,
                    "Cấu hình kết nối cơ sở dữ liệu chưa được khai báo.");
            case FileAOutcome.Corrupt:
                return new(StartupMode.NotConnected, ConfigErrors.Codes.Corrupt,
                    "Tập tin thông số cấu hình kết nối cơ sở dữ liệu không toàn vẹn.");
            case FileAOutcome.IoError:
                return new(StartupMode.NotConnected, ConfigErrors.Codes.IoError,
                    "Ứng dụng không thể đọc hoặc ghi tập tin cấu hình.");
        }

        if (!dbReachable)
            return new(StartupMode.NotConnected, StartupCodes.DbUnreachable,
                "Ứng dụng không thể kết nối đến máy chủ.");
        if (!schemaMatch)
            return new(StartupMode.NotConnected, StartupCodes.SchemaMismatch,
                string.IsNullOrEmpty(schemaMessage)
                    ? "Phiên bản của cơ sở dữ liệu không phù hợp."
                    : schemaMessage);

        return new(StartupMode.Connected, StartupCodes.Ready, "Ứng dụng kết nối cơ sở dữ liệu thành công.");
    }
}
