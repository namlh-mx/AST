using FluentAssertions;

namespace AST.Meta.Tests;

// Guard-parser fixtures for card 342 C3.6, plus the production-tree contract.
// Perimeter (C3.7): this guard enforces XAML-declared production hosts. It does not
// discover a host constructed wholly in production C#. There is no such host at the
// card-344 base SHA. Do not pretend Roslyn closes that gap.
public class AstOverlayDefaultFocusContractTests
{
    [Fact]
    public void Production_tree_has_exactly_one_marker_on_the_org_unit_host()
    {
        var scans = XamlCompositionGraph.Load(MetaTest.RepoRoot()).ScanHosts();
        scans.Should().ContainSingle("one XAML-declared production AstOverlayHost");
        var scan = scans[0];
        scan.ContractFailure.Should().BeNull();
        scan.HostFile.Should().Be("AST/Views/Iam/OrgUnit/OrgUnitDeclarationView.xaml");
        scan.Markers.Should().ContainSingle();
        scan.Markers[0].File.Should().Be("AST/Views/Iam/OrgUnit/OrgUnitSupplementalDialog.xaml");
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
    public void Bound_content_fails_closed()
    {
        using var repo = FixtureRepo.BoundContent();
        var failure = Single(repo).ContractFailure;
        failure.Should().NotBeNull();
        failure.Should().Contain("bound or assigned Content");
        failure.Should().Contain("statically provable XAML target");
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
        File("AST.Prod/Host.xaml", """
            <UserControl x:Class="AST.Prod.HostView"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI"
                         xmlns:local="clr-namespace:AST.Prod">
              <controls:AstOverlayHost PreviewKeyDown="OnPreview">
                <local:Dialog />
              </controls:AstOverlayHost>
            </UserControl>
            """),
        Dialog(marker: true));

    public static FixtureRepo PreviewKeyDownOnMarked() => Write(
        Host("        <local:Dialog />"),
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog",
            """        <Button controls:AstOverlayHost.IsDefaultFocus="True" PreviewKeyDown="OnPreview" />""")));

    public static FixtureRepo BoundContent() => Write(
        File("AST.Prod/Host.xaml", """
            <UserControl x:Class="AST.Prod.HostView"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI">
              <controls:AstOverlayHost Content="{Binding Anything}" />
            </UserControl>
            """));

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

    private static (string Path, string Contents) Host(string content) =>
        File("AST.Prod/Host.xaml", HostXaml(content));

    private static (string Path, string Contents) Dialog(bool marker) =>
        File("AST.Prod/Dialog.xaml", UserControl("AST.Prod.Dialog",
            marker
                ? """        <Button controls:AstOverlayHost.IsDefaultFocus="True" />"""
                : "        <Button />"));

    private static (string Path, string Contents) File(string path, string contents) => (path, contents);

    private static string HostXaml(string content) => $"""
        <UserControl x:Class="AST.Prod.HostView"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI"
                     xmlns:local="clr-namespace:AST.Prod">
          <controls:AstOverlayHost>
        {content}
          </controls:AstOverlayHost>
        </UserControl>
        """;

    private static string UserControl(string xClass, string content) => $"""
        <UserControl x:Class="{xClass}"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="clr-namespace:AST.Controls;assembly=AST.UI"
                     xmlns:local="clr-namespace:AST.Prod">
        {content}
        </UserControl>
        """;

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
