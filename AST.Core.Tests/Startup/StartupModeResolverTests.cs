using AST.Core.Startup;
using FluentAssertions;

namespace AST.Core.Tests.Startup;

public class StartupModeResolverTests
{
    [Fact]
    public void NotDeclared_is_NotConnected_with_declare_hint()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.NotDeclared, false, false, null);
        s.Mode.Should().Be(StartupMode.NotConnected);
        s.Reason.Should().Be("Config.NotDeclared");
        s.Message.Should().Be("Cấu hình kết nối cơ sở dữ liệu chưa được khai báo.");
    }

    [Fact]
    public void Corrupt_file_is_NotConnected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Corrupt, false, false, null);
        s.Reason.Should().Be("Config.Corrupt");
        s.Message.Should().Be("Tập tin thông số cấu hình kết nối cơ sở dữ liệu không toàn vẹn.");
    }

    [Fact]
    public void IoError_is_NotConnected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.IoError, false, false, null);
        s.Reason.Should().Be("Config.IoError");
        s.Message.Should().Be("Ứng dụng không thể đọc hoặc ghi tập tin cấu hình.");
    }

    [Fact]
    public void Ok_but_db_unreachable_is_NotConnected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, dbReachable: false, schemaMatch: false, null);
        s.Mode.Should().Be(StartupMode.NotConnected);
        s.Reason.Should().Be("Startup.DbUnreachable");
        s.Message.Should().Be("Ứng dụng không thể kết nối đến máy chủ.");
    }

    [Fact]
    public void Ok_reachable_schema_mismatch_carries_message()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, true, schemaMatch: false, "schema msg");
        s.Mode.Should().Be(StartupMode.NotConnected);
        s.Reason.Should().Be("Startup.SchemaMismatch");
        s.Message.Should().Be("schema msg");
    }

    [Fact]
    public void Ok_reachable_schema_mismatch_without_detail_uses_default_sentence()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, true, schemaMatch: false, null);
        s.Reason.Should().Be("Startup.SchemaMismatch");
        s.Message.Should().Be("Phiên bản của cơ sở dữ liệu không phù hợp.");
    }

    [Fact]
    public void Ok_reachable_schema_match_is_Connected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, true, true, "");
        s.Mode.Should().Be(StartupMode.Connected);
        s.Reason.Should().Be("Startup.Ready");
        s.Message.Should().Be("Ứng dụng kết nối cơ sở dữ liệu thành công.");
    }
}
