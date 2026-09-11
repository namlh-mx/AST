using System.Text.RegularExpressions;
using FluentAssertions;

namespace AST.Meta.Tests;

// Prevent the nine IAM declaration-screen sentences from being rewritten outside their named homes.
// Each sentence must appear exactly once as a full quoted production literal under AST.Shell/, and
// that occurrence must be the initializer of its named declaration: the eight constants in
// IamDeclarationMessages, and PlatformErrorDescriber.CatchAll for the ninth. Routing (which
// sentence appears when) stays in the ViewModels; this guard does not read it.
//
// WHAT THIS GUARD DOES NOT CATCH — declared, so the claim is not read wider than the mechanism:
//   1. A sentence ASSEMBLED rather than written whole — concatenation, interpolation, resources,
//      or generated equivalents. The scan matches one quoted token, so it cannot find an
//      additional dynamically assembled equivalent of a sentence that already has its named
//      full-literal initializer. That initializer remains mandatory: zero full literals fail
//      this guard. That is the same standing limit as PlatformCodeLiteralAbsenceTests: static
//      inspection cannot bound a DYNAMIC mint site.
//   2. A literal outside AST.Shell/ (for example a copy at a Core startup mint site). This
//      perimeter is AST.Shell/ only; other projects are out of this row.
//   3. A literal inside a /* block comment */. Only // line comments are stripped, so such a
//      literal is still scanned and REPORTED. That direction is deliberate: this guard checks
//      for the PRESENCE of exactly one owning initializer, and a false POSITIVE fails loudly.
public sealed class IamDeclarationMessageLiteralTests
{
    [Fact]
    public void Each_sentence_has_exactly_one_full_literal_at_its_named_initializer()
    {
        var root = MetaTest.RepoRoot();
        var homes = IamDeclarationMessageLiteralDetector.Homes;
        var occurrences = IamDeclarationMessageLiteralDetector.FindQuotedLiterals(root);

        var failures = new List<string>();
        foreach (var home in homes)
        {
            var found = occurrences.Where(o => o.Sentence == home.Sentence).ToList();
            if (found.Count != 1)
            {
                failures.Add(
                    $"{home.ConstName}: expected exactly 1 full literal, found {found.Count}"
                    + (found.Count == 0
                        ? ""
                        : " at " + string.Join(", ", found.Select(o => $"{o.RelativePath}:{o.Line}"))));
                continue;
            }

            var hit = found[0];
            if (!IamDeclarationMessageLiteralDetector.IsDeclaringInitializer(hit, home))
            {
                failures.Add(
                    $"{home.ConstName}: the single literal at {hit.RelativePath}:{hit.Line} "
                    + $"is not `const string {home.ConstName} = \"...\"` in {home.DeclaringFile}");
            }
        }

        string.Join("\n", failures).Should().BeEmpty(
            "each of the nine sentences must be written exactly once under AST.Shell/, as the "
            + "initializer of its named declaration. Offenders:\n" + string.Join("\n", failures));
    }
}

internal sealed record IamDeclarationSentenceHome(string Sentence, string DeclaringFile, string ConstName);

internal sealed record IamQuotedLiteral(string Sentence, string RelativePath, int Line, string LineText);

internal static class IamDeclarationMessageLiteralDetector
{
    internal static readonly IamDeclarationSentenceHome[] Homes =
    [
        new(
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "StaleDataReload"),
        new(
            "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "PeriodOverlap"),
        new(
            "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "CloseDateOutsideDeclaredPeriod"),
        new(
            "Đã lưu. Dữ liệu hiển thị chưa cập nhật.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "SavedDisplayNotUpdated"),
        new(
            "Dữ liệu đang được người dùng khác khai báo.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "ConcurrentDeclaration"),
        new(
            "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "CloseDateBeforeStart"),
        new(
            "Người dùng không được cấp quyền.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "PermissionDenied"),
        new(
            "Ứng dụng không tải được dữ liệu lịch sử.",
            "AST.Shell/Presentation/Iam/IamDeclarationMessages.cs",
            "HistoryLoadFailed"),
        new(
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
            "AST.Shell/Presentation/PlatformErrorDescriber.cs",
            "CatchAll"),
    ];

    public static IReadOnlyList<IamQuotedLiteral> FindQuotedLiterals(string root)
    {
        var shell = Path.Combine(root, "AST.Shell");
        var results = new List<IamQuotedLiteral>();
        foreach (var path in Directory.EnumerateFiles(shell, "*.cs", SearchOption.AllDirectories))
        {
            if (MetaTest.IsGenerated(root, path))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StripLineComment(lines[i]);
                foreach (var home in Homes)
                {
                    if (QuotedToken(home.Sentence).IsMatch(line))
                    {
                        results.Add(new IamQuotedLiteral(home.Sentence, relative, i + 1, line));
                    }
                }
            }
        }

        return results;
    }

    public static bool IsDeclaringInitializer(IamQuotedLiteral hit, IamDeclarationSentenceHome home)
    {
        if (!string.Equals(hit.RelativePath, home.DeclaringFile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DeclaringInitializer(home.ConstName, home.Sentence).IsMatch(hit.LineText);
    }

    private static Regex QuotedToken(string sentence) =>
        new("\"" + Regex.Escape(sentence) + "\"", RegexOptions.Compiled);

    private static Regex DeclaringInitializer(string constName, string sentence) =>
        new(
            @"\bconst\s+string\s+" + Regex.Escape(constName) + @"\s*=\s*"""
            + Regex.Escape(sentence) + @"""\s*;",
            RegexOptions.Compiled);

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }
}
