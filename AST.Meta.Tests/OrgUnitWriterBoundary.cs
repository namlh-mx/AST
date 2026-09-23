using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace AST.Meta.Tests;

// IL scan of the four production assemblies that can reach an org-unit version writer.
// The claim, the allowed callers and the exclusions are the header of OrgUnitWritePathAbsenceTests.
internal static class OrgUnitWriterBoundary
{
    internal static readonly string[] Projects =
    [
        "AST.Infrastructure",
        "AST.Modules.IAM",
        "AST",
        "AST.ConfigKeyGen",
    ];

    private const string VersionedRepository = "AST.Infrastructure.VersionedRepository`1";
    private const string OrgUnitRepository = "AST.Modules.IAM.Data.Repositories.OrgUnitRepository";
    private const string OrgUnitVersionEntity = "AST.Modules.IAM.Data.Entities.OrgUnitVersionEntity";
    private const string DeclarationService = "AST.Modules.IAM.OrgUnitDeclarationService";
    private const string IamAssembly = "AST.Modules.IAM";
    private const string InfrastructureAssembly = "AST.Infrastructure";

    private static readonly HashSet<string> WriterNames = new(StringComparer.Ordinal)
    {
        "UpsertVersionAsync",
        "CloseVersionAsync",
        "DeleteVersionAsync",
        "CancelVersionAsync",
        "AutoCutExclusivelyOwnedAsync",
        "UpsertAsync",
        "CancelPlanAsync",
        "MarkVersionInactiveForReplaceAsync",
        "StampVersionReplacedAsync",
    };

