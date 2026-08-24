using AST.Controls;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstField: each DP's default and a set/get round-trip. Defaults are read
// straight off the DP metadata (no instance needed, so these stay on the default MTA worker thread).
// The round-trips construct an AstField, and a WPF FrameworkElement ctor requires STA, so those bodies
// run through Sta.Run — mirroring SpacingTests. The visual template (label/helper/error, the HasError
// trigger) is the Tier-2 requester F5 gate, not covered here.
public class AstFieldTests
{
    [Fact]
    public void Label_default_is_empty_string()
        => Assert.Equal(string.Empty, (string)AstField.LabelProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Helper_default_is_empty_string()
        => Assert.Equal(string.Empty, (string)AstField.HelperProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Error_default_is_empty_string()
        => Assert.Equal(string.Empty, (string)AstField.ErrorProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void HasError_default_is_false()
        => Assert.False((bool)AstField.HasErrorProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Label_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var field = new AstField { Label = "Server address" };
        Assert.Equal("Server address", field.Label);
    });

    [Fact]
    public void Helper_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var field = new AstField { Helper = "Host or IP of the MySQL server" };
        Assert.Equal("Host or IP of the MySQL server", field.Helper);
    });

    [Fact]
    public void Error_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var field = new AstField { Error = "Server address is required" };
        Assert.Equal("Server address is required", field.Error);
    });

    [Fact]
    public void HasError_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var field = new AstField { HasError = true };
        Assert.True(field.HasError);
    });
}
