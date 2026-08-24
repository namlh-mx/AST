using AST.Core.Data;
using AST.Infrastructure.Security;
using MySqlConnector;

namespace AST.Infrastructure.Tests.Security;

public class MySqlConnectionStringFactoryTests
{
    [Fact]
    public void Build_uses_default_port_when_zero()
    {
        var cs = MySqlConnectionStringFactory.Build(new ConnectionFields("db.local", 0, "ast", "user", "pw"));
        var parsed = new MySqlConnectionStringBuilder(cs);
        Assert.Equal(3306u, parsed.Port);
    }

    [Fact]
    public void Build_trims_host_database_user()
    {
        var cs = MySqlConnectionStringFactory.Build(new ConnectionFields(" db.local ", 3306, " ast ", " user ", "pw"));
        var parsed = new MySqlConnectionStringBuilder(cs);
        Assert.Equal("db.local", parsed.Server);
        Assert.Equal("ast", parsed.Database);
        Assert.Equal("user", parsed.UserID);
    }

    [Fact]
    public void Build_enables_AllowPublicKeyRetrieval_for_mysql8_compat()
    {
        var cs = MySqlConnectionStringFactory.Build(new ConnectionFields("db.local", 3306, "ast", "user", "pw"));
        var parsed = new MySqlConnectionStringBuilder(cs);
        Assert.True(parsed.AllowPublicKeyRetrieval);
    }

    [Fact]
    public void Build_pools_by_default()
    {
        var cs = MySqlConnectionStringFactory.Build(new ConnectionFields("db.local", 3306, "ast", "user", "pw"));
        Assert.True(new MySqlConnectionStringBuilder(cs).Pooling);
    }

    [Fact]
    public void Build_disables_pooling_when_requested()
    {
        var cs = MySqlConnectionStringFactory.Build(new ConnectionFields("db.local", 3306, "ast", "user", "pw"), pooling: false);
        Assert.False(new MySqlConnectionStringBuilder(cs).Pooling);
    }
}
