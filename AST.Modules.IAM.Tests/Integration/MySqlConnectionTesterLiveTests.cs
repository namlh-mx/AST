using AST.Core.Data;
using AST.Infrastructure.Security;
using AST.Modules.IAM.Tests.TestSupport;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

[Collection(AstTestDatabaseCollection.Name)]
public class MySqlConnectionTesterLiveTests
{
    [Fact]
    public void Test_succeeds_against_real_database()
    {
        var cs = TestDatabase.TryGetConnectionString();
        TestDatabase.SkipUnlessAvailable(cs is not null);

        var b = new MySqlConnectionStringBuilder(cs);
        var fields = new ConnectionFields(b.Server, (int)b.Port, b.Database, b.UserID, b.Password);

        var result = new MySqlConnectionTester().Test(fields);

        Assert.False(result.IsError);
    }

    [Fact]
    public void Test_returns_AccessDenied_when_password_wrong()
    {
        var cs = TestDatabase.TryGetConnectionString();
        TestDatabase.SkipUnlessAvailable(cs is not null);

        var b = new MySqlConnectionStringBuilder(cs);
        var fields = new ConnectionFields(b.Server, (int)b.Port, b.Database, b.UserID, "wrong-password-definitely-not-it");

        var result = new MySqlConnectionTester().Test(fields);

        Assert.True(result.IsError);
        Assert.Equal("Startup.DbAccessDenied", result.FirstError.Code);
    }
}
