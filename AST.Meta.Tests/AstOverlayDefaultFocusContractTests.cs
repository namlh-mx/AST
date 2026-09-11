using FluentAssertions;

namespace AST.Meta.Tests;

// Guard-parser fixtures for card 342 C3.6 / card 346 Parts D and E, plus the production-tree contract.
// Perimeter (C3.7): this guard enforces XAML-declared production hosts. It does not discover a host
// constructed wholly in production C#. An XAML-declared host whose content is code-assigned or otherwise
// not statically provable fails closed. Do not pretend Roslyn closes the wholly-C#-constructed gap.
public class AstOverlayDefaultFocusContractTests
{
    [Fact]
    public void Production_tree_includes_the_org_unit_host_and_evaluates_every_host()
    {
        var scans = XamlCompositionGraph.Load(MetaTest.RepoRoot()).ScanHosts();
        AssertEveryHostSatisfiesContract(scans);
        var org = scans.Should().ContainSingle(s =>
            s.HostFile == "AST/Views/Iam/OrgUnit/OrgUnitDeclarationView.xaml").Which;
        org.Markers.Should().ContainSingle();
        org.Markers[0].File.Should().Be("AST/Views/Iam/OrgUnit/OrgUnitSupplementalDialog.xaml");
        org.StyleReference.Should().Be("{StaticResource AstOverlayHost}");
        org.BackgroundLiteral.Should().Be("#80000000");
    }

    [Fact]
    public void Current_shape_host_file_to_supplemental_file_gives_one()
    {
        using var repo = FixtureRepo.CurrentShape();
        Single(repo).Markers.Should().HaveCount(1);
        Single(repo).ContractFailure.Should().BeNull();
    }

    [Fact]
    public void Zero_marker_fails()
    {
        using var repo = FixtureRepo.ZeroMarker();
        Single(repo).Markers.Should().BeEmpty();
        Single(repo).ContractFailure.Should().Contain("0 true IsDefaultFocus marker");
    }

    [Fact]
    public void Two_markers_fail()
    {
        using var repo = FixtureRepo.TwoMarkers();
        Single(repo).Markers.Should().HaveCount(2);
        Single(repo).ContractFailure.Should().Contain("2 true IsDefaultFocus marker");
    }

    [Fact]
    public void Marker_two_xaml_backed_controls_deep_is_found()
    {
        using var repo = FixtureRepo.TwoDeep();
        Single(repo).Markers.Should().HaveCount(1);
        Single(repo).ContractFailure.Should().BeNull();
        Single(repo).Markers[0].File.Should().Contain("Leaf.xaml");
    }

    [Fact]
    public void One_direct_plus_one_two_deep_marker_reports_two()
    {
        using var repo = FixtureRepo.DirectPlusTwoDeep();
        Single(repo).Markers.Should().HaveCount(2);
        Single(repo).ContractFailure.Should().Contain("2 true IsDefaultFocus marker");
    }

    [Fact]
    public void Wrong_namespace_AstOverlayHost_is_ignored()
    {
        using var repo = FixtureRepo.WrongNamespace();
        XamlCompositionGraph.Load(repo.Root).ScanHosts().Should().BeEmpty();
    }

    [Fact]
    public void Unresolved_or_dynamic_content_fails_closed_naming_the_element_and_chain()
    {
        using var repo = FixtureRepo.UnresolvedContent();
        var failure = Single(repo).ContractFailure;
        failure.Should().NotBeNull();
        failure.Should().Contain("unresolved AST-owned content element");
        failure.Should().Contain("MissingDialog");
        failure.Should().Contain("chain:");
        failure.Should().Contain("statically provable XAML target");
    }

    [Fact]
    public void Composition_cycle_fails_closed()
    {
        using var repo = FixtureRepo.Cycle();
        var failure = Single(repo).ContractFailure;
        failure.Should().NotBeNull();
        failure.Should().Contain("composition cycle");
        failure.Should().Contain("chain:");
    }

