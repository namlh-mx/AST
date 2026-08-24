using AST.Core.Data;
using ErrorOr;
using MySqlConnector;
using Serilog;

namespace AST.Infrastructure.Security;

public sealed class MySqlConnectionTester : IConnectionTester
{
    public ErrorOr<Success> Test(ConnectionFields fields)
    {
        var connectionString = MySqlConnectionStringFactory.Build(fields, pooling: false);

        try
        {
            using var connection = new MySqlConnection(connectionString);
            connection.Open();
            return Result.Success;
        }
        catch (MySqlException ex) when (ex.Number is 1045 or 1044)
        {
            Log.Error(ex, "DB connection test rejected credentials (MySQL {Number}) for host {Host}", ex.Number, fields.Host);
            return Error.Validation("Startup.DbAccessDenied",
                "Sai tài khoản hoặc mật khẩu database.");
        }
        catch (MySqlException ex)
        {
            Log.Error(ex, "DB connection test failed to reach the server for host {Host}", fields.Host);
            return Error.Failure("Startup.DbConnectFailed",
                "Không kết nối được máy chủ database.");
        }
    }
}
