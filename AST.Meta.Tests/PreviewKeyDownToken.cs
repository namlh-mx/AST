namespace AST.Meta.Tests;

// One forbidden token. A match inside a comment or a reflection string still fails.
internal static class PreviewKeyDownToken
{
    public const string Token = "PreviewKeyDown";

    public static IReadOnlyList<string> Find(string relativePath, string source)
    {
        var hits = new List<string>();
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(Token, StringComparison.Ordinal))
                hits.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
        }

        return hits;
    }
}
