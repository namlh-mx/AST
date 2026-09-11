using System.Xml;
using System.Xml.Linq;

namespace AST.Meta.Tests;

// Transitive XAML composition graph for AstOverlayHost.IsDefaultFocus. Enumerates solution
// projects the same way XamlResourceGraph does (AST.slnx directories, default item globs);
// it does not share that type, which owns resource keys, and it does not use Roslyn.
//
// Perimeter: this guard enforces XAML-declared production hosts. It does not discover a host
// constructed wholly in production C#. At the card-344 base SHA the only `new AstOverlayHost`
// calls are in AST.App.Tests. A future production C# constructor needs a locked-surface review
// that extends this model before that consumer lands.
internal sealed class XamlCompositionGraph
{
    internal const string OverlayNamespace = "AST.Controls";
    internal const string OverlayAssembly = "AST.UI";
    internal const string OverlayTypeName = "AstOverlayHost";
    internal const string MarkerLocalName = "AstOverlayHost.IsDefaultFocus";

    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly IReadOnlyDictionary<(string Assembly, string Type), XamlDocument> _byClass;
    private readonly IReadOnlyList<XamlDocument> _documents;

    private XamlCompositionGraph(
        IReadOnlyList<XamlDocument> documents,
        IReadOnlyDictionary<(string Assembly, string Type), XamlDocument> byClass)
    {
        _documents = documents;
        _byClass = byClass;
    }

    public static XamlCompositionGraph Load(string repoRoot)
    {
        var documents = new List<XamlDocument>();
        foreach (var project in ProductionProjects(repoRoot))
        {
            foreach (var file in Directory.EnumerateFiles(project.Directory, "*.xaml", SearchOption.AllDirectories))
            {
                if (MetaTest.IsGenerated(repoRoot, file))
                    continue;
                documents.Add(XamlDocument.Load(repoRoot, file, project.AssemblyName));
            }
        }

        var byClass = new Dictionary<(string, string), XamlDocument>();
        foreach (var doc in documents)
        {
            var xClass = doc.Root.Attribute(XamlNs + "Class")?.Value;
            if (string.IsNullOrWhiteSpace(xClass))
                continue;
            byClass[(doc.AssemblyName, xClass)] = doc;
        }

        return new XamlCompositionGraph(documents, byClass);
    }

    public IReadOnlyList<OverlayHostScan> ScanHosts()
    {
        var scans = new List<OverlayHostScan>();
        foreach (var doc in _documents)
        {
            foreach (var element in doc.Root.DescendantsAndSelf())
            {
                if (!IsOverlayHost(element, doc.AssemblyName))
                    continue;
                scans.Add(ScanHost(doc, element));
            }
        }

        return scans;
    }

    private OverlayHostScan ScanHost(XamlDocument hostDoc, XElement host)
    {
        var hostLine = LineOf(host);
        var markers = new List<MarkerLocation>();
        var preview = new List<string>();
        var chain = new List<string> { FormatSite(hostDoc.RelativePath, hostLine, OverlayTypeName) };
        string? closed = null;
        var styleReference = host.Attribute("Style")?.Value;
        var backgroundLiteral = host.Attribute("Background")?.Value;

        if (!TryClassifyStyle(host, out var styleFailure))
        {
            closed = FailClosed(
                hostDoc.RelativePath,
                hostLine,
                styleFailure!,
                chain,
                "The consumer needs the exact keyed Style {StaticResource AstOverlayHost}.");
            return new OverlayHostScan(
                hostDoc.RelativePath, hostLine, markers, preview, closed, styleReference, backgroundLiteral);
        }

        if (HasBoundOrAssignedContent(host, out var contentReason))
        {
            closed = FailClosed(hostDoc.RelativePath, hostLine, contentReason, chain);
            return new OverlayHostScan(
                hostDoc.RelativePath, hostLine, markers, preview, closed, styleReference, backgroundLiteral);
        }

        var content = InstantiatedContent(host).ToList();
        if (content.Count == 0)
        {
            closed = FailClosed(
                hostDoc.RelativePath,
                hostLine,
                "no statically traversable inline content",
                chain);
            return new OverlayHostScan(
                hostDoc.RelativePath, hostLine, markers, preview, closed, styleReference, backgroundLiteral);
        }

        CollectPreview(host, hostDoc.RelativePath, preview, marked: false);

        var expanding = new HashSet<(string Assembly, string Type)>();
        WalkInstantiated(
            hostDoc,
            content,
            chain,
            expanding,
            markers,
            preview,
            ref closed);

        if (closed is null && preview.Count > 0)
            closed = string.Join(Environment.NewLine, preview);

        return new OverlayHostScan(
            hostDoc.RelativePath, hostLine, markers, preview, closed, styleReference, backgroundLiteral);
    }