    // Reference-type nullability is erased. Nullable value types stay Nullable<T>. !0 is TVersion.
    private static readonly string[] VersionedRepositoryPins =
    [
        "AutoCutExclusivelyOwnedAsync(System.Data.IDbConnection,System.Data.IDbTransaction,Int64,System.Collections.Generic.IReadOnlyList`1<AST.Core.EffectivePeriod.EffectivePeriod>,System.DateOnly,String,String,System.Collections.Generic.IReadOnlySet`1<System.ValueTuple`2<String,Int64>>) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<System.Collections.Generic.IReadOnlyList`1<AST.Core.Data.AutoCutOutcome>>>",
        "CancelVersionAsync(AST.Infrastructure.ICompositeWriteContext,Int64,Int64,System.DateOnly,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "CancelVersionAsync(Int64,Int64,System.DateOnly,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "CloseVersionAsync(AST.Infrastructure.ICompositeWriteContext,Int64,Int64,System.DateOnly,AST.Core.Time.OperationDate,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "CloseVersionAsync(Int64,Int64,System.DateOnly,AST.Core.Time.OperationDate,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "DeleteVersionAsync(Int64,Int64) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "UpsertVersionAsync(AST.Infrastructure.ICompositeWriteContext,Int64,AST.Core.EffectivePeriod.EffectivePeriod,!0,String,String,Nullable<AST.Core.Data.VersionOperationKind>) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "UpsertVersionAsync(Int64,AST.Core.EffectivePeriod.EffectivePeriod,!0,String,String,Nullable<AST.Core.Data.VersionOperationKind>) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
    ];

    private static readonly string[] OrgUnitRepositoryPins =
    [
        "CancelPlanAsync(AST.Infrastructure.ICompositeWriteContext,Int64,Int64,System.DateOnly,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "CancelPlanAsync(Int64,Int64,System.DateOnly,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "CloseVersionAsync(AST.Infrastructure.ICompositeWriteContext,Int64,Int64,System.DateOnly,AST.Core.Time.OperationDate,String,String) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "MarkVersionInactiveForReplaceAsync(AST.Infrastructure.ICompositeWriteContext,Int64) -> System.Threading.Tasks.Task`1<Int32>",
        "StampVersionReplacedAsync(AST.Infrastructure.ICompositeWriteContext,Int64,Int64) -> System.Threading.Tasks.Task",
        "UpsertAsync(AST.Infrastructure.ICompositeWriteContext,Int64,AST.Core.EffectivePeriod.EffectivePeriod,String,String,String,Nullable<Int64>,AST.Core.Data.VersionOperationKind,String,String,AST.Core.Iam.Repositories.OrgUnitSupplementalDto) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
        "UpsertAsync(Int64,AST.Core.EffectivePeriod.EffectivePeriod,String,String,String,Nullable<Int64>,AST.Core.Data.VersionOperationKind,String,String,AST.Core.Iam.Repositories.OrgUnitSupplementalDto) -> System.Threading.Tasks.Task`1<ErrorOr.ErrorOr`1<AST.Core.Data.UpsertResult>>",
    ];

    private static readonly Dictionary<ushort, OperandType> OperandTypes = BuildOperandTypes();

    internal static ScanReport Scan()
    {
        var location = typeof(OrgUnitWriterBoundary).Assembly.Location;
        if (!TryConfiguration(location, out var configuration, out var failure))
        {
            return ScanReport.Fail(failure);
        }

        string repoRoot;
        try
        {
            repoRoot = MetaTest.RepoRoot();
        }
        catch (InvalidOperationException ex)
        {
            return ScanReport.Fail(BuildFirst(ex.Message));
        }

        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        if (!File.Exists(propsPath))
        {
            return ScanReport.Fail(BuildFirst("Directory.Build.props not found: " + propsPath));
        }

        var opened = new List<OpenedAssembly>();
        try
        {
            foreach (var project in Projects)
            {
                var openedOrFailure = OpenProject(repoRoot, project, configuration, propsPath);
                if (openedOrFailure.Failure is not null)
                {
                    return ScanReport.Fail(openedOrFailure.Failure);
                }

                opened.Add(openedOrFailure.Assembly!);
            }

            var pinned = new List<PinnedMethod>();
            string? pinFailure = PinDeclaringTypes(opened, pinned);
            if (pinFailure is not null)
            {
                return ScanReport.Fail(pinFailure);
            }

            if (!RequiredTypesResolve(opened, out var missingType))
            {
                return ScanReport.Fail("unresolved required type: " + missingType);
            }

            var offenders = new List<string>();
            var serviceUses = 0;
            foreach (var assembly in opened)
            {
                var useFailure = ScanAssembly(repoRoot, assembly, pinned, offenders, ref serviceUses);
                if (useFailure is not null)
                {
                    return ScanReport.Fail(useFailure);
                }
            }

            if (serviceUses == 0)
            {
                return ScanReport.Fail("no allowed use in OrgUnitDeclarationService");
            }

            return ScanReport.Offending(offenders);
        }
        catch (BadImageFormatException ex)
        {
            return ScanReport.Fail(BuildFirst("cannot read metadata: " + ex.Message));
        }
        finally
        {
            foreach (var assembly in opened)
            {
                assembly.Dispose();
            }
        }
    }

    private static bool TryConfiguration(string location, out string configuration, out string failure)
    {
        configuration = "";
        var tfmDir = Path.GetDirectoryName(location);
        var cfgDir = tfmDir is null ? null : Path.GetDirectoryName(tfmDir);
        var binDir = cfgDir is null ? null : Path.GetDirectoryName(cfgDir);
        if (tfmDir is null || cfgDir is null || binDir is null
            || !string.Equals(Path.GetFileName(binDir), "bin", StringComparison.OrdinalIgnoreCase))
        {
            failure = BuildFirst("configuration cannot be derived from " + location);
            return false;
        }

        configuration = Path.GetFileName(cfgDir);
        failure = "";
        return true;
    }

    private static OpenedOrFailure OpenProject(string repoRoot, string project, string configuration, string propsPath)
    {
        var projectDir = Path.Combine(repoRoot, project);
        if (!Directory.Exists(projectDir))
        {
            return OpenedOrFailure.Fail(BuildFirst("project root not found: " + projectDir));
        }

        var csproj = Path.Combine(projectDir, project + ".csproj");
        if (!File.Exists(csproj))
        {
            return OpenedOrFailure.Fail(BuildFirst("project file not found: " + csproj));
        }

        XDocument projectXml;
        try
        {
            projectXml = XDocument.Load(csproj);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return OpenedOrFailure.Fail(BuildFirst("cannot read " + csproj + ": " + ex.Message));
        }

        var frameworks = projectXml.Descendants().Where(e => e.Name.LocalName == "TargetFramework").ToList();
        var multi = projectXml.Descendants().Any(e => e.Name.LocalName == "TargetFrameworks");
        if (multi || frameworks.Count != 1 || string.IsNullOrWhiteSpace(frameworks[0].Value))
        {
            return OpenedOrFailure.Fail(BuildFirst(csproj + " has no single TargetFramework"));
        }

        var unsupported = UnsupportedElement(repoRoot, projectDir, csproj, projectXml, propsPath);
        if (unsupported is not null)
        {
            return OpenedOrFailure.Fail(unsupported);
        }

        var tfm = frameworks[0].Value.Trim();
        var dll = Path.Combine(projectDir, "obj", configuration, tfm, project + ".dll");
        if (!File.Exists(dll))
        {
            return OpenedOrFailure.Fail(BuildFirst("DLL not found: " + dll));
        }

        var newer = NewerInput(repoRoot, projectDir, csproj, propsPath, dll);
        if (newer is not null)
        {
            return OpenedOrFailure.Fail(
                "rebuild first (dotnet build AST.slnx): " + newer + " is newer than " + dll);
        }

        FileStream? stream = null;
        PEReader? pe = null;
        MetadataReaderProvider? pdbProvider = null;
        try
        {
            stream = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            pe = new PEReader(stream);
            stream = null;
            if (!pe.TryOpenAssociatedPortablePdb(dll, OpenPdb, out pdbProvider, out _) || pdbProvider is null)
            {
                pe.Dispose();
                return OpenedOrFailure.Fail(BuildFirst("PDB not found: " + dll));
            }

            var reader = pe.GetMetadataReader();
            var pdb = pdbProvider.GetMetadataReader();
            var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
            return OpenedOrFailure.Ok(new OpenedAssembly(assemblyName, pe, pdbProvider, reader, pdb));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        {
            pdbProvider?.Dispose();
            pe?.Dispose();
            stream?.Dispose();
            return OpenedOrFailure.Fail(BuildFirst("cannot read " + dll + ": " + ex.Message));
        }
    }

    private static Stream? OpenPdb(string path) =>
        File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;

    private static string? UnsupportedElement(
        string repoRoot, string projectDir, string csproj, XDocument projectXml, string propsPath)
    {
        var buildFile = UnsupportedDirectoryBuild(repoRoot, projectDir);
        if (buildFile is not null)
        {
            return buildFile;
        }

        var files = new List<(string Path, XDocument Xml)> { (csproj, projectXml) };
        AddIfExists(files, propsPath);
        foreach (var (path, xml) in files)
        {
            foreach (var element in xml.Descendants())
            {
                var name = element.Name.LocalName;
                if (name.Equals("Import", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported element Import in " + Relative(repoRoot, path);
                }

                if (name.Equals("Compile", StringComparison.OrdinalIgnoreCase)
                    && element.Attributes().Any(a => a.Name.LocalName.Equals("Remove", StringComparison.OrdinalIgnoreCase)))
                {
                    return "unsupported element Compile Remove in " + Relative(repoRoot, path);
                }

                if ((name.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("EnableDefaultItems", StringComparison.OrdinalIgnoreCase))
                    && element.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported element " + name + " in " + Relative(repoRoot, path);
                }

                if (name.Equals("DefaultItemExcludes", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported element DefaultItemExcludes in " + Relative(repoRoot, path);
                }
            }
        }

        return null;
    }

    private static string? UnsupportedDirectoryBuild(string repoRoot, string projectDir)
    {
        var current = Path.GetFullPath(projectDir);
        var root = Path.GetFullPath(repoRoot);
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            var props = Path.Combine(current, "Directory.Build.props");
            if (File.Exists(props))
            {
                return "unsupported build file " + Relative(repoRoot, props);
            }

            var targets = Path.Combine(current, "Directory.Build.targets");
            if (File.Exists(targets))
            {
                return "unsupported build file " + Relative(repoRoot, targets);
            }

            current = Path.GetDirectoryName(current)!;
        }

        var rootTargets = Path.Combine(root, "Directory.Build.targets");
        if (File.Exists(rootTargets))
        {
            return "unsupported build file " + Relative(repoRoot, rootTargets);
        }

        return null;
    }

    private static void AddIfExists(List<(string Path, XDocument Xml)> files, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        files.Add((path, XDocument.Load(path)));
    }

    private static string? NewerInput(string repoRoot, string projectDir, string csproj, string propsPath, string dll)
    {
        var dllTime = File.GetLastWriteTimeUtc(dll);
        string? newer = null;
        DateTime newerTime = default;
        void Consider(string path)
        {
            var time = File.GetLastWriteTimeUtc(path);
            if (time > dllTime && (newer is null || time > newerTime))
            {
                newer = Relative(repoRoot, path);
                newerTime = time;
            }
        }

        Consider(csproj);
        Consider(propsPath);
        foreach (var source in EnumerateSources(projectDir))
        {
            Consider(source);
        }

        return newer;
    }

    private static IEnumerable<string> EnumerateSources(string projectDir)
    {
        var pending = new Stack<string>();
        pending.Push(projectDir);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" || name.StartsWith('.'))
                {
                    continue;
                }

                pending.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                yield return file;
            }
        }
    }

    private static string? PinDeclaringTypes(List<OpenedAssembly> opened, List<PinnedMethod> pinned)
    {
        var versioned = FindType(opened, InfrastructureAssembly, VersionedRepository);
        var repository = FindType(opened, IamAssembly, OrgUnitRepository);
        if (versioned is null || repository is null)
        {
            return "unresolved required type: "
                + (versioned is null ? VersionedRepository : OrgUnitRepository);
        }

        var versionedActual = DeclaredWriters(versioned.Value.Reader, versioned.Value.Handle, pinned);
        var repositoryActual = DeclaredWriters(repository.Value.Reader, repository.Value.Handle, pinned);
        var versionedMismatch = Mismatch(VersionedRepository, versionedActual, VersionedRepositoryPins);
        var repositoryMismatch = Mismatch(OrgUnitRepository, repositoryActual, OrgUnitRepositoryPins);
        if (versionedMismatch is null)
        {
            return repositoryMismatch;
        }

        return repositoryMismatch is null ? versionedMismatch : versionedMismatch + "\n" + repositoryMismatch;
    }

    private static List<string> DeclaredWriters(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        List<PinnedMethod> pinned)
    {
        var actual = new List<string>();
        var decoder = new TypeDecoder(reader.GetString(reader.GetAssemblyDefinition().Name));
        foreach (var methodHandle in reader.GetTypeDefinition(handle).GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var access = method.Attributes & MethodAttributes.MemberAccessMask;
            if (access == MethodAttributes.Private)
            {
                continue;
            }

            var name = reader.GetString(method.Name);
            if (!WriterNames.Contains(name))
            {
                continue;
            }

            var signature = method.DecodeSignature(decoder, null);
            actual.Add(Format(name, signature, substitution: null));
            pinned.Add(new PinnedMethod(name, signature));
        }

        actual.Sort(StringComparer.Ordinal);
        return actual;
    }

    private static string? Mismatch(string typeName, List<string> actual, string[] expected)
    {
        var expectedSorted = expected.OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (actual.SequenceEqual(expectedSorted))
        {
            return null;
        }

        return "pin mismatch on " + typeName
            + "\nactual:\n" + string.Join("\n", actual)
            + "\nexpected:\n" + string.Join("\n", expectedSorted);
    }

    private static bool RequiredTypesResolve(List<OpenedAssembly> opened, out string missing)
    {
        if (FindType(opened, InfrastructureAssembly, VersionedRepository) is null)
        {
            missing = VersionedRepository;
            return false;
        }

        if (FindType(opened, IamAssembly, OrgUnitRepository) is null)
        {
            missing = OrgUnitRepository;
            return false;
        }

        if (FindType(opened, IamAssembly, OrgUnitVersionEntity) is null)
        {
            missing = OrgUnitVersionEntity;
            return false;
        }

        if (FindType(opened, IamAssembly, DeclarationService) is null)
        {
            missing = DeclarationService;
            return false;
        }

        missing = "";
        return true;
    }

    private static FoundType? FindType(List<OpenedAssembly> opened, string assembly, string fullName)
    {
        foreach (var candidate in opened)
        {
            if (!string.Equals(candidate.AssemblyName, assembly, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var handle in candidate.Reader.TypeDefinitions)
            {
                if (TypeFullName(candidate.Reader, handle) == fullName)
                {
                    return new FoundType(candidate.Reader, handle);
                }
            }
        }

        return null;
    }

    private static string? ScanAssembly(
        string repoRoot,
        OpenedAssembly assembly,
        List<PinnedMethod> pinned,
        List<string> offenders,
        ref int serviceUses)
    {
        var decoder = new TypeDecoder(assembly.AssemblyName);
        foreach (var methodHandle in assembly.Reader.MethodDefinitions)
        {
            var method = assembly.Reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            MethodBodyBlock body;
            try
            {
                body = assembly.Pe.GetMethodBody(method.RelativeVirtualAddress);
            }
            catch (BadImageFormatException ex)
            {
                return BuildFirst("cannot decode IL of " + MethodLabel(assembly.Reader, methodHandle) + ": " + ex.Message);
            }

            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                var ilOffset = il.Offset;
                ushort code;
                try
                {
                    code = ReadOpcode(ref il);
                }
                catch (BadImageFormatException ex)
                {
                    return BuildFirst("cannot decode IL of " + MethodLabel(assembly.Reader, methodHandle) + ": " + ex.Message);
                }

                if (!OperandTypes.TryGetValue(code, out var operand))
                {
                    return "cannot decode IL of " + MethodLabel(assembly.Reader, methodHandle)
                        + ": unknown opcode 0x" + code.ToString("X4");
                }

                if (!IsUse(code))
                {
                    var skip = SkipOperand(ref il, operand);
                    if (skip is not null)
                    {
                        return skip + " in " + MethodLabel(assembly.Reader, methodHandle);
                    }

                    continue;
                }

                var token = il.ReadInt32();
                var resolved = ResolveToken(assembly.Reader, decoder, code, token);
                if (resolved.Kind == TokenKind.Unresolved)
                {
                    return "unresolved method binding: " + Mnemonic(code)
                        + " token 0x" + token.ToString("X8")
                        + " in " + MethodLabel(assembly.Reader, methodHandle);
                }

                if (resolved.Kind == TokenKind.NotMethod || !WriterNames.Contains(resolved.Name!))
                {
                    continue;
                }

                var parentClass = Classify(resolved.Parent!);
                if (parentClass == ParentClass.Other)
                {
                    continue;
                }

                var substitution = Substitution(resolved.Parent!);
                var matched = pinned.FirstOrDefault(p =>
                    p.Name == resolved.Name
                    && FormatsEqual(resolved.Name, resolved.Signature, p.Signature, substitution));
                if (matched is null)
                {
                    continue;
                }

                var location = Locate(repoRoot, assembly, methodHandle, ilOffset, MethodLabel(assembly.Reader, methodHandle));
                if (location.Failure is not null)
                {
                    return location.Failure;
                }

                var caller = Walk(assembly.Reader, method.GetDeclaringType());
                var callerName = TypeFullName(assembly.Reader, caller);
                if (IsAllowed(assembly.AssemblyName, callerName))
                {
                    if (assembly.AssemblyName == IamAssembly && callerName == DeclarationService)
                    {
                        serviceUses++;
                    }

                    continue;
                }

                offenders.Add(
                    Mnemonic(code) + " " + (parentClass == ParentClass.Open ? "open" : "orgunit")
                    + " " + callerName
                    + " target=" + Format(resolved.Name!, resolved.Signature, substitution)
                    + " @ " + location.Text);
            }
        }

        return null;
    }

    private static bool FormatsEqual(
        string name,
        MethodSignature<TypeForm> call,
        MethodSignature<TypeForm> pinnedSignature,
        ImmutableArray<TypeForm>? substitution) =>
        Format(name, call, substitution) == Format(name, pinnedSignature, substitution);

    private static Located Locate(
        string repoRoot,
        OpenedAssembly assembly,
        MethodDefinitionHandle methodHandle,
        int ilOffset,
        string methodLabel)
    {
        var row = MetadataTokens.GetRowNumber(methodHandle);
        if (row <= 0 || row > assembly.Pdb.MethodDebugInformation.Count)
        {
            return Located.Fail("use has no sequence point: " + methodLabel);
        }

        var debug = assembly.Pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
        SequencePoint? best = null;
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.IsHidden || point.Offset > ilOffset)
            {
                continue;
            }

            if (best is null || point.Offset >= best.Value.Offset)
            {
                best = point;
            }
        }

        if (best is null)
        {
            return Located.Fail("use has no sequence point: " + methodLabel);
        }

        if (best.Value.Document.IsNil)
        {
            return Located.Fail("use has no document: " + methodLabel);
        }

        var document = assembly.Pdb.GetDocument(best.Value.Document);
        var path = assembly.Pdb.GetString(document.Name);
        if (string.IsNullOrEmpty(path))
        {
            return Located.Fail("use has no document: " + methodLabel);
        }

        return Located.At(Relative(repoRoot, path) + ":" + best.Value.StartLine);
    }

    private static ResolvedToken ResolveToken(MetadataReader reader, TypeDecoder decoder, ushort code, int token)
    {
        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return ResolvedToken.Unresolved();
        }

        return ResolveHandle(reader, decoder, code, handle);
    }

    private static ResolvedToken ResolveHandle(
        MetadataReader reader,
        TypeDecoder decoder,
        ushort code,
        EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var parent = decoder.GetTypeFromDefinition(reader, method.GetDeclaringType(), 0);
                var signature = method.DecodeSignature(decoder, null);
                return ResolvedToken.Method(reader.GetString(method.Name), parent, signature);
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                if (IsFieldSignature(reader, member.Signature))
                {
                    return code == (ushort)ILOpCode.Ldtoken ? ResolvedToken.NotAMethod() : ResolvedToken.Unresolved();
                }

                TypeForm memberParent;
                try
                {
                    memberParent = ParentOf(reader, decoder, member.Parent);
                }
                catch (ArgumentException)
                {
                    return ResolvedToken.Unresolved();
                }
                catch (BadImageFormatException)
                {
                    return ResolvedToken.Unresolved();
                }

                MethodSignature<TypeForm> memberSignature;
                try
                {
                    memberSignature = member.DecodeMethodSignature(decoder, null);
                }
                catch (BadImageFormatException)
                {
                    return ResolvedToken.Unresolved();
                }

                return ResolvedToken.Method(reader.GetString(member.Name), memberParent, memberSignature);
            case HandleKind.MethodSpecification:
                var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveHandle(reader, decoder, code, spec.Method);
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
            case HandleKind.FieldDefinition:
                return code == (ushort)ILOpCode.Ldtoken ? ResolvedToken.NotAMethod() : ResolvedToken.Unresolved();
            default:
                return ResolvedToken.Unresolved();
        }
    }

    private static TypeForm ParentOf(MetadataReader reader, TypeDecoder decoder, EntityHandle parent) =>
        parent.Kind switch
        {
            HandleKind.TypeDefinition => decoder.GetTypeFromDefinition(reader, (TypeDefinitionHandle)parent, 0),
            HandleKind.TypeReference => decoder.GetTypeFromReference(reader, (TypeReferenceHandle)parent, 0),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)parent)
                .DecodeSignature(decoder, null),
            _ => throw new ArgumentException("method parent kind " + parent.Kind),
        };

    private static bool IsFieldSignature(MetadataReader reader, BlobHandle signature)
    {
        var blob = reader.GetBlobReader(signature);
        if (blob.RemainingBytes == 0)
        {
            return false;
        }

        return blob.ReadSignatureHeader().Kind == SignatureKind.Field;
    }

    private static ParentClass Classify(TypeForm parent)
    {
        if (parent is TypeForm.Var or TypeForm.MVar)
        {
            return ParentClass.Open;
        }

        if (IsNamed(parent, IamAssembly, OrgUnitRepository))
        {
            return ParentClass.OrgUnit;
        }

        if (IsNamed(parent, InfrastructureAssembly, VersionedRepository))
        {
            return ParentClass.Open;
        }

        if (parent is TypeForm.Inst inst && IsNamed(inst.Open, InfrastructureAssembly, VersionedRepository))
        {
            if (inst.Args.Length == 1 && IsNamed(inst.Args[0], IamAssembly, OrgUnitVersionEntity))
            {
                return ParentClass.OrgUnit;
            }

            if (inst.Args.Length == 1 && inst.Args[0] is TypeForm.Var or TypeForm.MVar)
            {
                return ParentClass.Open;
            }
        }

        return ParentClass.Other;
    }

    private static bool IsNamed(TypeForm type, string assembly, string fullName) =>
        type is TypeForm.Named named
        && named.FullName == fullName
        && named.Assembly == assembly;

    private static ImmutableArray<TypeForm>? Substitution(TypeForm parent) =>
        parent is TypeForm.Inst inst && IsNamed(inst.Open, InfrastructureAssembly, VersionedRepository)
            ? inst.Args
            : null;

    private static bool IsAllowed(string assembly, string fullName) =>
        (assembly == IamAssembly && fullName == DeclarationService)
        || (assembly == IamAssembly && fullName == OrgUnitRepository)
        || (assembly == InfrastructureAssembly && fullName == VersionedRepository);

    private static TypeDefinitionHandle Walk(MetadataReader reader, TypeDefinitionHandle handle)
    {
        while (true)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.IsNested)
            {
                return handle;
            }

            var name = reader.GetString(type.Name);
            if (!name.Contains('<'))
            {
                return handle;
            }

            handle = type.GetDeclaringType();
        }
    }

    private static string TypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        if (type.IsNested)
        {
            return TypeFullName(reader, type.GetDeclaringType()) + "/" + name;
        }

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string MethodLabel(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        return TypeFullName(reader, method.GetDeclaringType()) + "." + reader.GetString(method.Name);
    }

    private static string Format(string name, MethodSignature<TypeForm> signature, ImmutableArray<TypeForm>? substitution)
    {
        var parameters = string.Join(",", signature.ParameterTypes.Select(t => Display(Substitute(t, substitution))));
        return name + "(" + parameters + ") -> " + Display(Substitute(signature.ReturnType, substitution));
    }

    private static TypeForm Substitute(TypeForm type, ImmutableArray<TypeForm>? substitution)
    {
        if (substitution is null)
        {
            return type;
        }

        return type switch
        {
            TypeForm.Var variable when variable.Index < substitution.Value.Length => substitution.Value[variable.Index],
            TypeForm.Inst inst => new TypeForm.Inst(
                Substitute(inst.Open, substitution),
                inst.Args.Select(arg => Substitute(arg, substitution)).ToImmutableArray()),
            TypeForm.SzArray array => new TypeForm.SzArray(Substitute(array.Element, substitution)),
            TypeForm.MdArray array => new TypeForm.MdArray(Substitute(array.Element, substitution), array.Rank),
            TypeForm.ByRef byRef => new TypeForm.ByRef(Substitute(byRef.Element, substitution)),
            TypeForm.Pointer pointer => new TypeForm.Pointer(Substitute(pointer.Element, substitution)),
            _ => type,
        };
    }

    private static string Display(TypeForm type) => type switch
    {
        TypeForm.Prim prim => prim.Name,
        TypeForm.Named named => named.FullName,
        TypeForm.Inst inst when IsNullable(inst) => "Nullable<" + Display(inst.Args[0]) + ">",
        TypeForm.Inst inst => Display(inst.Open) + "<" + string.Join(",", inst.Args.Select(Display)) + ">",
        TypeForm.Var variable => "!" + variable.Index,
        TypeForm.MVar variable => "!!" + variable.Index,
        TypeForm.SzArray array => Display(array.Element) + "[]",
        TypeForm.MdArray array => Display(array.Element) + "[" + new string(',', array.Rank - 1) + "]",
        TypeForm.ByRef byRef => Display(byRef.Element) + "&",
        TypeForm.Pointer pointer => Display(pointer.Element) + "*",
        _ => "?",
    };

    private static bool IsNullable(TypeForm.Inst inst) =>
        inst.Args.Length == 1 && inst.Open is TypeForm.Named named && named.FullName == "System.Nullable`1";

    private static bool IsUse(ushort code) => code is
        (ushort)ILOpCode.Call or (ushort)ILOpCode.Callvirt or (ushort)ILOpCode.Newobj
        or (ushort)ILOpCode.Ldftn or (ushort)ILOpCode.Ldvirtftn or (ushort)ILOpCode.Ldtoken;

    private static string Mnemonic(ushort code) => code switch
    {
        (ushort)ILOpCode.Call => "call",
        (ushort)ILOpCode.Callvirt => "callvirt",
        (ushort)ILOpCode.Newobj => "newobj",
        (ushort)ILOpCode.Ldftn => "ldftn",
        (ushort)ILOpCode.Ldvirtftn => "ldvirtftn",
        (ushort)ILOpCode.Ldtoken => "ldtoken",
        _ => "op",
    };

    private static ushort ReadOpcode(ref BlobReader il)
    {
        var first = il.ReadByte();
        if (first != 0xFE)
        {
            return first;
        }

        return (ushort)((first << 8) | il.ReadByte());
    }

    private static string? SkipOperand(ref BlobReader il, OperandType operand)
    {
        switch (operand)
        {
            case OperandType.InlineNone:
                return null;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                il.Offset += 1;
                return null;
            case OperandType.InlineVar:
                il.Offset += 2;
                return null;
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                il.Offset += 4;
                return null;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                il.Offset += 8;
                return null;
            case OperandType.InlineSwitch:
                var count = il.ReadInt32();
                il.Offset += count * 4;
                return null;
            default:
                return "cannot decode operand " + operand;
        }
    }

    private static Dictionary<ushort, OperandType> BuildOperandTypes()
    {
        var map = new Dictionary<ushort, OperandType>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op && !map.ContainsKey((ushort)op.Value))
            {
                map.Add((ushort)op.Value, op.OperandType);
            }
        }

        return map;
    }

    private static string BuildFirst(string detail) => "build first (dotnet build AST.slnx): " + detail;

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : relative;
    }

    internal sealed record ScanReport(string? Failure, IReadOnlyList<string> Offenders)
    {
        public static ScanReport Fail(string failure) => new(failure, []);

        public static ScanReport Offending(IReadOnlyList<string> offenders) => new(null, offenders);
    }

    private enum ParentClass { OrgUnit, Open, Other }

    private enum TokenKind { NotMethod, Method, Unresolved }

    private readonly struct FoundType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        public MetadataReader Reader { get; } = reader;
        public TypeDefinitionHandle Handle { get; } = handle;
    }

