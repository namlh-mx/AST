using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace AST.Infrastructure;

// [Small B2 add-on seam] Dapper 2.1.79 does NOT natively recognize `DateOnly` as a parameter/result
// (SqlMapper.LookupDbType throws NotSupportedException when binding `DateOnly` via DynamicParameters) --
// discovered while running the B2 integration tests (period.From/To use DateOnly throughout VersionedRepository,
// per D4 "DAY granularity"). Registers the type handler ONCE when the AST.Infrastructure assembly is loaded
// ([ModuleInitializer], C# 9+) so it applies to EVERY Dapper query in the whole app — instead of each
// module having to register it again. This assembly loads before the first Dapper-DateOnly statement because every
// repository with an effective period inherits VersionedRepository (defined here) and the connection source is
// MySqlConnectionFactory (also here). Maps DateOnly <-> DbType.Date (the MySQL DATE column receives a DateTime).
internal static class DapperDateOnlyTypeHandler
{
#pragma warning disable CA2255 // "only for application code" -- this is a library but we INTENTIONALLY
    // need auto-registration on assembly load (everywhere that uses AST.Infrastructure needs this handler ready
    // before the first Dapper statement, there is no single common "Startup" point to call it manually).
    [ModuleInitializer]
    internal static void Register()
    {
        SqlMapper.AddTypeHandler(new Handler());
        SqlMapper.AddTypeHandler(typeof(DateOnly?), new NullableHandler());
    }
#pragma warning restore CA2255

    private sealed class Handler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d => d,
            _ => DateOnly.FromDateTime(Convert.ToDateTime(value)),
        };
    }

    private sealed class NullableHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is null ? DBNull.Value : value.Value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly? Parse(object value) =>
            value is null or DBNull ? null : DateOnly.FromDateTime(Convert.ToDateTime(value));
    }
}
