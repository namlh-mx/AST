using AST.Core.Startup;
using AST.Core.Presentation;

namespace AST.Shell.Tests.Presentation;

public class StartupModePresentationTests
{
    [Theory]
    [InlineData(StartupMode.Connected, "AstConnectedBrush", true)]
    [InlineData(StartupMode.NotConnected, "AstNotConnectedBrush", false)]
    public void Maps_mode_to_brush_key_and_bool(StartupMode mode, string brushKey, bool isConnected)
    {
        Assert.Equal(brushKey, StartupModePresentation.BrushKey(mode));
        Assert.Equal(isConnected, StartupModePresentation.IsConnected(mode));
    }
}