    private sealed class PinnedMethod(string name, MethodSignature<TypeForm> signature)
    {
        public string Name { get; } = name;
        public MethodSignature<TypeForm> Signature { get; } = signature;
    }

    private readonly struct ResolvedToken
    {
        public TokenKind Kind { get; private init; }
        public string? Name { get; private init; }
        public TypeForm? Parent { get; private init; }
        public MethodSignature<TypeForm> Signature { get; private init; }

        public static ResolvedToken NotAMethod() => new() { Kind = TokenKind.NotMethod };
        public static ResolvedToken Unresolved() => new() { Kind = TokenKind.Unresolved };

        public static ResolvedToken Method(string name, TypeForm parent, MethodSignature<TypeForm> signature) =>
            new() { Kind = TokenKind.Method, Name = name, Parent = parent, Signature = signature };
    }

    private readonly struct Located
    {
        public string? Failure { get; private init; }
        public string? Text { get; private init; }
        public static Located Fail(string failure) => new() { Failure = failure };
        public static Located At(string text) => new() { Text = text };
    }

    private readonly struct OpenedOrFailure
    {
        public string? Failure { get; private init; }
        public OpenedAssembly? Assembly { get; private init; }
        public static OpenedOrFailure Fail(string failure) => new() { Failure = failure };
        public static OpenedOrFailure Ok(OpenedAssembly assembly) => new() { Assembly = assembly };
    }

