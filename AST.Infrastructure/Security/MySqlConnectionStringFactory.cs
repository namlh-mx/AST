using AST.Core;
using AST.Core.Data;
using MySqlConnector;

namespace AST.Infrastructure.Security;

// Single source of truth for turning ConnectionFields into a MySqlConnector connection string.
// Used by both the live connection tester and the runtime File A provider so they never diverge.
[SharedComponent]
internal static class MySqlConnectionStringFactory
{
    private const uint DefaultPort = 3306;

    public static string Build(ConnectionFields fields, bool pooling = true)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var port = fields.Port is > 0 and <= 65535 ? (uint)fields.Port : DefaultPort;

        return new MySqlConnectionStringBuilder
        {
            Server = fields.Host.Trim(),
            Port = port,
            Database = fields.Database.Trim(),
            UserID = fields.User.Trim(),
            Password = fields.Password,
            ConnectionTimeout = 5,
            // MySQL 8 caching_sha2_password over a non-TLS LAN link: required for the same setups DBeaver
            // enables by default (allowPublicKeyRetrieval). Harmless when the server uses mysql_native_password.
            AllowPublicKeyRetrieval = true,
            // A connection *test* must re-authenticate against the server every time, so the tester passes
            // pooling:false. The runtime provider keeps pooling (default true). Otherwise Open() reuses an
            // already-authenticated pooled connection and reports success even after the DB user was dropped.
            Pooling = pooling,
        }.ConnectionString;
    }
}
