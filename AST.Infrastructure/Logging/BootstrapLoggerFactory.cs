using AST.Core;
using Serilog;
using ILogger = Serilog.ILogger;

namespace AST.Infrastructure.Logging;

// Initializes the Serilog file sink for the Shell startup chain (rule-platform-infra §⑧.4: technical logs are
// written LOCALLY at %LOCALAPPDATA%\AST\logs\, NOT to the network share). Wrapped FAIL-SAFE at the startup
// BOUNDARY: if creating the directory/sink fails (permissions, contention, the path taken by another file...) it
// must NOT be thrown and crash the app (rule-platform-infra invariant #1) -- falls back to a NO-sink logger (no-op,
// subsequent Log.* calls can still be made safely, they simply do not write to a file). Serilog itself catches
// runtime sink errors when EMITTING an event (see the Serilog Reliability docs), but Directory.CreateDirectory here
// is OUR OWN code so it can still throw -- hence a separate try/catch is needed, not relying entirely on Serilog.
[SharedComponent]
public static class BootstrapLoggerFactory
{
    public static ILogger Create(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            return new LoggerConfiguration()
                .WriteTo.File(
                    Path.Combine(logDirectory, "ast-.log"),
                    rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }
        catch (Exception)
        {
            // Fail-safe fallback: a logger with no sink -- the app keeps running, StartupStatus still displays
            // correctly via IStartupState even though technical logs cannot be written to a file.
            return new LoggerConfiguration().CreateLogger();
        }
    }
}
