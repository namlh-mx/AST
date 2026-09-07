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

namespace AST.App.Tests.Views;

// Card 273 step 5 + card 276: supplemental draft reaches dirty through the View's DraftChanged
// forwarder (ToDto → MarkSupplementalDirty) without committing Supplemental. Full View/Dialog
// XAML cannot InitializeComponent under OffscreenHost, so this drives the internal forwarder.
// The discard-confirmed close transition is covered in Shell ViewModel tests (card 276); the
// source-text pin of the handler body was deleted there as misleading (backlog 3.63 remains open).
public class OrgUnitSupplementalDraftDirtyTests
{
    private const string RevertToEntryStateMessage =
        "Thông tin đang khai báo không thay đổi so với thông tin hiện có của đơn vị.";

    [Fact]
    public async Task SupplementalDraftForwarder_EditWithoutSaving_KeepsFormDirty_RevertClearsDirtyAndShowsSentence()
    {
        var today = new DateOnly(2026, 7, 24);
        var vm = BuildViewModel(today);
        (await vm.LoadAsync(1, today)).Should().Be(CardLoadOutcome.Loaded);
        vm.BeginEditCommand.Execute();
        vm.IsDirty.Should().BeFalse();

        var draft = SupplementalDraft.FromDto(vm.Supplemental);
        draft.Phone = "0909123456";
        OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, draft.ToDto());

        vm.IsDirty.Should().BeTrue();
        vm.HasUnsavedInput.Should().BeTrue("leave gate must still protect an unsaved overlay draft");
        vm.Supplemental.Should().Be(new OrgUnitSupplementalDto(),
            "DraftChanged must not commit Supplemental");

        draft.Phone = string.Empty;
        OrgUnitDeclarationView.ForwardSupplementalDraftChanged(vm, draft.ToDto());

        vm.IsDirty.Should().BeFalse();
        vm.HasUnsavedInput.Should().BeFalse();
        vm.Severity.Should().Be(StatusSeverity.Info);
        vm.StatusMessage.Should().Be(RevertToEntryStateMessage);
    }

    private static OrgUnitDeclarationViewModel BuildViewModel(DateOnly today)
    {
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
            Supplemental: new OrgUnitSupplementalDto(),
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