    private void WalkInstantiated(
        XamlDocument doc,
        IEnumerable<XElement> elements,
        List<string> chain,
        HashSet<(string Assembly, string Type)> expanding,
        List<MarkerLocation> markers,
        List<string> preview,
        ref string? closed)
    {
        foreach (var element in elements)
        {
            if (closed is not null)
                return;

            if (IsSkippedConstruct(element, out var skipKind))
            {
                if (SubtreeContainsTrueMarker(element, doc.AssemblyName)
                    || SubtreeContainsBoundContent(element))
                {
                    closed = FailClosed(
                        doc.RelativePath,
                        LineOf(element),
                        $"{skipKind} is the only statically visible route to a focus target",
                        chain);
                }

                continue;
            }

            if (HasBoundOrAssignedContent(element, out var boundReason))
            {
                closed = FailClosed(doc.RelativePath, LineOf(element), boundReason, chain);
                return;
            }

            var usageDeclared = TryReadMarker(
                element, doc.AssemblyName, out var usageValue, out var usageError);
            if (usageError is not null)
            {
                closed = FailClosed(doc.RelativePath, LineOf(element), usageError, chain);
                return;
            }

            if (TryResolve(element, doc.AssemblyName, out var key, out var resolved, out var unresolvedAst))
            {
                if (expanding.Contains(key))
                {
                    closed = FailClosed(
                        doc.RelativePath,
                        LineOf(element),
                        "composition cycle " + string.Join(" -> ", chain.Append(FormatSite(resolved!.RelativePath, LineOf(resolved.Root), key.Type))),
                        chain);
                    return;
                }

                var rootDeclared = TryReadMarker(
                    resolved!.Root, resolved.AssemblyName, out var rootValue, out var rootError);
                if (rootError is not null)
                {
                    closed = FailClosed(resolved.RelativePath, LineOf(resolved.Root), rootError, chain);
                    return;
                }

                var effectiveTrue = usageDeclared ? usageValue : rootDeclared && rootValue;
                if (effectiveTrue)
                {
                    var sites = new List<(string File, int Line)>();
                    if (usageDeclared)
                        sites.Add((doc.RelativePath, LineOf(element)));
                    if (rootDeclared)
                        sites.Add((resolved.RelativePath, LineOf(resolved.Root)));
                    markers.Add(new MarkerLocation(sites[0].File, sites[0].Line)
                    {
                        ContributingDeclarations = sites,
                    });
                    CollectPreview(element, doc.RelativePath, preview, marked: true);
                    CollectPreview(resolved.Root, resolved.RelativePath, preview, marked: true);
                }

                expanding.Add(key);
                chain.Add(FormatSite(resolved.RelativePath, LineOf(resolved.Root), key.Type));
                WalkInstantiated(
                    resolved,
                    InstantiatedContent(resolved.Root),
                    chain,
                    expanding,
                    markers,
                    preview,
                    ref closed);
                chain.RemoveAt(chain.Count - 1);
                expanding.Remove(key);
            }
            else if (unresolvedAst)
            {
                closed = FailClosed(
                    doc.RelativePath,
                    LineOf(element),
                    $"unresolved AST-owned content element <{element.Name.LocalName}>",
                    chain);
                return;
            }
            else if (usageDeclared && usageValue)
            {
                markers.Add(new MarkerLocation(doc.RelativePath, LineOf(element))
                {
                    ContributingDeclarations = [(doc.RelativePath, LineOf(element))],
                });
                CollectPreview(element, doc.RelativePath, preview, marked: true);
            }

            WalkInstantiated(doc, InstantiatedContent(element), chain, expanding, markers, preview, ref closed);
        }
    }

    private bool TryResolve(
        XElement element,
        string referringAssembly,
        out (string Assembly, string Type) key,
        out XamlDocument? resolved,
        out bool unresolvedAst)
    {
        key = default;
        resolved = null;
        unresolvedAst = false;
        if (!TryParseClr(element.Name.NamespaceName, referringAssembly, out var ns, out var assembly))
            return false;

        key = (assembly, ns + "." + element.Name.LocalName);
        if (_byClass.TryGetValue(key, out resolved))
            return true;

        unresolvedAst = IsAstOwned(ns, assembly);
        return false;
    }