    private sealed class OpenedAssembly(
        string assemblyName,
        PEReader pe,
        MetadataReaderProvider pdbProvider,
        MetadataReader reader,
        MetadataReader pdb) : IDisposable
    {
        public string AssemblyName { get; } = assemblyName;
        public PEReader Pe { get; } = pe;
        public MetadataReader Reader { get; } = reader;
        public MetadataReader Pdb { get; } = pdb;
        private readonly MetadataReaderProvider _pdbProvider = pdbProvider;

        public void Dispose()
        {
            _pdbProvider.Dispose();
            Pe.Dispose();
        }
    }

    private abstract record TypeForm
    {
        public sealed record Prim(string Name) : TypeForm;
        public sealed record Named(string Assembly, string FullName) : TypeForm;
        public sealed record Inst(TypeForm Open, ImmutableArray<TypeForm> Args) : TypeForm;
        public sealed record Var(int Index) : TypeForm;
        public sealed record MVar(int Index) : TypeForm;
        public sealed record SzArray(TypeForm Element) : TypeForm;
        public sealed record MdArray(TypeForm Element, int Rank) : TypeForm;
        public sealed record ByRef(TypeForm Element) : TypeForm;
        public sealed record Pointer(TypeForm Element) : TypeForm;
    }

    private sealed class TypeDecoder(string assembly) : ISignatureTypeProvider<TypeForm, object?>
    {
        private readonly string _assembly = assembly;

