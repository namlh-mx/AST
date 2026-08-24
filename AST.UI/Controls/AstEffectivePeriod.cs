using System.Windows;
using System.Windows.Controls;

namespace AST.Controls;

// Shared effective-period block: composes two AstDateBox atoms (From / To) + a "Không xác định" checkbox
// that opens the end. IsUndetermined is the explicit discriminator the checkbox drives (BindsTwoWayByDefault):
// IsUndetermined == true means the OPEN END (persisted as 9999-12-31 at the data layer, never shown to the
// user); IsUndetermined == false AND To == null means a genuinely MISSING To (the VM must BLOCK, spec §1.4)
// -- the two are no longer conflated (2026-07-23 rebuild fixes the original To==null-means-both bug). A typed
// To with IsUndetermined == false is a concrete inclusive end. Templated Control (parts model) purely to wire
// the checkbox<->To interlock in OnApplyTemplate; date parsing/masking itself now lives entirely in
// AstDateBox (PART_FromBox / PART_ToBox), so this control does no text parsing at all. The control performs
// NO validation (no range check, no From<=To) -- the ViewModel owns all rules. No Themes/Generic.xaml -- same
// keyed-style convention as AstField / AstDateBox.
[TemplatePart(Name = PartFromBox, Type = typeof(AstDateBox))]
[TemplatePart(Name = PartToBox, Type = typeof(AstDateBox))]
[TemplatePart(Name = PartUndeterminedCheck, Type = typeof(CheckBox))]
public class AstEffectivePeriod : Control
{
    private const string PartFromBox = "PART_FromBox";
    private const string PartToBox = "PART_ToBox";
    private const string PartUndeterminedCheck = "PART_UndeterminedCheck";

    private AstDateBox? _fromBox;
    private AstDateBox? _toBox;
    private CheckBox? _undeterminedCheck;

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateOnly?), typeof(AstEffectivePeriod),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    // Start day; null = not yet entered.
    public DateOnly? From
    {
        get => (DateOnly?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateOnly?), typeof(AstEffectivePeriod),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    // End day when IsUndetermined == false. Read this ONLY together with IsUndetermined (see class remarks):
    // IsUndetermined => open end (9999-12-31); !IsUndetermined && To == null => MISSING (BLOCK); else concrete.
    public DateOnly? To
    {
        get => (DateOnly?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public static readonly DependencyProperty IsUndeterminedProperty = DependencyProperty.Register(
        nameof(IsUndetermined), typeof(bool), typeof(AstEffectivePeriod),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsUndeterminedChanged));

    // The "Không xác định" checkbox state -- the explicit discriminator (see class remarks). Checking it
    // clears + disables To (open end); unchecking re-enables To for entry.
    public bool IsUndetermined
    {
        get => (bool)GetValue(IsUndeterminedProperty);
        set => SetValue(IsUndeterminedProperty, value);
    }

    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateOnly), typeof(AstEffectivePeriod), new PropertyMetadata(default(DateOnly)));

    // Business "today" (IBusinessDateProvider.Today), forwarded loss-lessly to both AstDateBox children's
    // Today DP so their calendar-glyph navigate-to-Today-when-empty behaviour works. Never DateTime.Now.
    public DateOnly Today
    {
        get => (DateOnly)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    public static readonly DependencyProperty IsFromEnabledProperty = DependencyProperty.Register(
        nameof(IsFromEnabled), typeof(bool), typeof(AstEffectivePeriod),
        new PropertyMetadata(true, OnIsFromEnabledChanged));

    // When false, PART_FromBox is disabled (Close mode: only To is editable). Default true.
    public bool IsFromEnabled
    {
        get => (bool)GetValue(IsFromEnabledProperty);
        set => SetValue(IsFromEnabledProperty, value);
    }

    public static readonly DependencyProperty IsUndeterminedEnabledProperty = DependencyProperty.Register(
        nameof(IsUndeterminedEnabled), typeof(bool), typeof(AstEffectivePeriod),
        new PropertyMetadata(true, OnIsUndeterminedEnabledChanged));

    // When false, PART_UndeterminedCheck is disabled (Close mode: end date must stay determined).
    public bool IsUndeterminedEnabled
    {
        get => (bool)GetValue(IsUndeterminedEnabledProperty);
        set => SetValue(IsUndeterminedEnabledProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _fromBox = GetTemplateChild(PartFromBox) as AstDateBox;
        _toBox = GetTemplateChild(PartToBox) as AstDateBox;
        _undeterminedCheck = GetTemplateChild(PartUndeterminedCheck) as CheckBox;

        ApplyFromEnabledState();
        ApplyUndeterminedEnabledState();
        ApplyUndeterminedState();
    }

    private static void OnIsUndeterminedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AstEffectivePeriod)d).ApplyUndeterminedState();

    private static void OnIsFromEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AstEffectivePeriod)d).ApplyFromEnabledState();

    private static void OnIsUndeterminedEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AstEffectivePeriod)d).ApplyUndeterminedEnabledState();

    private void ApplyFromEnabledState()
    {
        if (_fromBox is null) return;
        _fromBox.IsEnabled = IsFromEnabled;
    }

    private void ApplyUndeterminedEnabledState()
    {
        if (_undeterminedCheck is null) return;
        _undeterminedCheck.IsEnabled = IsUndeterminedEnabled;
    }

    private void ApplyUndeterminedState()
    {
        if (_toBox is null) return;

        _toBox.IsEnabled = !IsUndetermined;
        if (IsUndetermined) To = null; // open end; user never sees 9999-12-31 (persisted at the data layer)
    }
}