    [Fact]
    public void Zero_marker_host_in_a_Tests_project_is_outside_the_production_universe()
    {
        using var repo = FixtureRepo.TestsProjectHost();
        XamlCompositionGraph.Load(repo.Root).ScanHosts().Should().BeEmpty();
    }

    [Fact]
    public void PreviewKeyDown_attribute_on_the_host_fails()
    {
        using var repo = FixtureRepo.PreviewKeyDownOnHost();
        Single(repo).ContractFailure.Should().Contain("PreviewKeyDown");
        Single(repo).ContractFailure.Should().Contain("AstOverlayHost");
    }

    [Fact]
    public void PreviewKeyDown_attribute_on_the_marked_field_fails()
    {
        using var repo = FixtureRepo.PreviewKeyDownOnMarked();
        Single(repo).ContractFailure.Should().Contain("PreviewKeyDown");
        Single(repo).ContractFailure.Should().Contain("marked default-focus");
    }

    [Fact]
    public void Owner_qualified_PreviewKeyDown_on_the_host_fails()
    {
        using var repo = FixtureRepo.OwnerQualifiedPreviewKeyDownOnHost();
        Single(repo).ContractFailure.Should().Contain("PreviewKeyDown");
        Single(repo).ContractFailure.Should().Contain("AstOverlayHost");
        Single(repo).ContractFailure.Should().NotContain("0 true IsDefaultFocus marker");
    }

    [Fact]
    public void Owner_qualified_PreviewKeyDown_on_the_marked_field_fails()
    {
        using var repo = FixtureRepo.OwnerQualifiedPreviewKeyDownOnMarked();
        Single(repo).ContractFailure.Should().Contain("PreviewKeyDown");
        Single(repo).ContractFailure.Should().Contain("marked default-focus");
        Single(repo).ContractFailure.Should().NotContain("0 true IsDefaultFocus marker");
    }

    [Fact]
    public void Bound_content_fails_closed()
    {
        using var repo = FixtureRepo.BoundContent();
        var failure = Single(repo).ContractFailure;
        failure.Should().NotBeNull();
        failure.Should().Contain("bound or assigned Content");
        failure.Should().Contain("statically provable XAML target");
    }

    [Fact]
    public void Nested_bound_content_beside_a_valid_marker_fails_closed()
    {
        using var repo = FixtureRepo.NestedBoundContent();
        var failure = Single(repo).ContractFailure;
        failure.Should().NotBeNull();
        failure.Should().Contain("bound or assigned Content");
        failure.Should().Contain("<ContentControl>");
        failure.Should().Contain("chain:");
        failure.Should().NotContain("1 true IsDefaultFocus marker");
    }

    [Fact]
    public void Resolved_root_plus_child_markers_report_two_runtime_instances()
    {
        using var repo = FixtureRepo.RootPlusChild();
        Single(repo).Markers.Should().HaveCount(2);
        Single(repo).ContractFailure.Should().Contain("2 true IsDefaultFocus marker");
        Single(repo).Markers.Select(m => m.File).Should().Contain(f => f.Contains("Dialog.xaml"));
    }

    [Fact]
    public void Usage_true_and_root_true_is_one_marked_instance()
    {
        using var repo = FixtureRepo.UsageTrueRootTrue();
        var scan = Single(repo);
        scan.Markers.Should().HaveCount(1);
        scan.ContractFailure.Should().BeNull();
        scan.Markers[0].ContributingDeclarations.Should().HaveCount(2);
    }

    [Fact]
    public void Usage_false_and_root_true_is_zero_true_markers()
    {
        using var repo = FixtureRepo.UsageFalseRootTrue();
        var scan = Single(repo);
        scan.Markers.Should().BeEmpty();
        scan.ContractFailure.Should().Contain("0 true IsDefaultFocus marker");
    }

    [Fact]
    public void Two_valid_hosts_each_satisfy_the_contract()
    {
        using var repo = FixtureRepo.TwoValidHosts();
        var scans = XamlCompositionGraph.Load(repo.Root).ScanHosts();
        scans.Should().HaveCount(2);
        AssertEveryHostSatisfiesContract(scans);
    }

