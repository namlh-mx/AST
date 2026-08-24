using AST.Core.Iam;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AST.Core.Tests.Iam;

public class FunctionRegistryTests
{
    [Fact]
    public void Register_ThenFind_ReturnsSameDescriptor()
    {
        IFunctionRegistry registry = new FunctionRegistry();
        var descriptor = new FunctionDescriptor(
            "IAM.User.Create", "FX001", "Tạo người dùng", MenuGroupCodes.ConfigSecurity,
            "IAM/UserCreate", "IAM.User.Create", 1);

        registry.Register(descriptor);

        Assert.Same(descriptor, registry.Find("IAM.User.Create"));
        Assert.Single(registry.All);
    }

    [Fact]
    public void Register_SameKeyTwice_IsIdempotent_LatestWins()
    {
        IFunctionRegistry registry = new FunctionRegistry();
        var first = new FunctionDescriptor(
            "IAM.User.Create", "FX001", "Tạo người dùng", MenuGroupCodes.ConfigSecurity,
            "IAM/UserCreate", "IAM.User.Create", 1);
        var second = first with { DisplayName = "Tạo người dùng mới" };

        registry.Register(first);
        registry.Register(second);

        Assert.Single(registry.All);
        Assert.Equal("Tạo người dùng mới", registry.Find("IAM.User.Create")!.DisplayName);
    }

    [Fact]
    public void Find_UnknownKey_ReturnsNull()
    {
        IFunctionRegistry registry = new FunctionRegistry();

        Assert.Null(registry.Find("Unknown.Key"));
    }

    // Regression for the "plug-in functions never appear on the sidebar" bug: a Register() call that
    // happens BEFORE Seal() (i.e. before the sidebar is built) must be silent -- no diagnostic, it is exactly
    // the expected/normal path (a module registering during Prism's InitializeModules()).
    [Fact]
    public void Register_BeforeSeal_LogsNothing_AndIsFound()
    {
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var registry = new FunctionRegistry();
            var descriptor = new FunctionDescriptor(
                "IAM.User.Create", "FX001", "Tạo người dùng", MenuGroupCodes.ConfigSecurity,
                "IAM/UserCreate", "IAM.User.Create", 1);

            registry.Register(descriptor);

            sink.Events.Should().BeEmpty();
            registry.Find("IAM.User.Create").Should().BeSameAs(descriptor);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // Acceptance criterion 3: a Register() call reaching the registry AFTER Seal() (the composition root calls
    // it once all modules + the app's own registrations are done, see AST/App.xaml.cs OnInitialized) must
    // Log.Error exactly once naming the function key, and must NOT throw -- edge/bootstrap code degrades to a
    // clear diagnostic, never a crash, over what is ultimately a cosmetic (menu) concern.
    [Fact]
    public void Register_AfterSeal_LogsErrorNamingFunctionKey_AndDoesNotThrow()
    {
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var registry = new FunctionRegistry();
            registry.Seal();
            var descriptor = new FunctionDescriptor(
                "Late.Function.Register", "FX099", "Đăng ký muộn", MenuGroupCodes.ConfigSecurity,
                "Late/Target", "Late.Function.Register", 1);

            var act = () => registry.Register(descriptor);

            act.Should().NotThrow();
            sink.Events.Should().ContainSingle();
            sink.Events[0].Level.Should().Be(LogEventLevel.Error);
            sink.Events[0].RenderMessage().Should().Contain("Late.Function.Register");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // A late registration is logged as a diagnostic, but the data itself is still kept -- Find/All (used by
    // permission checks, not just the sidebar) must not silently lose the function just because it missed the
    // menu-build window.
    [Fact]
    public void Register_AfterSeal_StillStoresDescriptor()
    {
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(new CapturingSink()).CreateLogger();
        try
        {
            var registry = new FunctionRegistry();
            registry.Seal();
            var descriptor = new FunctionDescriptor(
                "Late.Function.Store", "FX098", "Lưu muộn", MenuGroupCodes.ConfigSecurity,
                "Late/Target2", "Late.Function.Store", 1);

            registry.Register(descriptor);

            registry.Find("Late.Function.Store").Should().BeSameAs(descriptor);
            registry.All.Should().Contain(descriptor);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