    private static bool IsOverlayHost(XElement element, string referringAssembly)
    {
        if (!element.Name.LocalName.Equals(OverlayTypeName, StringComparison.Ordinal))
            return false;
        if (!TryParseClr(element.Name.NamespaceName, referringAssembly, out var ns, out var assembly))
            return false;
        return ns.Equals(OverlayNamespace, StringComparison.Ordinal)
            && assembly.Equals(OverlayAssembly, StringComparison.Ordinal);
    }

    private static bool TryReadMarker(
        XElement element,
        string referringAssembly,
        out bool value,
        out string? error)
    {
        value = false;
        error = null;
        foreach (var attr in element.Attributes())
        {
            if (!attr.Name.LocalName.Equals(MarkerLocalName, StringComparison.Ordinal))
                continue;
            if (!TryParseClr(attr.Name.NamespaceName, referringAssembly, out var ns, out var assembly))
                continue;
            if (!ns.Equals(OverlayNamespace, StringComparison.Ordinal)
                || !assembly.Equals(OverlayAssembly, StringComparison.Ordinal))
            {
                continue;
            }

            if (!bool.TryParse(attr.Value.Trim(), out value))
            {
                error = $"IsDefaultFocus value '{attr.Value}' cannot be classified as Boolean";
                return true;
            }

            return true;
        }

        return false;
    }

    private static void CollectPreview(XElement element, string file, List<string> preview, bool marked)
    {
        foreach (var attr in element.Attributes())
        {
            if (!IsPreviewKeyDownAttribute(attr.Name.LocalName))
                continue;
            var role = marked ? "marked default-focus element" : OverlayTypeName;
            preview.Add($"{file}:{LineOf(element)}: PreviewKeyDown attribute on {role}");
        }
    }

    private static bool IsPreviewKeyDownAttribute(string localName) =>
        localName.Equals("PreviewKeyDown", StringComparison.Ordinal)
        || localName.EndsWith(".PreviewKeyDown", StringComparison.Ordinal);

    private static bool TryClassifyStyle(XElement host, out string? failure)
    {
        var reference = host.Attribute("Style")?.Value;
        if (string.IsNullOrWhiteSpace(reference))
        {
            failure = "missing Style {StaticResource AstOverlayHost}";
            return false;
        }

        var trimmed = reference.Trim();
        if (trimmed.StartsWith("{StaticResource", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            var inner = trimmed[1..^1];
            var parts = inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals("StaticResource", StringComparison.Ordinal))
            {
                if (parts[1].Equals(OverlayTypeName, StringComparison.Ordinal))
                {
                    failure = null;
                    return true;
                }

                failure = $"Style key '{parts[1]}' is not the required AstOverlayHost";
                return false;
            }
        }

        failure = $"unclassifiable or dynamic Style '{trimmed}'";
        return false;
    }

    private static bool HasBoundOrAssignedContent(XElement element, out string reason)
    {
        var content = element.Attribute("Content");
        if (content is not null && IsMarkupExtension(content.Value))
        {
            reason = $"bound or assigned Content '{content.Value.Trim()}' on <{element.Name.LocalName}>";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsMarkupExtension(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') && !trimmed.StartsWith("{}");
    }

    private static IEnumerable<XElement> InstantiatedContent(XElement owner)
    {
        foreach (var child in owner.Elements())
        {
            if (child.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))
                continue;
            if (IsPropertyElement(child, owner))
            {
                foreach (var nested in child.Elements())
                    yield return nested;
                continue;
            }

            yield return child;
        }
    }

    private static bool IsPropertyElement(XElement child, XElement owner)
    {
        var local = child.Name.LocalName;
        var dot = local.LastIndexOf('.');
        if (dot <= 0)
            return false;
        var ownerName = local[..dot];
        return ownerName.Equals(owner.Name.LocalName, StringComparison.Ordinal)
            && child.Name.Namespace == owner.Name.Namespace;
    }

    private static bool IsSkippedConstruct(XElement element, out string kind)
    {
        var local = element.Name.LocalName;
        if (local.EndsWith(".Resources", StringComparison.Ordinal))
        {
            kind = ".Resources";
            return true;
        }

        if (local.Equals("Style", StringComparison.Ordinal)
            || local.Equals("ControlTemplate", StringComparison.Ordinal)
            || local.Equals("DataTemplate", StringComparison.Ordinal))
        {
            kind = local;
            return true;
        }

        if (local.Equals("ContentPresenter", StringComparison.Ordinal)
            || (local.Equals("ItemsControl", StringComparison.Ordinal)
                && element.Attribute("ItemsSource") is { } items
                && IsMarkupExtension(items.Value)))
        {
            kind = local;
            return true;
        }

        kind = "";
        return false;
    }

    private static bool SubtreeContainsTrueMarker(XElement root, string referringAssembly)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            if (TryReadMarker(el, referringAssembly, out var value, out _) && value)
                return true;
        }

