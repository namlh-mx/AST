using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace AST.AppScope.Tests;

// The one mechanical guard the pump-free host rests on. MEASURED 2026-08-20: with a message pump
// running, WPF's Application CONSTRUCTOR callback raises Startup -> PrismApplicationBase.OnStartup ->
// Initialize() -> DirectoryModuleCatalog.InnerLoad(), which throws because a test output has no
// Modules/ folder, reaches App's DispatcherUnhandledException net, and Shutdown(1)s the host. Every
// run. Application.Run() is not required and was never called.
//
// So "this host never pumps" is a correctness constraint, and a comment cannot hold it. This scan can
// -- for the cases named in its own failure message, and no further.
public class PumpFreeHostTests
{
    // Every API that starts or re-enters a WPF message loop. RunAsync is Wpf.Ui's; ShowDialog pumps a
    // nested loop of its own.
    private static readonly string[] PumpApis =
        ["Dispatcher.Run", "PushFrame", "Application.Run", "ShowDialog", "RunAsync"];

    // This file names all five tokens, so scanning it would put GREEN out of reach -- R4's F-19 lesson:
    // a scan that reports its own declaration is not a guard. Excluded by RELATIVE PATH, not by file
    // name: `126` F-17 pointed out that a name-only exclusion lets a helper called PumpFreeHostTests.cs
    // in any subfolder walk straight through the guard.
    private const string GuardOwnPath = "PumpFreeHostTests.cs";

    [Fact]
    public void NoFileInThisProjectStartsAMessagePump()
    {
        var project = Path.Combine(MergedScope.RepoRoot(), "AST.AppScope.Tests");
        Directory.Exists(project).Should().BeTrue(
            "a guard that cannot find its own target passes by not running - the path is {0}", project);

        // THE WHOLE PROJECT, not just AppScope.cs (125 F-13): moving the pump into a helper file was a
        // one-line escape from the single-file version of this scan.
        var files = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && Path.GetRelativePath(project, file) != GuardOwnPath)
            .ToList();

        files.Should().NotBeEmpty("an empty file set is a broken scan, not a clean project");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(project, file);
            var code = StripComments(File.ReadAllText(file));

            // Whitespace is REMOVED before matching, so `Dispatcher` and `.Run();` written on two lines
            // -- or with a comment between them -- are still one call (125 F-13, 126 F-17). Comments are
            // stripped FIRST, and block comments are replaced newline-for-newline so the line numbers
            // below stay true.
            var collapsed = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

            foreach (var api in PumpApis.Where(api => collapsed.Contains(api, StringComparison.Ordinal)))
            {
                var lines = code.Split('\n');
                var line = Array.FindIndex(lines, text => text.Contains(api, StringComparison.Ordinal));
                offenders.Add(line >= 0
                    ? $"{name}:{line + 1} calls '{api}'"
                    : $"{name} contains '{api}' split across lines or interrupted by a comment");
            }
        }

        offenders.Should().BeEmpty(
            "this host must never pump a message loop. A pump lets WPF run the callback its Application "
            + "constructor queued, which starts Prism, which fails on DirectoryModuleCatalog and takes "
            + "the host down through App's fail-closed net - measured 2026-08-20, on every run. "
            + "⚠️ WHAT THIS SCAN CANNOT SEE, so that its silence is never read as proof: a pump reached "
            + "through reflection, a delegate, a using-alias, or inside a library call this project "
            + "makes; and a token spelled differently from these five. It DOES see line comments, block "
            + "comments and split tokens, because it strips comments before matching - and it will "
            + "over-strip a `//` or `/*` that appears inside a string literal, and this project's "
            + "sources DO contain one - MergedScope.cs holds a XAML namespace URI - so everything "
            + "after that `//` is dropped from the line for matching purposes. Accepted, because the "
            + "stripper is not a lexer and no pump token has ever followed a literal `//`. "
            + "If a future host "
            + "genuinely needs a pump, that is a design change requiring per-process isolation "
            + "(spec: Candidates H1-H3), not a line added here");
    }

    // Deliberately NOT a full C# lexer: it does not know string literals (see the failure message).
    // Block comments become the same number of newlines they consumed, so a line number computed after
    // stripping still points at the real source line.
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(
            source,
            @"/\*.*?\*/",
            match => new string('\n', match.Value.Count(c => c == '\n')),
            RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }
}
