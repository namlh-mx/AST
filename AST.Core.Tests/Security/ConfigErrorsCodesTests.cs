using System.Reflection;
using AST.Core.Security;
using FluentAssertions;

namespace AST.Core.Tests.Security;

// Pins the closed set of Config.* codes (AST.Core/Security/ConfigErrors.cs). ConfigErrors is LOCKED
// in docs/shared-components.md because these values are written into the hash-chained config audit
// log: a changed VALUE reinterprets history. The first test is what makes that mechanical.
public class ConfigErrorsCodesTests
{
    [Fact]
    public void Codes_PinTheActualWireStringValues()
    {
        ConfigErrors.Codes.NotDeclared.Should().Be("Config.NotDeclared");
        ConfigErrors.Codes.IoError.Should().Be("Config.IoError");
        ConfigErrors.Codes.SignatureInvalid.Should().Be("Config.SignatureInvalid");
        ConfigErrors.Codes.ContentInvalid.Should().Be("Config.ContentInvalid");
        ConfigErrors.Codes.KeyMismatch.Should().Be("Config.KeyMismatch");
        ConfigErrors.Codes.KeyUnreadable.Should().Be("Config.KeyUnreadable");
        ConfigErrors.Codes.KeyRequired.Should().Be("Config.KeyRequired");
        ConfigErrors.Codes.Corrupt.Should().Be("Config.Corrupt");
        ConfigErrors.Codes.PublicKeyNotConfigured.Should().Be("Config.PublicKeyNotConfigured");
        ConfigErrors.Codes.CurrentUserUnknown.Should().Be("Config.CurrentUserUnknown");
    }

    // Reflects independently over Codes' own public string fields and asserts All is exactly that
    // set — fails if an 11th constant is declared without also being added to All, and fails if All
    // drifts to contain something Codes no longer declares. The filter catches BOTH `const string`
    // and `static readonly string`; only All itself (IReadOnlyList<string>) is excluded by FieldType.
    [Fact]
    public void Codes_All_ContainsExactlyTheDeclaredConstants()
    {
        var declaredCodes = typeof(ConfigErrors.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(string) && (f.IsLiteral || f.IsInitOnly))
            .Select(f => (string)(f.IsLiteral ? f.GetRawConstantValue()! : f.GetValue(null)!))
            .ToArray();

        ConfigErrors.Codes.All.Should().BeEquivalentTo(declaredCodes);
    }
}
