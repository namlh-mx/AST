using System.Reflection;
using System.Text.RegularExpressions;
using AST.Core.EffectivePeriod;
using FluentAssertions;

namespace AST.Shell.Tests;

internal readonly record struct CompletenessEntry(string Message, bool AnsweredByCatchAll = false);

internal static class FormatMapCompletenessTestSupport
{
    internal static string SliceViewModelMapSource(
        string viewModelRelativePath,
        string startMarker,
        string endMarker)
    {
        var src = File.ReadAllText(
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                viewModelRelativePath)));
        var start = src.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"missing start marker '{startMarker}'");
        var end = src.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"missing end marker '{endMarker}'");
        return src[start..end];
    }

    internal static bool RegionHasSwitchLabel(string region, string code)
    {
        var withoutComments = StripComments(region);

        if (Regex.IsMatch(
                withoutComments,
                $@"(?m)(?:^|\s|or\s)""{Regex.Escape(code)}""\s*(?:=>|\bor\b)"))
        {
            return true;
        }

        foreach (var field in typeof(VersionCloseRules.Codes).GetFields(
                     BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) as string != code)
            {
                continue;
            }

            if (Regex.IsMatch(
                    withoutComments,
                    $@"(?m)(?:^|\s|or\s)VersionCloseRules\.Codes\.{field.Name}\s*(?:=>|\bor\b)"))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripComments(string region)
    {
        var withoutLineComments = Regex.Replace(region, @"//.*$", string.Empty, RegexOptions.Multiline);
        return Regex.Replace(withoutLineComments, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    internal static void AssertCompletenessDictionaryArmsInRegion(
        string region,
        IReadOnlyDictionary<string, string> completenessDictionary,
        string because) =>
        AssertCompletenessDictionaryArmsInRegion(
            region,
            completenessDictionary.ToDictionary(
                kv => kv.Key,
                kv => new CompletenessEntry(kv.Value)),
            because);

    internal static void AssertCompletenessDictionaryArmsInRegion(
        string region,
        IReadOnlyDictionary<string, CompletenessEntry> completenessDictionary,
        string because)
    {
        region.Should().Contain("StartsWith(\"Authz.\"", because);

        var missing = new List<string>();
        foreach (var (code, entry) in completenessDictionary)
        {
            if (entry.AnsweredByCatchAll)
            {
                continue;
            }

            if (code.StartsWith("Authz.", StringComparison.Ordinal)
                && code is not "Authz.NotGranted"
                && code is not "Authz.ScopeInsufficient")
            {
                continue;
            }

            if (!RegionHasSwitchLabel(region, code))
            {
                missing.Add(code);
            }
        }

        missing.Should().BeEmpty(
            $"{because}: every completeness-dictionary code must have a live switch label in the sliced region "
            + "(Authz.* prefix-arm codes and catch-all-by-design codes excepted):\n  "
            + string.Join("\n  ", missing));
    }
}