        return false;
    }

    private static bool SubtreeContainsBoundContent(XElement root) =>
        root.DescendantsAndSelf().Any(el =>
            el.Attribute("Content") is { } content && IsMarkupExtension(content.Value));

    private static bool IsAstOwned(string ns, string assembly) =>
        assembly.StartsWith("AST", StringComparison.Ordinal)
        || ns.Equals("AST", StringComparison.Ordinal)
        || ns.StartsWith("AST.", StringComparison.Ordinal);

    internal static bool TryParseClr(string xmlns, string referringAssembly, out string ns, out string assembly)
    {
        ns = "";
        assembly = referringAssembly;
        const string Prefix = "clr-namespace:";
        if (!xmlns.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = xmlns[Prefix.Length..].Split(';');
        ns = parts[0].Trim();
        if (ns.Length == 0)
            return false;

        foreach (var part in parts.Skip(1))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("assembly", StringComparison.OrdinalIgnoreCase))
                assembly = kv[1].Trim();
        }

        return true;
    }

    private static IReadOnlyList<ProjectInfo> ProductionProjects(string repoRoot)
    {
        var slnx = XDocument.Load(Path.Combine(repoRoot, "AST.slnx"));
        var projects = new List<ProjectInfo>();
        foreach (var path in slnx.Root!.Descendants("Project").Select(p => p.Attribute("Path")?.Value))
        {
            if (path is null)
                continue;
            var csproj = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name.EndsWith(".Tests", StringComparison.Ordinal))
                continue;
            projects.Add(new ProjectInfo(Path.GetDirectoryName(csproj)!, ReadAssemblyName(csproj, name)));
        }

        return projects;
    }

    private static string ReadAssemblyName(string csproj, string fallback)
    {
        var root = XDocument.Load(csproj).Root!;
        var named = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("AssemblyName", StringComparison.Ordinal));
        var value = named?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int LineOf(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    private static string FormatSite(string file, int line, string type) => $"{file}:{line}/{type}";

    private static string FailClosed(
        string file,
        int line,
        string reason,
        IReadOnlyList<string> chain,
        string remediation =
            "The consumer needs a statically provable XAML target or a newly reviewed extension of this model.") =>
        $"{file}:{line}: {reason}. {remediation} chain: {string.Join(" -> ", chain)}";

    private sealed record ProjectInfo(string Directory, string AssemblyName);

    internal sealed class XamlDocument
    {
        private XamlDocument(string relativePath, string assemblyName, XElement root)
        {
            RelativePath = relativePath;
            AssemblyName = assemblyName;
            Root = root;
        }

        public string RelativePath { get; }

        public string AssemblyName { get; }

        public XElement Root { get; }

        public static XamlDocument Load(string repoRoot, string fullPath, string assemblyName)
        {
            var doc = XDocument.Load(fullPath, LoadOptions.SetLineInfo);
            var rel = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
            return new XamlDocument(rel, assemblyName, doc.Root!);
        }
    }
}

internal sealed record OverlayHostScan(
    string HostFile,
    int HostLine,
    IReadOnlyList<MarkerLocation> Markers,
    IReadOnlyList<string> PreviewKeyDown,
    string? ClosedFailure,
    string? StyleReference = null,
    string? BackgroundLiteral = null)
{
    public string? ContractFailure
    {
        get
        {
            if (ClosedFailure is not null)
                return ClosedFailure;
            if (PreviewKeyDown.Count > 0)
                return string.Join(Environment.NewLine, PreviewKeyDown);
            if (Markers.Count == 1)
                return null;
            var listed = Markers.Count == 0
                ? "none"
                : string.Join(", ", Markers.Select(m => $"{m.File}:{m.Line}"));
            return $"{HostFile}:{HostLine}: expanded instantiated composition has {Markers.Count} true IsDefaultFocus marker(s) (exactly one required): {listed}";
        }
    }
}

internal sealed record MarkerLocation(string File, int Line)
{
    public IReadOnlyList<(string File, int Line)> ContributingDeclarations { get; init; } =
        Array.Empty<(string File, int Line)>();
}
