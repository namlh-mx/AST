using System.Text.RegularExpressions;

namespace AST.Meta.Tests;

// META guard — a .NET custom date-format string treats '/' as the CURRENT thread culture's date-separator
// PLACEHOLDER, not a literal character. `$"{asOf:dd/MM/yyyy}"` therefore renders with whatever separator the
// running machine's culture uses (e.g. "10-08-2026" on a culture whose separator is '-'), even though the app's
// UI convention is a literal dd/MM/yyyy. This bit an operator-facing message (EffectivePeriodResolver.NoCoverage)
// 2026-08-10 — invisible in dev because the dev machine's culture separator happened to already be '/'. Every
// call site in this codebase that renders correctly instead passes CultureInfo.InvariantCulture explicitly
// (`x.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)`). This guard makes the interpolated-without-culture
// shape a build-red failure so the bug class cannot silently regress on a machine whose culture separator is '/'.
public class DateFormatCultureTests
{
    // The `{expr:dd/MM/yyyy}` interpolation-hole shape: a format specifier made only of date/time-format letters
    // (d/M/y/H/m/s) joined by literal '/' — which .NET reinterprets as the date-separator placeholder. A
    // `ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)` call (the correct shape) never has a ':' immediately
    // before the format text inside the braces, so it does not match.
    private static readonly Regex InterpolatedSlashDateFormat = new(
        @"\{[^{}]+:[dMyHms]+(?:/[dMyHms]+)+\}",
        RegexOptions.Compiled);

    // An explicit, commented opt-out on the SAME line — for the rare case a literal '/' format is genuinely
    // intended and culture-neutral by construction. Never weaken this guard globally for one exception.
    private const string OptOutMarker = "meta-allow: interpolated-date-format";

    [Fact]
    public void NoInterpolatedDateFormatUsesSlashWithoutExplicitCulture()
    {
        var root = MetaTest.RepoRoot();
        var violations = new List<string>();

        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcluded(root, cs))
            {
                continue;
            }

            var lines = File.ReadAllLines(cs);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!InterpolatedSlashDateFormat.IsMatch(line))
                {
                    continue;
                }

                if (line.Contains(OptOutMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(root, cs)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "An interpolated date format like $\"...{x:dd/MM/yyyy}...\" relies on the CURRENT thread culture's "
                + "date separator, NOT a literal '/' -- it will render with dashes (or another separator) on a "
                + "machine whose culture isn't '/'. Fix: replace it with "
                + "`x.ToString(\"dd/MM/yyyy\", CultureInfo.InvariantCulture)` (the shape every other correct call "
                + "site in this codebase already uses). If a literal '/' format is genuinely intended for a "
                + "non-date, culture-neutral use, add `// meta-allow: interpolated-date-format` on the SAME line "
                + "instead of weakening this guard:\n  " + string.Join("\n  ", violations));
    }

    // Test projects (any top-level "*.Tests" folder, including this guard's own project) are excluded — this
    // guard protects production/operator-facing code, and its own doc comments/message quote the offending shape
    // as text, which would otherwise false-positive itself.
    private static bool IsExcluded(string root, string path)
    {
        if (MetaTest.IsGenerated(root, path))
        {
            return true;
        }

        var rel = Path.GetRelativePath(root, path);
        var firstSegment = rel.Split(Path.DirectorySeparatorChar)[0];
        return firstSegment.EndsWith(".Tests", StringComparison.Ordinal);
    }
}
