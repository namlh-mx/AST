using AST.Controls;
using AST.Core.Presentation;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstVersionStatus: Status default + round-trip. The template (text + brush via the
// converters, hidden when None) is a separate task (the keyed style in Controls.xaml) and the Tier-2 requester
// F5 gate -- not covered here.
public class AstVersionStatusTests
{
    [Fact]
    public void Status_default_is_None()
        => Assert.Equal(VersionStatus.None, (VersionStatus)AstVersionStatus.StatusProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Status_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var label = new AstVersionStatus { Status = VersionStatus.Effective };
        Assert.Equal(VersionStatus.Effective, label.Status);
    });
}
