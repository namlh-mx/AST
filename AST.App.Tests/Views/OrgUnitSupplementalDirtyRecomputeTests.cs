using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AST.Controls;
using AST.Converters;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation;
using AST.Shell.ViewModels.Iam;
using AST.Views.Iam.OrgUnit;
using FluentAssertions;
using Moq;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace AST.App.Tests.Views;

// C7 dirty latch, N2 whitespace (four observables together), N4 reopen-after-B7.
public class OrgUnitSupplementalDirtyRecomputeTests
{
    [Fact]
    public void Unlock_with_no_edit_stays_clean()
        => OffscreenHost.Run(CreateDialog, (_, dialog) =>
        {
            dialog.LoadDraft(new SupplementalDraft(), lockFields: true, allowUnlock: true);
            Click(dialog.EditButton);

            dialog.IsDirtyUnlocked.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
        });

    [Fact]
    public void One_field_changed_then_reverted_is_clean()
        => OffscreenHost.Run(CreateDialog, (window, dialog) =>
        {
            dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
            var vm = BuildLoadedEditingViewModel();
            var phone = Editor(dialog, 0);
            phone.Focus();
            Sta.PumpToIdle();
            phone.Text = "0909123456";
            dialog.IsDirtyUnlocked.Should().BeTrue();
            dialog.SaveButton.IsEnabled.Should().BeTrue();
            OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, dialog.Draft.ToDto());
            vm.IsDirty.Should().BeTrue();

            BubblingKey.Raise(phone, Key.Escape);
            Sta.PumpToIdle();

            phone.Text.Should().BeEmpty();
            dialog.Draft.BusinessNumber.Should().BeEmpty();
            dialog.IsDirtyUnlocked.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
            OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, dialog.Draft.ToDto());
            vm.IsDirty.Should().BeFalse();
        });

    [Fact]
    public void Two_fields_changed_and_one_reverted_stays_dirty()
        => OffscreenHost.Run(CreateDialog, (_, dialog) =>
        {
            dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
            var first = Editor(dialog, 0);
            var second = Editor(dialog, 1);
            first.Focus();
            Sta.PumpToIdle();
            first.Text = "one";
            second.Focus();
            Sta.PumpToIdle();
            second.Text = "two";
            BubblingKey.Raise(second, Key.Escape);

            first.Text.Should().Be("one");
            second.Text.Should().BeEmpty();
            dialog.IsDirtyUnlocked.Should().BeTrue();
            dialog.SaveButton.IsEnabled.Should().BeTrue();
        });

    [Fact]
    public void Save_then_unlock_with_no_further_edit_is_clean()
        => OffscreenHost.Run(CreateDialog, (_, dialog) =>
        {
            dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
            Editor(dialog, 0).Text = "0909123456";
            Click(dialog.SaveButton);
            dialog.IsDirtyUnlocked.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();

            Click(dialog.EditButton);

            dialog.IsDirtyUnlocked.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.Draft.BusinessNumber.Should().Be("0909123456");
        });

    [Fact]
    public void Leading_trailing_whitespace_on_lost_focus_trims_and_stays_clean_on_all_four_observables()
        => OffscreenHost.Run(CreateDialog, (_, dialog) =>
        {
            var seed = new SupplementalDraft { Phone = "0123456789" };
            dialog.LoadDraft(seed, lockFields: false, allowUnlock: true);
            var vm = BuildLoadedEditingViewModel(new OrgUnitSupplementalDto(Phone: "0123456789"));
            var phone = FindPhone(dialog);
            phone.Focus();
            Sta.PumpToIdle();
            phone.Text = "  0123456789  ";

            phone.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, phone));
            Sta.PumpToIdle();
            OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, dialog.Draft.ToDto());

            phone.Text.Should().Be("0123456789", "N2 field text");
            dialog.Draft.Phone.Should().Be("0123456789", "N2 draft value");
            dialog.IsDirtyUnlocked.Should().BeFalse("N2 local dirty");
            dialog.SaveButton.IsEnabled.Should().BeFalse("N2 Save");
            vm.IsDirty.Should().BeFalse("N2 parent dirty");
        });

    [Fact]
    public void Whitespace_only_on_lost_focus_empties_the_field_and_stays_clean_on_all_four_observables()
        => OffscreenHost.Run(CreateDialog, (_, dialog) =>
        {
            dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
            var vm = BuildLoadedEditingViewModel();
            var phone = FindPhone(dialog);
            phone.Focus();
            Sta.PumpToIdle();
            phone.Text = "   ";

            phone.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, phone));
            Sta.PumpToIdle();
            OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, dialog.Draft.ToDto());

            phone.Text.Should().BeEmpty("N2 field text");
            dialog.Draft.Phone.Should().BeEmpty("N2 draft value");
            dialog.IsDirtyUnlocked.Should().BeFalse("N2 local dirty");
            dialog.SaveButton.IsEnabled.Should().BeFalse("N2 Save");
            vm.IsDirty.Should().BeFalse("N2 parent dirty");
        });

    [Fact]
    public void B7_reopen_does_not_resurrect_raw_whitespace()
        => OffscreenHost.Run(
            window =>
            {
                var dialog = CreateDialog(window);
                return new AstOverlayHost { Content = dialog };
            },
            (_, host) =>
            {
                var dialog = (OrgUnitSupplementalDialog)host.Content;
                dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
                host.IsOpen = true;
                Sta.PumpToIdle();
                var phone = FindPhone(dialog);
                phone.Focus();
                Sta.PumpToIdle();
                phone.Text = "  raw  ";
                var closes = 0;
                host.CloseRequested += (_, _) => closes++;

                host.IsOpen = false;
                Sta.PumpToIdle();
                closes.Should().Be(0, "B7: programmatic close raises no CloseRequested");

                dialog.LoadDraft(new SupplementalDraft(), lockFields: false, allowUnlock: true);
                host.IsOpen = true;
                Sta.PumpToIdle();

                FindPhone(dialog).Text.Should().NotBe("  raw  ", "N4: reopen must not resurrect raw whitespace");
                FindPhone(dialog).Text.Should().BeEmpty();
            });

    private static OrgUnitSupplementalDialog CreateDialog(Window window)
    {
        // Dialog BAML resolves StaticResource against Application.Resources. Point that
        // dictionary at the OffscreenHost window's already-loaded 7-entry merge first.
        _ = window.FindResource("AstLabelMediumText");
        _ = window.FindResource("AstFieldLabel");
        Application.Current.Resources = window.Resources;
        if (!window.Resources.Contains("InverseBool"))
            window.Resources["InverseBool"] = new InverseBooleanConverter();
        return new OrgUnitSupplementalDialog();
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static UiTextBox Editor(OrgUnitSupplementalDialog dialog, int index)
    {
        var editors = Walk(dialog).OfType<UiTextBox>().Where(box => box.IsEnabled).ToList();
        return editors[index];
    }

    private static UiTextBox FindPhone(OrgUnitSupplementalDialog dialog) =>
        Walk(dialog).OfType<UiTextBox>().First(box => box.PlaceholderText == "Điện thoại");

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        if (count == 0)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Walk(child))
                    yield return nested;
            }

            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Walk(child))
                yield return nested;
        }
    }

    private static OrgUnitDeclarationViewModel BuildLoadedEditingViewModel(
        OrgUnitSupplementalDto? supplemental = null)
    {
        var vm = BuildViewModel(supplemental ?? new OrgUnitSupplementalDto());
        vm.LoadAsync(1, new DateOnly(2026, 7, 24)).GetAwaiter().GetResult();
        vm.BeginEditCommand.Execute();
        return vm;
    }

    private static OrgUnitDeclarationViewModel BuildViewModel(OrgUnitSupplementalDto? supplemental = null)
    {
        var today = new DateOnly(2026, 7, 24);
        var repository = new Mock<IOrgUnitRepository>();
        repository.Setup(repo => repo.GetByIdentityAsync(1, today)).ReturnsAsync(new OrgUnitVersionDto(
            Id: 77,
            OrgUnitId: 1,
            EffectiveFrom: today.AddDays(-10),
            EffectiveTo: EffectivePeriod.OpenEnd,
            IsActive: true,
            OrgCode: "ABCD",
            OrgNameFullVn: "Đơn vị đầy đủ",
            OrgNameShortVn: "Đơn vị",
            ParentId: 5,
            RecordedAt: DateTime.UtcNow,
            RecordedBy: "tester",
            Reason: null,
            Supplemental: supplemental ?? new OrgUnitSupplementalDto(),
            Status: VersionLifecycleStatus.Normal,
            ParentOrgCodeAsOf: "PAR",
            ParentOrgNameFullVnAsOf: "Cha"));
        repository.Setup(repo => repo.GetInScopeAsync(It.IsAny<DataScope>(), It.IsAny<DateOnly>()))
            .ReturnsAsync(Array.Empty<OrgUnitVersionDto>());
        repository.Setup(repo => repo.GetHistoryInScopeAsync(It.IsAny<DataScope>(), It.IsAny<long?>()))
            .ReturnsAsync(Array.Empty<OrgUnitVersionDto>());

        var dates = new Mock<IBusinessDateProvider>();
        dates.SetupGet(provider => provider.Today).Returns(today);
        var currentUser = new Mock<ICurrentWindowsUser>();
        currentUser.SetupGet(user => user.Username).Returns("tester");
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DataScope(ScopeLevel.Global, null, "tester"));

        return new OrgUnitDeclarationViewModel(
            repository.Object,
            Mock.Of<IOrgUnitDeclarationService>(),
            dates.Object,
            currentUser.Object,
            auth.Object,
            Mock.Of<IConfirmationPrompt>(),
            Mock.Of<IBreakGlassPolicy>());
    }
}
