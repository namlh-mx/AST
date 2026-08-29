using System.Text.RegularExpressions;
using FluentAssertions;

namespace AST.Meta.Tests;

// Guards that a platform error code cannot skip becoming a named constant. The partition test over the three
// Codes.All lists is structurally blind to Error.Failure("Config.NewBare", …), so this source-text scan
// is the other half of completeness (shape borrowed from the directory scans in
// OrgUnitWritePathAbsenceTests and WritePathBusinessDateTests, not from their project lists).
//
// PREDICATE (falsifiable): a string literal matching "(Startup|Config|BreakGlass).[A-Za-z][A-Za-z0-9]*" may
// appear in the seven production projects only as the initializer of a const string declaration in one of the
// three declaring files — StartupCodes.cs, ConfigErrors.cs, BreakGlassAdminRules.cs — except the two named
// MenuGroupCodes declarations exempted below.
//
// TEST PROJECTS ARE OUT OF SCOPE: phantom platform codes live in test doubles on purpose; scanning *Tests
// would either false-positive or force those doubles to mirror production constants.
//
// MENU GROUP EXEMPTION (two declarations by name, not the file): MenuGroupCodes.ConfigSecurity and
// MenuGroupCodes.ConfigParams carry Config.*-shaped values that are menu group keys, not error codes — union
// membership must not be inferred from a dotted prefix alone.
//
// WHAT THIS GUARD DOES NOT CATCH — declared, so the claim is not read wider than the mechanism:
//   1. A code ASSEMBLED rather than written whole — a raise site that concatenates two string fragments,
//      or interpolates a name into a dotted prefix. The regex matches one quoted token, so neither form
//      is seen, and the partition test declares no new constant either — both guards stay green,
//      while a real platform code reaches the operator. This is the project's already-accepted standing
//      limit that static inspection cannot bound a dynamic mint site; it is recorded again here because
//      a reader of THIS file would otherwise infer the stronger claim.
//   2. A literal inside a /* block comment */. Only // line comments are stripped, so such a literal is
//      still scanned and REPORTED. ⚠️ That direction is deliberate and is the opposite of the precedent
//      at FormatMapCompletenessTestSupport, which strips block comments: that guard checks for the
//      PRESENCE of a switch arm, where an unstripped comment lets a deleted arm look alive — a false
//      NEGATIVE. This guard checks for the ABSENCE of a literal, where the same omission can only
//      produce a false POSITIVE, which fails loudly and is safe. Comment-stripping logic does not
//      transfer between guards by copying; it transfers by re-deriving the direction.
public sealed class PlatformCodeLiteralAbsenceTests
{
    [Fact]
    public void No_platform_error_code_literal_outside_a_declaring_const_initializer()
    {
        var root = MetaTest.RepoRoot();

        var offenders = ProductionDirectories
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !MetaTest.IsGenerated(root, path))
            .SelectMany(path => PlatformCodeLiteralAbsenceDetector.FindViolations(root, path))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        string.Join(", ", offenders).Should().BeEmpty(
            "a platform error code must be declared as a const string in StartupCodes.cs, ConfigErrors.cs or "
            + "BreakGlassAdminRules.cs before use — a bare literal bypasses the partition guard. Offenders are "
            + "reported as relativePath:line.");
    }

    // All seven production projects — do NOT inherit the precedent detectors' shorter lists (AST-CONSULT-209:
    // they omit AST.UI and AST.ConfigKeyGen).
    private static readonly string[] ProductionDirectories =
    [
        "AST",
        "AST.Core",
        "AST.Infrastructure",
        "AST.Modules.IAM",
        "AST.Shell",
        "AST.UI",
        "AST.ConfigKeyGen",
    ];
}

internal static class PlatformCodeLiteralAbsenceDetector
{
    private static readonly Regex PlatformLiteral = new(
        @"""(?:Startup|Config|BreakGlass)\.[A-Za-z][A-Za-z0-9]*""",
        RegexOptions.Compiled);

    private static readonly Regex DeclaringConstInitializer = new(
        @"\bconst\s+string\s+\w+\s*=\s*""(?:Startup|Config|BreakGlass)\.[A-Za-z][A-Za-z0-9]*""\s*;",
        RegexOptions.Compiled);

    // Only these two declarations — not the MenuGroupCodes file — because their values share the Config.* shape.
    private static readonly Regex MenuGroupKeyExemption = new(
        @"\bconst\s+string\s+(?:ConfigSecurity|ConfigParams)\s*=\s*""Config\.(?:Security|Params)""\s*;",
        RegexOptions.Compiled);

    private static readonly HashSet<string> DeclaringFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "AST.Core/Startup/StartupCodes.cs",
        "AST.Core/Security/ConfigErrors.cs",
        "AST.Core/Iam/BreakGlassAdminRules.cs",
    };

    private const string MenuGroupCodesFile = "AST.Core/Iam/MenuGroupCodes.cs";

    public static IEnumerable<string> FindViolations(string root, string absolutePath)
    {
        var relativePath = Path.GetRelativePath(root, absolutePath);
        var normalizedRelative = relativePath.Replace('\\', '/');
        var isDeclaringFile = DeclaringFiles.Contains(normalizedRelative);
        var isMenuGroupCodes = string.Equals(normalizedRelative, MenuGroupCodesFile, StringComparison.OrdinalIgnoreCase);

        var lines = File.ReadAllLines(absolutePath);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripLineComment(lines[i]);
            if (!PlatformLiteral.IsMatch(line))
            {
                continue;
            }

            if (isDeclaringFile && DeclaringConstInitializer.IsMatch(line))
            {
                continue;
            }

            if (isMenuGroupCodes && MenuGroupKeyExemption.IsMatch(line))
            {
                continue;
            }

            yield return $"{relativePath}:{i + 1}";
        }
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }
}