        public TypeForm GetPrimitiveType(PrimitiveTypeCode typeCode) => new TypeForm.Prim(typeCode.ToString());

        public TypeForm GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new TypeForm.Named(_assembly, TypeFullName(metadataReader, handle));

        public TypeForm GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var typeRef = metadataReader.GetTypeReference(handle);
            return new TypeForm.Named(AssemblyOf(metadataReader, typeRef), FullNameOf(metadataReader, typeRef));
        }

        public TypeForm GetSZArrayType(TypeForm elementType) => new TypeForm.SzArray(elementType);

        public TypeForm GetArrayType(TypeForm elementType, ArrayShape shape) => new TypeForm.MdArray(elementType, shape.Rank);

        public TypeForm GetByReferenceType(TypeForm elementType) => new TypeForm.ByRef(elementType);

        public TypeForm GetPointerType(TypeForm elementType) => new TypeForm.Pointer(elementType);

        public TypeForm GetGenericInstantiation(TypeForm genericType, ImmutableArray<TypeForm> typeArguments) =>
            new TypeForm.Inst(genericType, typeArguments);

        public TypeForm GetGenericTypeParameter(object? genericContext, int index) => new TypeForm.Var(index);

        public TypeForm GetGenericMethodParameter(object? genericContext, int index) => new TypeForm.MVar(index);

        public TypeForm GetModifiedType(TypeForm modifier, TypeForm unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeForm GetPinnedType(TypeForm elementType) => elementType;

        public TypeForm GetFunctionPointerType(MethodSignature<TypeForm> signature) => new TypeForm.Prim("fnptr");

        public TypeForm GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private string AssemblyOf(MetadataReader metadataReader, TypeReference typeRef)
        {
            var scope = typeRef.ResolutionScope;
            return scope.Kind switch
            {
                HandleKind.AssemblyReference => metadataReader.GetString(
                    metadataReader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                HandleKind.TypeReference => AssemblyOf(metadataReader, metadataReader.GetTypeReference((TypeReferenceHandle)scope)),
                _ => _assembly,
            };
        }

        private static string FullNameOf(MetadataReader metadataReader, TypeReference typeRef)
        {
            var name = metadataReader.GetString(typeRef.Name);
            if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                var parent = metadataReader.GetTypeReference((TypeReferenceHandle)typeRef.ResolutionScope);
                return FullNameOf(metadataReader, parent) + "/" + name;
            }

            var ns = metadataReader.GetString(typeRef.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
    }
}
