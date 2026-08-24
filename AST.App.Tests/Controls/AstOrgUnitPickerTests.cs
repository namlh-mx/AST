using AST.Controls;
using AST.Core.Presentation;

namespace AST.App.Tests.Controls;

// Tier-1 headless coverage of AstOrgUnitPicker: DP defaults + round-trips. The dropdown template (ComboBox
// display of "code — short name", enable/disable wiring) is a separate task (the keyed style in Controls.xaml)
// and the Tier-2 requester F5 gate -- not covered here.
public class AstOrgUnitPickerTests
{
    [Fact]
    public void Items_default_is_null()
        => Assert.Null(AstOrgUnitPicker.ItemsProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void SelectedOrgUnitId_default_is_null()
        => Assert.Null(AstOrgUnitPicker.SelectedOrgUnitIdProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void Items_round_trips() => Sta.Run(() =>
    {
        var items = new[] { new OrgUnitPickerItem(1, "HO — Hội sở"), new OrgUnitPickerItem(2, "CN1 — Chi nhánh 1") };
        var picker = new AstOrgUnitPicker { Items = items };
        Assert.Same(items, picker.Items);
    });

    [Fact]
    public void SelectedOrgUnitId_round_trips() => Sta.Run(() =>
    {
        var picker = new AstOrgUnitPicker { SelectedOrgUnitId = 42 };
        Assert.Equal(42, picker.SelectedOrgUnitId);
    });

    [Fact]
    public void Mode_default_is_Display()
        => Assert.Equal(AstOrgUnitPickerMode.Display, AstOrgUnitPicker.ModeProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void Mode_round_trips() => Sta.Run(() =>
    {
        var picker = new AstOrgUnitPicker { Mode = AstOrgUnitPickerMode.Editable };
        Assert.Equal(AstOrgUnitPickerMode.Editable, picker.Mode);
    });

    [Fact]
    public void DisplayText_default_is_empty()
        => Assert.Equal(string.Empty, AstOrgUnitPicker.DisplayTextProperty.DefaultMetadata.DefaultValue);

    [Fact]
    public void DisplayText_round_trips() => Sta.Run(() =>
    {
        var picker = new AstOrgUnitPicker { DisplayText = "HO — Hội sở" };
        Assert.Equal("HO — Hội sở", picker.DisplayText);
    });

    [Fact]
    public void PickerItem_carries_id_and_display()
    {
        var item = new OrgUnitPickerItem(7, "X — Y");
        Assert.Equal(7, item.Id);
        Assert.Equal("X — Y", item.Display);
    }
}
