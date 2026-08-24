using System.Windows;
using System.Windows.Controls;
using AST.Core.Presentation;

namespace AST.Controls;

// Editable shows the selectable ComboBox; Display shows DisplayText as static read-only content. The
// caller (View/VM) decides what DisplayText means (root copy, locked parent name, ...) -- the control
// stays display-only with no business semantics, per its LOCKED shared-component contract.
public enum AstOrgUnitPickerMode
{
    Editable,
    Display,
}

// Shared org-unit selector (Screen A parent; later User / Representative). Display-only: the VM supplies
// the already-EP-filtered candidate list (N2) and reads back SelectedOrgUnitId -- the picker does NO
// filtering and NO DB access. Lookless Control (no code-behind); default look = the keyed Style
// x:Key="AstOrgUnitPicker" in Controls.xaml (no Themes/Generic.xaml), same convention as AstField.
// Unifies the former 3-way overlay (root label / locked text / editable picker) into one control: Mode
// switches between the native ComboBox (Editable) and a static text display (Display) so a mode change
// never swaps which control is visible -- eliminating the field-height jump that pattern caused.
public class AstOrgUnitPicker : Control
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<OrgUnitPickerItem>), typeof(AstOrgUnitPicker),
        new PropertyMetadata(null));

    // The already-filtered candidate parents (VM supplies; N2 eligibility done in the VM).
    public IEnumerable<OrgUnitPickerItem>? Items
    {
        get => (IEnumerable<OrgUnitPickerItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty SelectedOrgUnitIdProperty = DependencyProperty.Register(
        nameof(SelectedOrgUnitId), typeof(long?), typeof(AstOrgUnitPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    // The chosen parent's org_unit.id; null = none selected.
    public long? SelectedOrgUnitId
    {
        get => (long?)GetValue(SelectedOrgUnitIdProperty);
        set => SetValue(SelectedOrgUnitIdProperty, value);
    }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(AstOrgUnitPickerMode), typeof(AstOrgUnitPicker),
        new PropertyMetadata(AstOrgUnitPickerMode.Display));

    public AstOrgUnitPickerMode Mode
    {
        get => (AstOrgUnitPickerMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
        nameof(DisplayText), typeof(string), typeof(AstOrgUnitPicker),
        new PropertyMetadata(string.Empty));

    // Static content shown in Display mode; the caller decides what it means.
    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }
}
