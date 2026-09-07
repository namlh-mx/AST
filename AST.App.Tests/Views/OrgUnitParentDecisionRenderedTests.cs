using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AST.Controls;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation;
using AST.Shell.ViewModels.Iam;
using FluentAssertions;
using Moq;
using UiButton = Wpf.Ui.Controls.Button;

namespace AST.App.Tests.Views;

public class OrgUnitParentDecisionRenderedTests
{
    [Fact]
    public void ReplacePeriodCommit_RendersHeldParentAndDisablesSaveUntilDelayedDecisionResolves()
    {
        var today = new DateOnly(2026, 9, 6);
        Task<IReadOnlyList<OrgUnitPickerItem>> currentEligibility =
            Task.FromResult<IReadOnlyList<OrgUnitPickerItem>>([new OrgUnitPickerItem(1, "PAR — Cha")]);
        var delayed = new TaskCompletionSource<IReadOnlyList<OrgUnitPickerItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        OffscreenHost.Run(
            window =>
            {
                window.Width = 900;
                window.Height = 400;
                var vm = BuildViewModel(today, () => currentEligibility);
                vm.LoadAsync(3, today).GetAwaiter().GetResult().Should().Be(CardLoadOutcome.Loaded);
                vm.BeginReplaceCommand.Execute();
                return new ParentRowHost(vm, window.Resources);
            },
            (window, row) =>
            {
                try
                {
                    currentEligibility = delayed.Task;
                    row.Period.SetCurrentValue(AstEffectivePeriod.IsUndeterminedProperty, false);
                    Sta.PumpToIdle();

                    AssertHeldParentRendered(row, ParentEligibilityState.Incomplete);

                    row.Period.SetCurrentValue(AstEffectivePeriod.ToProperty, today.AddDays(10));
                    Sta.PumpToIdle();

                    AssertHeldParentRendered(row, ParentEligibilityState.Loading);

                    delayed.SetResult([new OrgUnitPickerItem(1, "PAR — Cha")]);
                    Sta.PumpToIdle();
                    window.UpdateLayout();

                    row.ViewModel.ParentDecision.Phase.Should().Be(ParentEligibilityState.Resolved);
                    row.ViewModel.ParentDecision.Presentation.Should().Be(ParentPresentationDisposition.Editable);
                    row.ViewModel.ParentDecision.CommitDisposition.Should().Be(ParentCommitDisposition.Allowed);
                    row.Save.IsEnabled.Should().BeTrue();
                    row.Picker.ApplyTemplate();
                    var combo = (FrameworkElement)row.Picker.Template.FindName("EditableComboBox", row.Picker)!;
                    combo.Visibility.Should().Be(Visibility.Visible);
                }
                finally
                {
                    row.Dispose();
                }
            });
    }

    private static void AssertHeldParentRendered(ParentRowHost row, ParentEligibilityState phase)
    {
        row.ViewModel.ParentDecision.Phase.Should().Be(phase);
        row.ViewModel.ParentId.Should().Be(1);
        row.ViewModel.ParentDecision.DisplayItems.Should().Contain(item => item.Id == 1);
        row.Save.IsEnabled.Should().BeFalse();

        row.Picker.ApplyTemplate();
        var display = (TextBox)row.Picker.Template.FindName("DisplayTextBox", row.Picker)!;
        display.Visibility.Should().Be(Visibility.Visible);
        display.Text.Should().Be("PAR — Cha");
    }

    private static OrgUnitDeclarationViewModel BuildViewModel(
        DateOnly today,
        Func<Task<IReadOnlyList<OrgUnitPickerItem>>> currentEligibility)
    {
        var repository = new Mock<IOrgUnitRepository>();
        repository.Setup(repo => repo.GetByIdentityAsync(3, today)).ReturnsAsync(Dto(today));
        repository.Setup(repo => repo.GetEligibleParentsAsync(
                It.IsAny<DataScope>(), It.IsAny<EffectivePeriod>(), It.IsAny<long?>()))
            .Returns(currentEligibility);

        var dates = new Mock<IBusinessDateProvider>();
        dates.SetupGet(provider => provider.Today).Returns(today);
        var currentUser = new Mock<ICurrentWindowsUser>();
        currentUser.SetupGet(user => user.Username).Returns("tester");
        return new OrgUnitDeclarationViewModel(
            repository.Object,
            Mock.Of<IOrgUnitDeclarationService>(),
            dates.Object,
            currentUser.Object,
            Mock.Of<IAuthorizationService>(),
            Mock.Of<IConfirmationPrompt>(),
            Mock.Of<IBreakGlassPolicy>());
    }

    private static OrgUnitVersionDto Dto(DateOnly today) => new(
        Id: 30,
        OrgUnitId: 3,
        EffectiveFrom: today.AddDays(-10),
        EffectiveTo: EffectivePeriod.OpenEnd,
        IsActive: true,
        OrgCode: "CN001",
        OrgNameFullVn: "Chi nhánh một",
        OrgNameShortVn: "CN1",
        ParentId: 1,
        RecordedAt: DateTime.UtcNow,
        RecordedBy: "tester",
        Reason: null,
        Supplemental: new OrgUnitSupplementalDto(),
        Status: VersionLifecycleStatus.Normal,
        ParentOrgCodeAsOf: "PAR",
        ParentOrgNameFullVnAsOf: "Cha");

    // Real app controls/styles and bindings, hosted in a shown off-screen Window. The production View's
    // renderer is deliberately tiny and identical: it maps only the decision's two projection properties.
    private sealed class ParentRowHost : StackPanel, IDisposable
    {
        public ParentRowHost(OrgUnitDeclarationViewModel viewModel, ResourceDictionary resources)
        {
            ViewModel = viewModel;
            Orientation = Orientation.Vertical;

            Period = new AstEffectivePeriod { Style = (Style)resources["AstEffectivePeriod"] };
            BindingOperations.SetBinding(Period, AstEffectivePeriod.IsUndeterminedProperty, new Binding(nameof(viewModel.IsUndetermined))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
            });
            BindingOperations.SetBinding(Period, AstEffectivePeriod.FromProperty, new Binding(nameof(viewModel.EffectiveFrom))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
            });
            BindingOperations.SetBinding(Period, AstEffectivePeriod.ToProperty, new Binding(nameof(viewModel.EffectiveTo))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
            });

            Picker = new AstOrgUnitPicker { Style = (Style)resources["AstOrgUnitPicker"] };
            BindingOperations.SetBinding(Picker, AstOrgUnitPicker.ItemsProperty, new Binding(nameof(viewModel.ParentPickerItems))
            {
                Source = viewModel,
            });
            BindingOperations.SetBinding(Picker, AstOrgUnitPicker.SelectedOrgUnitIdProperty, new Binding(nameof(viewModel.ParentId))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
            });

            Save = new UiButton { Content = "Lưu", Command = viewModel.SaveCommand };
            Children.Add(Period);
            Children.Add(Picker);
            Children.Add(Save);

            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyDecision();
        }

        public OrgUnitDeclarationViewModel ViewModel { get; }
        public AstEffectivePeriod Period { get; }
        public AstOrgUnitPicker Picker { get; }
        public UiButton Save { get; }

        public void Dispose() => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.ParentDecision))
                ApplyDecision();
        }

        private void ApplyDecision()
        {
            var decision = ViewModel.ParentDecision;
            Picker.Mode = decision.Presentation == ParentPresentationDisposition.Editable
                ? AstOrgUnitPickerMode.Editable
                : AstOrgUnitPickerMode.Display;
            Picker.DisplayText = decision.DisplayText;
        }
    }
}
