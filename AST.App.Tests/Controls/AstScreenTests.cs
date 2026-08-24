using System.Windows.Input;
using AST.Controls;
using AST.Core.Presentation;
using Wpf.Ui.Controls;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstScreen: each of the 5 passthrough DPs' default and a set/get round-trip.
// Defaults are read straight off the DP metadata (no instance needed, so these stay on the default MTA worker
// thread). The round-trips construct an AstScreen, and a WPF FrameworkElement ctor requires STA, so those
// bodies run through Sta.Run — mirroring AstFieldTests. The composed visual (header/status band/body slot and
// the TemplateBinding passthroughs) is the Tier-2 requester F5 gate, not covered here.
public class AstScreenTests
{
    [Fact]
    public void Title_default_is_null()
        => Assert.Null(AstScreen.TitleProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void Icon_default_is_empty()
        => Assert.Equal(SymbolRegular.Empty, (SymbolRegular)AstScreen.IconProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void BackCommand_default_is_null()
        => Assert.Null(AstScreen.BackCommandProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void Message_default_is_null()
        => Assert.Null(AstScreen.MessageProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void Severity_default_is_none()
        => Assert.Equal(StatusSeverity.None, (StatusSeverity)AstScreen.SeverityProperty.DefaultMetadata.DefaultValue!);

    [Fact]
    public void Title_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var screen = new AstScreen { Title = "Connection declaration" };
        Assert.Equal("Connection declaration", screen.Title);
    });

    [Fact]
    public void Icon_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var screen = new AstScreen { Icon = SymbolRegular.Database24 };
        Assert.Equal(SymbolRegular.Database24, screen.Icon);
    });

    [Fact]
    public void BackCommand_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        ICommand command = new StubCommand();
        var screen = new AstScreen { BackCommand = command };
        Assert.Same(command, screen.BackCommand);
    });

    [Fact]
    public void Message_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var screen = new AstScreen { Message = "Saved successfully" };
        Assert.Equal("Saved successfully", screen.Message);
    });

    [Fact]
    public void Severity_getter_returns_what_the_setter_set() => Sta.Run(() =>
    {
        var screen = new AstScreen { Severity = StatusSeverity.Success };
        Assert.Equal(StatusSeverity.Success, screen.Severity);
    });

    private sealed class StubCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