    [Fact]
    public void Two_valid_hosts_first_invalid_names_that_host()
    {
        using var repo = FixtureRepo.TwoValidHosts(firstMarked: false, secondMarked: true);
        var scans = XamlCompositionGraph.Load(repo.Root).ScanHosts();
        scans.Should().HaveCount(2);
        var first = scans.Should().ContainSingle(s => s.HostFile.Contains("HostA.xaml")).Which;
        first.ContractFailure.Should().Contain("0 true IsDefaultFocus marker");
        first.ContractFailure.Should().Contain("HostA.xaml");
        scans.Should().ContainSingle(s => s.HostFile.Contains("HostB.xaml")).Which
            .ContractFailure.Should().BeNull();
    }

    [Fact]
    public void Two_valid_hosts_second_invalid_names_that_host()
    {
        using var repo = FixtureRepo.TwoValidHosts(firstMarked: true, secondMarked: false);
        var scans = XamlCompositionGraph.Load(repo.Root).ScanHosts();
        scans.Should().HaveCount(2);
        var second = scans.Should().ContainSingle(s => s.HostFile.Contains("HostB.xaml")).Which;
        second.ContractFailure.Should().Contain("0 true IsDefaultFocus marker");
        second.ContractFailure.Should().Contain("HostB.xaml");
        scans.Should().ContainSingle(s => s.HostFile.Contains("HostA.xaml")).Which
            .ContractFailure.Should().BeNull();
    }

    [Fact]
    public void Accepted_static_resource_style_key_passes()
    {
        using var repo = FixtureRepo.CurrentShape();
        Single(repo).ContractFailure.Should().BeNull();
        Single(repo).StyleReference.Should().Be("{StaticResource AstOverlayHost}");
    }

    [Fact]
    public void Missing_style_fails_closed_for_missing_style_not_marker_count()
    {
        using var repo = FixtureRepo.MissingStyle();
        var failure = Single(repo).ContractFailure;
        failure.Should().Contain("missing Style {StaticResource AstOverlayHost}");
        failure.Should().NotContain("true IsDefaultFocus marker");
    }

    [Fact]
    public void Wrong_style_key_fails_closed_for_the_wrong_key_not_marker_count()
    {
        using var repo = FixtureRepo.WrongStyleKey();
        var failure = Single(repo).ContractFailure;
        failure.Should().Contain("Style key 'AstField' is not the required AstOverlayHost");
        failure.Should().NotContain("true IsDefaultFocus marker");
    }

    [Fact]
    public void Unclassifiable_dynamic_style_fails_closed_for_that_reason_not_marker_count()
    {
        using var repo = FixtureRepo.DynamicStyle();
        var failure = Single(repo).ContractFailure;
        failure.Should().Contain("unclassifiable or dynamic Style");
        failure.Should().Contain("DynamicResource");
        failure.Should().NotContain("true IsDefaultFocus marker");
    }

    private static void AssertEveryHostSatisfiesContract(IReadOnlyList<OverlayHostScan> scans)
    {
        scans.Should().NotBeEmpty();
        foreach (var scan in scans)
            scan.ContractFailure.Should().BeNull($"{scan.HostFile}:{scan.HostLine}: {scan.ContractFailure}");
    }

    private static OverlayHostScan Single(FixtureRepo repo)
    {
        var scans = XamlCompositionGraph.Load(repo.Root).ScanHosts();
        scans.Should().ContainSingle();
        return scans[0];
    }
}

internal sealed class FixtureRepo : IDisposable
{
    private FixtureRepo(string root) => Root = root;

    public string Root { get; }

    public static FixtureRepo CurrentShape() => Write(
        Host("        <local:Dialog />"),
        Dialog(marker: true));

    public static FixtureRepo ZeroMarker() => Write(
        Host("        <local:Dialog />"),
        Dialog(marker: false));

