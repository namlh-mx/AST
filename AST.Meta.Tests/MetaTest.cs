namespace AST.Meta.Tests;

// Shared helpers for the meta-test guards — one home for repo-root discovery and the generated-file skip,
// reused by every *Tests guard in this project (rule-prefer-existing; extracted when the 2nd guard landed).
internal static class MetaTest
{
    // Repo root = the directory holding AST.slnx, found by walking up from the test binary's location
    // (bin/Debug/net10.0/...). Independent of the working directory so it holds under dotnet test and VS.
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AST.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Repo root (the directory containing AST.slnx) not found above {AppContext.BaseDirectory}.");
    }

    // True for build output (bin/obj) — never scan generated files as if they were source.
    public static bool IsGenerated(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
