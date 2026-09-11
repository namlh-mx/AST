using FluentAssertions;

namespace AST.Meta.Tests;

public class PreviewKeyDownAbsenceTests
{
    [Fact]
    public void Overlay_host_and_field_revert_sources_do_not_contain_PreviewKeyDown()
    {
        var root = MetaTest.RepoRoot();
        string[] files =
        [
            Path.Combine("AST.UI", "Controls", "AstOverlayHost.cs"),
            Path.Combine("AST", "Behaviors", "AstFieldRevert.cs"),
        ];

        var hits = files
            .SelectMany(rel =>
            {
                var unix = rel.Replace('\\', '/');
                return PreviewKeyDownToken.Find(unix, System.IO.File.ReadAllText(Path.Combine(root, rel)));
            })
            .ToList();

        hits.Should().BeEmpty(
            "B-04: one forbidden token PreviewKeyDown in the two owner sources, fail-closed including comments");
    }

    [Theory]
    [MemberData(nameof(ForbiddenSpellings))]
    public void Token_guard_catches_the_named_entry_points(string label, string source)
    {
        PreviewKeyDownToken.Find("probe.cs", source).Should().NotBeEmpty(label);
    }

    [Fact]
    public void Token_guard_does_not_fire_on_bubbling_KeyDown_alone()
    {
        PreviewKeyDownToken.Find("probe.cs", "AddHandler(Keyboard.KeyDownEvent, OnBubblingKeyDown);")
            .Should().BeEmpty();
    }

    public static TheoryData<string, string> ForbiddenSpellings => new()
    {
        { "CLR event subscription", "host.PreviewKeyDown += OnPreview;" },
        { "AddHandler", "AddHandler(UIElement.PreviewKeyDownEvent, handler);" },
        { "attached-event helper", "Keyboard.AddPreviewKeyDownHandler(field, handler);" },
        { "class handler", "EventManager.RegisterClassHandler(typeof(AstOverlayHost), UIElement.PreviewKeyDownEvent, handler);" },
        { "override", "protected override void OnPreviewKeyDown(KeyEventArgs e) { }" },
    };
}