    public static FixtureRepo TwoMarkers() => Write(
        Host("        <local:Dialog />"),
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog", """
                    <StackPanel>
                      <Button controls:AstOverlayHost.IsDefaultFocus="True" />
                      <Button controls:AstOverlayHost.IsDefaultFocus="True" />
                    </StackPanel>
                    """)));

    public static FixtureRepo TwoDeep() => Write(
        Host("        <local:Mid />"),
        File("AST.Prod/Mid.xaml", UserControl("AST.Prod.Mid", "        <local:Leaf />")),
        File("AST.Prod/Leaf.xaml", UserControl("AST.Prod.Leaf",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />""")));

    public static FixtureRepo DirectPlusTwoDeep() => Write(
        Host("""
                    <StackPanel>
                      <Button controls:AstOverlayHost.IsDefaultFocus="True" />
                      <local:Mid />
                    </StackPanel>
                    """),
        File("AST.Prod/Mid.xaml", UserControl("AST.Prod.Mid", "        <local:Leaf />")),
        File("AST.Prod/Leaf.xaml", UserControl("AST.Prod.Leaf",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />""")));

    public static FixtureRepo WrongNamespace() => Write(
        File("AST.Prod/Wrong.xaml", """
            <UserControl x:Class="AST.Prod.Wrong"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:other="clr-namespace:Other.Controls;assembly=Other">
              <other:AstOverlayHost>
                <Button />
              </other:AstOverlayHost>
            </UserControl>
            """));

    public static FixtureRepo UnresolvedContent() => Write(
        Host("        <local:MissingDialog />"));

    public static FixtureRepo Cycle() => Write(
        Host("        <local:LoopA />"),
        File("AST.Prod/LoopA.xaml", UserControl("AST.Prod.LoopA", "        <local:LoopB />")),
        File("AST.Prod/LoopB.xaml", UserControl("AST.Prod.LoopB", "        <local:LoopA />")));

    public static FixtureRepo TestsProjectHost()
    {
        var root = NewRoot();
        WriteFile(root, "AST.slnx", """
            <Solution>
              <Project Path="AST.Prod.Tests/AST.Prod.Tests.csproj" />
            </Solution>
            """);
        WriteFile(root, "AST.Prod.Tests/AST.Prod.Tests.csproj", Csproj);
        WriteFile(root, "AST.Prod.Tests/Host.xaml", HostXaml("        <Button />"));
        return new FixtureRepo(root);
    }

    public static FixtureRepo PreviewKeyDownOnHost() => Write(
        Host("        <local:Dialog />", extraHostAttributes: "PreviewKeyDown=\"OnPreview\""),
        Dialog(marker: true));

    public static FixtureRepo PreviewKeyDownOnMarked() => Write(
        Host("        <local:Dialog />"),
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" PreviewKeyDown="OnPreview" />""")));

    public static FixtureRepo OwnerQualifiedPreviewKeyDownOnHost() => Write(
        Host("        <local:Dialog />", extraHostAttributes: "TextBox.PreviewKeyDown=\"OnPreview\""),
        Dialog(marker: true));

    public static FixtureRepo OwnerQualifiedPreviewKeyDownOnMarked() => Write(
        Host("        <local:Dialog />"),
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" TextBox.PreviewKeyDown="OnPreview" />""")));

    public static FixtureRepo BoundContent() => Write(
        File("AST.Prod/Host.xaml", HostXaml(
            content: "",
            extraHostAttributes: "Content=\"{Binding Anything}\"",
            includeContentElement: false)));

    public static FixtureRepo NestedBoundContent() => Write(
        Host("""
                    <StackPanel>
                      <Button controls:AstOverlayHost.IsDefaultFocus="True" />
                      <ContentControl Content="{Binding DynamicEditor}" />
                    </StackPanel>
                    """));

    public static FixtureRepo RootPlusChild() => Write(
        Host("        <local:Dialog />"),
        File("AST.Prod/Dialog.xaml", UserControl(
            "AST.Prod.Dialog",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />""",
            rootMarker: true)));

    public static FixtureRepo UsageTrueRootTrue() => Write(
        Host("""        <local:Dialog controls:AstOverlayHost.IsDefaultFocus="True" />"""),
        File("AST.Prod/Dialog.xaml", UserControl(
            "AST.Prod.Dialog",
            "        <Button />",
            rootMarker: true)));

    public static FixtureRepo UsageFalseRootTrue() => Write(
        Host("        <local:Dialog controls:AstOverlayHost.IsDefaultFocus=\"False\" />"),
        File("AST.Prod/Dialog.xaml", UserControl(
            "AST.Prod.Dialog",
            "        <Button />",
            rootMarker: true)));

    public static FixtureRepo TwoValidHosts(bool firstMarked = true, bool secondMarked = true) => Write(
        File("AST.Prod/HostA.xaml", HostXaml("        <local:DialogA />", xClass: "AST.Prod.HostA")),
        File("AST.Prod/DialogA.xaml", UserControl("AST.Prod.DialogA",
            firstMarked
                ? """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />"""
                : "        <Button />")),
        File("AST.Prod/HostB.xaml", HostXaml("        <local:DialogB />", xClass: "AST.Prod.HostB")),
        File("AST.Prod/DialogB.xaml", UserControl("AST.Prod.DialogB",
            secondMarked
                ? """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />"""
                : "        <Button />")));

    public static FixtureRepo MissingStyle() => Write(
        Host("        <local:Dialog />", style: null),
        Dialog(marker: true));

    public static FixtureRepo WrongStyleKey() => Write(
        Host("        <local:Dialog />", style: "{StaticResource AstField}"),
        Dialog(marker: true));

    public static FixtureRepo DynamicStyle() => Write(
        Host("        <local:Dialog />", style: "{DynamicResource AstOverlayHost}"),
        Dialog(marker: true));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // temp leftover is not a test failure
        }
    }

    private static FixtureRepo Write(params (string Path, string Contents)[] files)
    {
        var root = NewRoot();
        WriteFile(root, "AST.slnx", """
            <Solution>
              <Project Path="AST.Prod/AST.Prod.csproj" />
            </Solution>
            """);
        WriteFile(root, "AST.Prod/AST.Prod.csproj", Csproj);
        foreach (var (path, contents) in files)
            WriteFile(root, path, contents);
        return new FixtureRepo(root);
    }

    private static (string Path, string Contents) Host(
        string content,
        string? style = "{StaticResource AstOverlayHost}",
        string extraHostAttributes = "") =>
        File("AST.Prod/Host.xaml", HostXaml(content, style: style, extraHostAttributes: extraHostAttributes));

    private static (string Path, string Contents) Dialog(bool marker) =>
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog",
            marker
                ? """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />"""
                : "        <Button />"));

    private static (string Path, string Contents) File(string path, string contents) => (path, contents);

    private static string HostXaml(
        string content,
        string xClass = "AST.Prod.HostView",
        string? style = "{StaticResource AstOverlayHost}",
        string extraHostAttributes = "",
        bool includeContentElement = true)
    {
        var styleAttr = style is null ? "" : $" Style=\"{style}\"";
        var extra = string.IsNullOrWhiteSpace(extraHostAttributes) ? "" : " " + extraHostAttributes.Trim();
        var inner = includeContentElement ? "\n" + content + "\n          " : "";
        return $"""
        <UserControl x:Class="{xClass}"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI"
                     xmlns:local="clr-namespace:AST.Prod">
          <controls:AstOverlayHost{styleAttr}{extra}>{inner}</controls:AstOverlayHost>
        </UserControl>
        """;
    }

    private static string UserControl(string xClass, string content, bool rootMarker = false)
    {
        var marker = rootMarker ? " controls:AstOverlayHost.IsDefaultFocus=\"True\"" : "";
        return $"""
        <UserControl x:Class="{xClass}"{marker}
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI"
                     xmlns:local="clr-namespace:AST.Prod">
        {content}
        </UserControl>
        """;
    }

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ast-overlay-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFile(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
    }
}
