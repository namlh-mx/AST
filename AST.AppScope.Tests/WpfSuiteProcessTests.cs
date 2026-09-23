using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;

namespace AST.AppScope.Tests;

// Each suite owns the per-process WPF Application singleton. This fact is the guard for THIS suite's
// process. AST.App.Tests is not edited by this task, so it does not host the mirror fact.
public class WpfSuiteProcessTests
{
    internal const string AppScopeSuite = "AST.AppScope.Tests";
    internal const string AppTestsSuite = "AST.App.Tests";

    internal static string? SharedHostViolation(IEnumerable<string?> loadedSimpleNames)
    {
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in loadedSimpleNames)
        {
            if (name is not null)
            {
                loaded.Add(name);
            }
        }

        if (loaded.Contains(AppScopeSuite) && loaded.Contains(AppTestsSuite))
        {
            return AppScopeSuite + " and " + AppTestsSuite
                + " are both loaded in this process. Each owns the per-process WPF Application singleton and must not share a test host process.";
        }

        return null;
    }

    internal static IEnumerable<string?> AssembliesLoadedInThisProcess()
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            foreach (var assembly in context.Assemblies)
            {
                yield return assembly.GetName().Name;
            }
        }
    }

    [Fact]
    public void ThisProcessDoesNotLoadAstAppTests() =>
        SharedHostViolation(AssembliesLoadedInThisProcess()).Should().BeNull();

    [Fact]
    public void LoadingAstAppTestsAssemblyFailsTheSameDetection()
    {
        var path = OtherSuiteAssemblyPath();
        File.Exists(path).Should().BeTrue(
            "the falsifier loads the built AST.App.Tests assembly by path, at {0}", path);

        var weakContext = LoadOtherSuiteAndDetect(path);

        for (var attempt = 0; attempt < 10 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        weakContext.IsAlive.Should().BeFalse(
            "the other suite was loaded in a collectible AssemblyLoadContext and must not remain for the rest of the run");
        AssembliesLoadedInThisProcess().Should().NotContain(AppTestsSuite);
        SharedHostViolation(AssembliesLoadedInThisProcess()).Should().BeNull();
    }

    private static WeakReference LoadOtherSuiteAndDetect(string path)
    {
        var context = new CollectibleSuiteContext();
        var weakContext = new WeakReference(context);
        var loaded = context.LoadFromAssemblyPath(path);
        loaded.GetName().Name.Should().Be(AppTestsSuite);
        SharedHostViolation(AssembliesLoadedInThisProcess()).Should().NotBeNullOrWhiteSpace();
        context.Unload();
        return weakContext;
    }

    private static string OtherSuiteAssemblyPath()
    {
        var suiteDir = Path.GetDirectoryName(typeof(WpfSuiteProcessTests).Assembly.Location)
            ?? throw new InvalidOperationException("This suite's assembly has no location.");
        var tfm = Path.GetFileName(suiteDir);
        var configuration = Path.GetFileName(Path.GetDirectoryName(suiteDir));
        var repoRoot = Path.GetFullPath(Path.Combine(suiteDir, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "AST.App.Tests", "bin", configuration!, tfm, "AST.App.Tests.dll");
    }

    private sealed class CollectibleSuiteContext : AssemblyLoadContext
    {
        public CollectibleSuiteContext()
            : base(isCollectible: true)
        {
        }

        // Dependencies bind in the default context. Only the suite assembly passed to
        // LoadFromAssemblyPath lives here, so Unload can drop that assembly.
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
