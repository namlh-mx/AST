using System.Reflection;
using AST.Core.Startup;
using FluentAssertions;

namespace AST.Core.Tests.Startup;

// Pins the closed set of Startup.* codes (AST.Core/Startup/StartupCodes.cs). Mirrors
// VersionCloseRulesTests' two guards: one pins the wire VALUES, one proves All is exactly the
// declared constants. Neither can be satisfied by the class merely existing.
public class StartupCodesTests
{
    // Pin the actual wire string VALUES, not just the symbol. Every consumer maps these codes by
    // literal, so a value change is a breaking change that symbol-comparing tests cannot see.
    [Fact]
    public void Codes_PinTheActualWireStringValues()
    {
        StartupCodes.Pending.Should().Be("Startup.Pending");
        StartupCodes.Ready.Should().Be("Startup.Ready");
        StartupCodes.DbUnreachable.Should().Be("Startup.DbUnreachable");
        StartupCodes.SchemaMismatch.Should().Be("Startup.SchemaMismatch");
        StartupCodes.DbAccessDenied.Should().Be("Startup.DbAccessDenied");
        StartupCodes.DbConnectFailed.Should().Be("Startup.DbConnectFailed");
        StartupCodes.Unexpected.Should().Be("Startup.Unexpected");
    }

    // Reflects independently over StartupCodes' own public string fields and asserts All is exactly
    // that set — fails if an 8th constant is declared without being added to All, and fails if All
    // drifts to contain something no longer declared. The filter catches BOTH `const string` and
    // `static readonly string` shapes; only All itself (IReadOnlyList<string>) is excluded.
    [Fact]
    public void All_ContainsExactlyTheDeclaredConstants()
    {
        var declaredCodes = typeof(StartupCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(string) && (f.IsLiteral || f.IsInitOnly))
            .Select(f => (string)(f.IsLiteral ? f.GetRawConstantValue()! : f.GetValue(null)!))
            .ToArray();

        StartupCodes.All.Should().BeEquivalentTo(declaredCodes);
    }
}
