using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AST.Controls;
using AST.Core.Presentation;
using Serilog;
using Wpf.Ui;

namespace AST.Views.Iam.OrgUnit;

// Screen-A local supplemental catalog body (spec 2.4). Hosted as an overlay on OrgUnitDeclarationView.
// Leave-dirty confirm is asked by the host through ConfirmLeaveAsync() (shared LeaveGate).
// 3→2 cấp confirm also lives here (view-layer; SupplementalDraft stays dialog-free).
// Field lock/unlock is bindable Draft.FieldsLocked — same DataTrigger/binding shape as the main card.
public partial class OrgUnitSupplementalDialog : UserControl
{
    public SupplementalDraft Draft { get; private set; } = new();

    // Set by the host before showing — needed for §2.4 3→2 confirm.
    public IContentDialogService? Dialogs { get; set; }

    public event EventHandler? CloseRequested;
    public event EventHandler? DraftSaved;
    public event EventHandler? DraftChanged;

    private bool _isDirty;
    private bool _allowUnlock = true;
    private System.ComponentModel.PropertyChangedEventHandler? _draftHandler;

    public OrgUnitSupplementalDialog()
    {
        InitializeComponent();
        AttachDraft(Draft);
        Loaded += OnLoaded;
    }

    // lockFields: start with fields disabled (view). allowUnlock: whether [Sửa] may re-enable fields.
    public void LoadDraft(SupplementalDraft seed, bool lockFields, bool allowUnlock)
    {
        AttachDraft(seed.Clone());
        Draft.FieldsLocked = lockFields;
        _allowUnlock = allowUnlock;
        _isDirty = false;
        if (SaveButton is not null) SaveButton.IsEnabled = false;
        if (EditButton is not null) EditButton.IsEnabled = lockFields && allowUnlock;
    }

    public bool IsDirtyUnlocked => _isDirty && !Draft.FieldsLocked;
    public bool IsDraftLocked => Draft.FieldsLocked;

    private void AttachDraft(SupplementalDraft draft)
    {
        if (_draftHandler is not null)
            Draft.PropertyChanged -= _draftHandler;
        Draft = draft;
        DataContext = Draft;
        _draftHandler = OnDraftChanged;
        Draft.PropertyChanged += _draftHandler;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (SaveButton is not null) SaveButton.IsEnabled = false;
        if (EditButton is not null) EditButton.IsEnabled = Draft.FieldsLocked && _allowUnlock;
    }

    private void OnDraftChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Draft.FieldsLocked) return;
        _isDirty = true;
        if (SaveButton is not null) SaveButton.IsEnabled = true;
        if (e.PropertyName is nameof(SupplementalDraft.FieldsLocked) or nameof(SupplementalDraft.DistrictEnabled))
            return;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    // §2.4: 3→2 with district data → confirm then clear VN+EN; empty district → apply, no confirm.
    // Checked (not PreviewMouse) so keyboard Space / arrow-group nav share the same path as mouse:
    // ButtonBase.OnClick (mouse or keyboard) → RadioButton.OnToggle → IsChecked=true → Checked.
    private async void OnLevel2Checked(object sender, RoutedEventArgs e)
    {
        if (Draft.FieldsLocked || Draft.AdminDivisionLevel == 2)
            return;

        var hasDistrict = !string.IsNullOrWhiteSpace(Draft.AddrDistrictVn)
            || !string.IsNullOrWhiteSpace(Draft.AddrDistrictEn);

        if (!hasDistrict)
        {
            Draft.AdminDivisionLevel = 2;
            return;
        }

        if (Dialogs is null)
        {
            Log.Error("Supplemental 3→2 confirm skipped: Dialogs not set on OrgUnitSupplementalDialog.");
            ResyncLevelRadiosFromDraft();
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await AstDialog.ConfirmAsync(
                Dialogs,
                "Chuyển sang 2 cấp sẽ xóa dữ liệu Quận/huyện (VN và EN). Tiếp tục?",
                StatusSeverity.Warning,
                "Tiếp tục",
                Wpf.Ui.Controls.SymbolRegular.Checkmark24,
                "Hủy",
                Wpf.Ui.Controls.SymbolRegular.Dismiss24);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show 3→2 cấp confirm");
            ResyncLevelRadiosFromDraft();
            return;
        }

        if (!confirmed)
        {
            // Toggle already committed IsChecked=true on "2 cấp" and unchecked "3 cấp" via
            // RadioButton.OnChecked → UpdateRadioButtonGroup. OneWay binding alone does not snap
            // back; Uncheck also does NOT re-check the sibling (group only unchecks on check).
            // UpdateTarget both radios from AdminDivisionLevel (still 3).
            ResyncLevelRadiosFromDraft();
            return;
        }

        Draft.AddrDistrictVn = string.Empty;
        Draft.AddrDistrictEn = string.Empty;
        Draft.AdminDivisionLevel = 2;
    }

    // 2→3: unconditional sync. Required because IsLevel3 is OneWay get-only — without writing
    // AdminDivisionLevel=3 the UI would show "3 cấp" while the draft stayed at 2 (same divergence
    // class as the keyboard bypass on 3→2). No confirm gate per §2.4.
    private void OnLevel3Checked(object sender, RoutedEventArgs e)
    {
        if (Draft.FieldsLocked || Draft.AdminDivisionLevel == 3)
            return;

        Draft.AdminDivisionLevel = 3;
    }

    private void ResyncLevelRadiosFromDraft()
    {
        BindingOperations.GetBindingExpression(Level2Radio, ToggleButton.IsCheckedProperty)?.UpdateTarget();
        BindingOperations.GetBindingExpression(Level3Radio, ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Draft.FieldsLocked = true;
        _isDirty = false;
        if (SaveButton is not null) SaveButton.IsEnabled = false;
        if (EditButton is not null) EditButton.IsEnabled = _allowUnlock;
        DraftSaved?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (!_allowUnlock) return;
        Draft.FieldsLocked = false;
        if (SaveButton is not null) SaveButton.IsEnabled = false;
        if (EditButton is not null) EditButton.IsEnabled = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
