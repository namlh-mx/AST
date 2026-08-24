using AST.Core.Data;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class MySqlConnectionTesterTests
{
    [Fact]
    public void Test_returns_error_when_host_unreachable()
    {
        // 127.0.0.1:1 -> connection refused quickly; the tester must RETURN an error, NOT throw.
        var fields = new ConnectionFields("127.0.0.1", 1, "ast", "u", "p");
        var result = new MySqlConnectionTester().Test(fields);
        Assert.True(result.IsError);
        Assert.Equal("Startup.DbConnectFailed", result.FirstError.Code);
    }

    [Fact]
    public void Test_returns_error_not_throw_for_unknown_hostname()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new MySqlConnectionTester().Test(new ConnectionFields("localhostk", 3306, "ast", "u", "p"));
        sw.Stop();
        Assert.True(result.IsError);
        Assert.Equal("Startup.DbConnectFailed", result.FirstError.Code);
        Assert.True(sw.ElapsedMilliseconds < 15_000, $"Took {sw.ElapsedMilliseconds}ms — likely blocking UI too long");
    }
}
