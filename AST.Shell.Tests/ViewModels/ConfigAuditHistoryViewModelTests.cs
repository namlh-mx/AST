using AST.Core.Security;
using AST.Core.Presentation;
using AST.Shell.Session;
using AST.Shell.ViewModels.Platform;
using ErrorOr;
using FluentAssertions;

namespace AST.Shell.Tests.ViewModels;

public class ConfigAuditHistoryViewModelTests
{
    private sealed class FakeLog : IConfigAuditLog
    {
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> ReadResult = Array.Empty<ConfigAuditRecord>();
        public ErrorOr<ConfigAuditIntegrity> IntegrityResult = new ConfigAuditIntegrity(true, null, true);

        public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase) => Result.Success;
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read() => ReadResult;
        public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity() => IntegrityResult;
    }

    private static ConfigAuditRecord Record(int seq, string target, string action, ConfigAuditDiff? diff, string? tipSig)
        => new(
            new ConfigAuditContent(seq, "2026-07-12T08:00:00Z", new ConfigAuditActor("boss", "PC01"),
                target, action, diff, "Success", null, tipSig is null ? null : "abcd", ConfigAuditChain.GenesisPrevHash),
            Hash: "hash" + seq, TipSig: tipSig);

    private static AdminSession Authed()
    {
        var s = new AdminSession();
        s.Authenticate(new byte[] { 9 }, "pw");
        return s;
    }

    private static ConfigAuditHistoryViewModel Vm(FakeLog log, IAdminSession? session = null)
        => new(log, session ?? Authed());

    [Fact]
    public void Load_projects_records_into_rows()
    {
        var log = new FakeLog
        {
            ReadResult = new[]
            {
                Record(1, "FileB", "Create", null, null),
                Record(2, "FileB", "Update", new ConfigAuditDiff(new[] { "boss2" }, Array.Empty<string>()), "sig2"),
            },
        };
        var vm = Vm(log);

        vm.LoadCommand.Execute();

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("Người cứu hộ", vm.Rows[0].Target);
        Assert.Equal("Tạo", vm.Rows[0].Operation); // no-diff record -> single row, action as operation
        Assert.Equal("—", vm.Rows[0].User);
        Assert.False(vm.Rows[0].Signed);
        Assert.Equal("Thêm", vm.Rows[1].Operation); // diff row, one per added user
        Assert.Equal("boss2", vm.Rows[1].User);
        Assert.True(vm.Rows[1].Signed);
    }

    [Fact]
    public void Load_splits_one_save_into_one_row_per_user()
    {
        var log = new FakeLog
        {
            ReadResult = new[]
            {
                Record(1, "FileB", "Update",
                    new ConfigAuditDiff(new[] { "a", "b" }, new[] { "c" }), "sig1"),
            },
        };
        var vm = Vm(log);

        vm.LoadCommand.Execute();

        Assert.Equal(3, vm.Rows.Count); // +a, +b, -c
        Assert.Equal(("Thêm", "a"), (vm.Rows[0].Operation, vm.Rows[0].User));
        Assert.Equal(("Thêm", "b"), (vm.Rows[1].Operation, vm.Rows[1].User));
        Assert.Equal(("Xóa", "c"), (vm.Rows[2].Operation, vm.Rows[2].User));
    }

    [Fact]
    public void Load_maps_target_and_action_to_vietnamese_labels()
    {
        var log = new FakeLog
        {
            ReadResult = new[]
            {
                Record(1, "FileA", "Create", null, null),
                Record(2, "FileB", "Update", new ConfigAuditDiff(new[] { "boss2" }, Array.Empty<string>()), "sig2"),
                Record(3, "FileA", "SignatureVerifyFailed", null, null),
            },
        };
        var vm = Vm(log);

        vm.LoadCommand.Execute();

        Assert.Equal("Thông số kết nối", vm.Rows[0].Target);
        Assert.Equal("Tạo", vm.Rows[0].Operation);
        Assert.Equal("Người cứu hộ", vm.Rows[1].Target);
        Assert.Equal("Thêm", vm.Rows[1].Operation); // File B update with an added user
        Assert.Equal("boss2", vm.Rows[1].User);
        Assert.Equal("Lỗi xác minh chữ ký", vm.Rows[2].Operation);
    }

    [Fact]
    public void Verify_intact_chain_reports_the_final_success_message()
    {
        var log = new FakeLog { IntegrityResult = new ConfigAuditIntegrity(true, null, true) };
        var vm = Vm(log);

        vm.VerifyCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Equal("Nhật ký nguyên vẹn, chữ ký hợp lệ.", vm.IntegrityMessage);
    }

    [Fact]
    public void Verify_broken_chain_reports_error_naming_the_sequence()
    {
        var log = new FakeLog { IntegrityResult = new ConfigAuditIntegrity(false, 3, true) };
        var vm = Vm(log);

        vm.VerifyCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        vm.IntegrityMessage.Should().Be("Nhật ký cấu hình không toàn vẹn.");
    }

    [Fact]
    public void Verify_invalid_tip_signature_reports_the_final_error_message()
    {
        var log = new FakeLog { IntegrityResult = new ConfigAuditIntegrity(true, null, false) };
        var vm = Vm(log);

        vm.VerifyCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal("Chữ ký không hợp lệ.", vm.IntegrityMessage);
    }

    [Fact]
    public void Verify_surfaces_a_read_or_verify_error()
    {
        var log = new FakeLog { IntegrityResult = ConfigErrors.IoError("nhật ký cấu hình") };
        var vm = Vm(log);

        vm.VerifyCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    [Fact]
    public void Authenticating_the_session_auto_loads_and_verifies_history()
    {
        var session = new AdminSession();
        var log = new FakeLog { ReadResult = new[] { Record(1, "FileB", "Create", null, null) } };
        var vm = Vm(log, session);
        Assert.Empty(vm.Rows);

        session.Authenticate(new byte[] { 1 }, "pw");

        Assert.Single(vm.Rows);
        Assert.Equal(StatusSeverity.Success, vm.Severity); // verify ran on the intact chain
    }

    [Fact]
    public void Ending_the_session_clears_history()
    {
        var session = Authed();
        var log = new FakeLog { ReadResult = new[] { Record(1, "FileB", "Create", null, null) } };
        var vm = Vm(log, session);
        vm.LoadCommand.Execute();
        Assert.Single(vm.Rows);

        session.Clear();

        Assert.Empty(vm.Rows);
    }
}
