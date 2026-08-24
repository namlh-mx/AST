using System.Text.RegularExpressions;

namespace AST.Meta.Tests;

// Source-text detector: does this C# fragment declare its own captured IBusinessDateProvider (write-path
// clock trap)? Takes text only — AST.Meta.Tests has no ProjectReference to production assemblies.
internal static class WritePathClockDetector
{
    public static bool Detects(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var aliases = ExtractTypeAliases(source);
        var typePattern = BuildTypePattern(aliases);

        foreach (var rawLine in source.Split('\n'))
        {
            var line = StripLineComment(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsNamespaceUsing(line))
            {
                continue;
            }

            if (IsTypeAliasDeclaration(line))
            {
                continue;
            }

            if (IsParameterDeclarationLine(line, typePattern))
            {
                continue;
            }

            if (DeclaresCapturedProvider(line, typePattern))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Regex TypeAliasDeclaration = new(
        @"^\s*using\s+(\w+)\s*=\s*[^;]*\bIBusinessDateProvider\b",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceUsing = new(
        @"^\s*using\s+(?:global::)?[\w.]+;\s*$",
        RegexOptions.Compiled);

    // Standalone ctor-param line, or TYPE name inside a (... ) parameter list on the same line.
    private static readonly Regex ParameterLine = new(
        @"^\s*TYPE\s+\w+\s*[,)]\s*$|\([^)]*\bTYPE\s+\w+\s*[,)]",
        RegexOptions.Compiled);

    private static readonly Regex CapturedField = new(
        @"^\s*(?:(?:private|protected|internal|public|readonly|static|required)\s+)*TYPE\s+\w+\s*(?:=\s*[^;]+)?;\s*$",
        RegexOptions.Compiled);

    private static readonly Regex CapturedAutoProperty = new(
        @"^\s*(?:(?:private|protected|internal|public|readonly|static|required)\s+)*TYPE\s+\w+\s*\{",
        RegexOptions.Compiled);

    private static List<string> ExtractTypeAliases(string source)
    {
        var aliases = new List<string>();
        foreach (Match match in TypeAliasDeclaration.Matches(source))
        {
            aliases.Add(match.Groups[1].Value);
        }

        return aliases;
    }

    private static string BuildTypePattern(IReadOnlyList<string> aliases)
    {
        if (aliases.Count == 0)
        {
            return @"\bIBusinessDateProvider\b";
        }

        var parts = new List<string> { @"\bIBusinessDateProvider\b" };
        foreach (var alias in aliases)
        {
            parts.Add($@"\b{Regex.Escape(alias)}\b");
        }

        return $"(?:{string.Join("|", parts)})";
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool IsNamespaceUsing(string line) =>
        NamespaceUsing.IsMatch(line);

    private static bool IsTypeAliasDeclaration(string line) =>
        TypeAliasDeclaration.IsMatch(line);

    private static bool IsParameterDeclarationLine(string line, string typePattern)
    {
        var regex = ParameterLine.ToString().Replace("TYPE", typePattern, StringComparison.Ordinal);
        return Regex.IsMatch(line, regex);
    }

    private static bool DeclaresCapturedProvider(string line, string typePattern)
    {
        if (!Regex.IsMatch(line, typePattern))
        {
            return false;
        }

        var fieldPattern = CapturedField.ToString().Replace("TYPE", typePattern, StringComparison.Ordinal);
        if (Regex.IsMatch(line, fieldPattern))
        {
            return true;
        }

        var propertyPattern = CapturedAutoProperty.ToString().Replace("TYPE", typePattern, StringComparison.Ordinal);
        return Regex.IsMatch(line, propertyPattern);
    }
}
