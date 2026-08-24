using AST.Core.Startup;

namespace AST.Core.Tests.Startup;

public class StartupModeResolverTests
{
    [Fact]
    public void NotDeclared_is_NotConnected_with_declare_hint()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.NotDeclared, false, false, null);
        Assert.Equal(StartupMode.NotConnected, s.Mode);
        Assert.Equal("Config.NotDeclared", s.Reason);
        Assert.Contains("Khai báo", s.Message);
    }

    [Fact]
    public void Corrupt_file_is_NotConnected()
        => Assert.Equal("Config.Corrupt",
            StartupModeResolver.Resolve(FileAOutcome.Corrupt, false, false, null).Reason);

    [Fact]
    public void IoError_is_NotConnected()
        => Assert.Equal("Config.IoError",
            StartupModeResolver.Resolve(FileAOutcome.IoError, false, false, null).Reason);

    [Fact]
    public void Ok_but_db_unreachable_is_NotConnected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, dbReachable: false, schemaMatch: false, null);
        Assert.Equal(StartupMode.NotConnected, s.Mode);
        Assert.Equal("Startup.DbUnreachable", s.Reason);
    }

    [Fact]
    public void Ok_reachable_schema_mismatch_carries_message()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, true, schemaMatch: false, "schema msg");
        Assert.Equal(StartupMode.NotConnected, s.Mode);
        Assert.Equal("Startup.SchemaMismatch", s.Reason);
        Assert.Equal("schema msg", s.Message);
    }

    [Fact]
    public void Ok_reachable_schema_match_is_Connected()
    {
        var s = StartupModeResolver.Resolve(FileAOutcome.Ok, true, true, "");
        Assert.Equal(StartupMode.Connected, s.Mode);
        Assert.Equal("Startup.Ready", s.Reason);
    }
}
