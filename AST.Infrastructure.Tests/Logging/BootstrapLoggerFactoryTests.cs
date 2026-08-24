using AST.Infrastructure.Logging;

namespace AST.Infrastructure.Tests.Logging;

public class BootstrapLoggerFactoryTests
{
    [Fact]
    public void Create_with_writable_directory_returns_usable_logger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ast-log-ok-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = BootstrapLoggerFactory.Create(dir);

            Assert.NotNull(logger);
            var ex = Record.Exception(() => logger.Information("test"));
            Assert.Null(ex);
            (logger as IDisposable)?.Dispose(); // release the file sink before cleaning up the temp directory
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Create_when_directory_creation_fails_falls_back_without_throwing()
    {
        // Forces Directory.CreateDirectory to throw: uses a path that collides with the name of an existing FILE (a
        // directory cannot be created with the same name as a file) -- deterministically simulates an "unwritable path",
        // independent of real OS permissions.
        var parent = Path.Combine(Path.GetTempPath(), "ast-log-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var blockedPath = Path.Combine(parent, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        try
        {
            var ex = Record.Exception(() =>
            {
                var logger = BootstrapLoggerFactory.Create(blockedPath);
                Assert.NotNull(logger);
                // Fail-safe fallback: the no-op logger must still be callable safely, without throwing.
                logger.Information("test");
            });

            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
