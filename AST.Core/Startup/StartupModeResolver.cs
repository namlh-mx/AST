namespace AST.Core.Startup;

// PURE function: (File A result + DB connection + schema) -> mode + message. No I/O (spec §2.1).
public static class StartupModeResolver
{
    public static StartupStatus Resolve(FileAOutcome fileA, bool dbReachable, bool schemaMatch, string? schemaMessage)
    {
        switch (fileA)
        {
            case FileAOutcome.NotDeclared:
                return new(StartupMode.NotConnected, "Config.NotDeclared",
                    "Chưa khai báo kết nối database. Vào menu Khai báo để nhập thông tin kết nối.");
            case FileAOutcome.Corrupt:
                return new(StartupMode.NotConnected, "Config.Corrupt",
                    "Tệp cấu hình kết nối database bị hỏng hoặc đã bị sửa. Cần khai báo lại.");
            case FileAOutcome.IoError:
                return new(StartupMode.NotConnected, "Config.IoError",
                    "Không đọc được tệp cấu hình kết nối database (kiểm tra mạng hoặc quyền truy cập).");
        }

        if (!dbReachable)
            return new(StartupMode.NotConnected, StartupCodes.DbUnreachable,
                "Không kết nối được máy chủ database. Kiểm tra lại thông tin kết nối hoặc trạng thái máy chủ.");
        if (!schemaMatch)
            return new(StartupMode.NotConnected, StartupCodes.SchemaMismatch,
                string.IsNullOrEmpty(schemaMessage)
                    ? "Phiên bản schema của database không khớp phiên bản ứng dụng cần."
                    : schemaMessage);

        return new(StartupMode.Connected, StartupCodes.Ready, "Đã kết nối database.");
    }
}
