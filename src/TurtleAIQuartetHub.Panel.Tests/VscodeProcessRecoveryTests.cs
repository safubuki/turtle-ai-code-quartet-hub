using System.ComponentModel;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class VscodeProcessRecoveryTests
{
    public const int Count = 14;

    public static async Task RunAsync(List<string> failures)
    {
        VerifyExecutableScope(failures);
        await VerifyScanScopeAsync(failures);
        await VerifyConfirmedTargetsOnlyAsync(failures);
        await VerifyIdentityChangesAsync(failures);
        await VerifyAlreadyExitedAsync(failures);
        await VerifyFailuresAndTimeoutAsync(failures);
        await VerifyScanFailureAsync(failures);
    }

    private static void VerifyExecutableScope(List<string> failures)
    {
        Check(failures, "正式な VS Code と Insiders の exe だけを対象にする",
            VscodeProcessRecoveryService.IsSupportedExecutable("Code", @"C:\VSCode\Code.exe")
            && VscodeProcessRecoveryService.IsSupportedExecutable("Code - Insiders", @"C:\VSCode\Code - Insiders.exe")
            && VscodeProcessRecoveryService.IsSupportedExecutable("code", @"C:\VSCode\CODE.EXE"));
        Check(failures, "Antigravity・Codium・node・shell と不一致の exe を除外する",
            !VscodeProcessRecoveryService.IsSupportedExecutable("Antigravity", @"C:\IDE\Antigravity.exe")
            && !VscodeProcessRecoveryService.IsSupportedExecutable("Codium", @"C:\IDE\Codium.exe")
            && !VscodeProcessRecoveryService.IsSupportedExecutable("node", @"C:\VSCode\node.exe")
            && !VscodeProcessRecoveryService.IsSupportedExecutable("cmd", @"C:\Windows\cmd.exe")
            && !VscodeProcessRecoveryService.IsSupportedExecutable("Code", @"C:\IDE\Other.exe")
            && !VscodeProcessRecoveryService.IsSupportedExecutable("Code", "Code.exe"));
    }

    private static async Task VerifyScanScopeAsync(List<string> failures)
    {
        var access = new FakeAccess();
        access.Add(Info(1));
        access.Add(Info(2) with { UserSid = "S-1-other-user" });
        access.Add(Info(3) with { SessionId = 8 });
        access.Add(Info(4) with { ProcessName = "node", ExecutablePath = @"C:\VSCode\node.exe" });
        access.Add(Info(5)).ReadFailure = new Win32Exception(5);
        var scan = await Service(access).ScanAsync();
        Check(failures, "現在ユーザー・同じ Windows セッションの VS Code だけを列挙する",
            scan.Processes.Select(item => item.ProcessId).SequenceEqual([1]));
        Check(failures, "取得権限のないプロセスは対象外にし、取得失敗を通知する",
            scan.Issues.Count == 1 && scan.Issues[0].Contains("PID 5"));
        Check(failures, "確認用の一覧取得だけではプロセスを終了しない",
            access.States.Values.All(state => state.KillCalls == 0));
    }

    private static async Task VerifyConfirmedTargetsOnlyAsync(List<string> failures)
    {
        var access = new FakeAccess();
        var first = access.Add(Info(10));
        var confirmed = await Service(access).ScanAsync();
        var later = access.Add(Info(11));
        var result = await Service(access).TerminateAsync(confirmed.Processes);
        Check(failures, "確認した対象だけを終了し、確認後に起動した VS Code は残留として報告する",
            first.KillCalls == 1 && later.KillCalls == 0 && result.TerminatedCount == 1
            && result.RemainingScan.Processes.Select(item => item.ProcessId).SequenceEqual([11])
            && result.HasRemainingProcesses);
        Check(failures, "VS Code の子にある AI・ターミナル等のプロセスツリーを終了しない",
            first.RequestedEntireTree is false);

        var empty = await Service(access).TerminateAsync([]);
        Check(failures, "空の確認対象から再検出したプロセスを自動終了しない",
            empty.Results.Count == 0 && later.KillCalls == 0 && empty.HasRemainingProcesses);
    }

    private static async Task VerifyIdentityChangesAsync(List<string> failures)
    {
        var access = new FakeAccess();
        var originals = Enumerable.Range(20, 5).Select(id => Info(id)).ToArray();
        access.Add(originals[0] with { StartTimeUtc = originals[0].StartTimeUtc.AddSeconds(1) });
        access.Add(originals[1] with { ExecutablePath = @"C:\OtherInstall\Code.exe" });
        access.Add(originals[2] with { SessionId = 7 });
        access.Add(originals[3] with { UserSid = "S-1-other-user" });
        access.Add(originals[4] with { ProcessName = "Code - Insiders", ExecutablePath = @"C:\IDE\Code - Insiders.exe" });
        var result = await Service(access).TerminateAsync(originals);
        Check(failures, "PID 再利用・別実行ファイル・別セッション・別ユーザー・別種別を終了しない",
            result.Results.All(item => item.Status == VscodeProcessRecoveryStatus.IdentityChanged)
            && access.States.Values.All(state => state.KillCalls == 0));

        var forged = Info(25) with { UserSid = "S-1-other-user" };
        var otherUser = access.Add(forged);
        var forgedResult = await Service(access).TerminateAsync([forged]);
        Check(failures, "呼び出し元が対象外の識別情報を渡しても別ユーザーを終了しない",
            forgedResult.Results[0].Status == VscodeProcessRecoveryStatus.IdentityChanged && otherUser.KillCalls == 0);
    }

    private static async Task VerifyAlreadyExitedAsync(List<string> failures)
    {
        var access = new FakeAccess();
        var info = Info(30);
        var result = await Service(access).TerminateAsync([info, info]);
        Check(failures, "確認後に自然終了した対象は成功に準じて報告し、PID 重複を除く",
            result.AlreadyExitedCount == 1 && result.Results.Count == 1 && result.FailedCount == 0);
    }

    private static async Task VerifyFailuresAndTimeoutAsync(List<string> failures)
    {
        var access = new FakeAccess();
        var denied = access.Add(Info(40));
        denied.KillFailure = new Win32Exception(5);
        var hanging = access.Add(Info(41));
        hanging.ExitAfterKill = false;
        var success = access.Add(Info(42));
        var before = DateTime.UtcNow;
        var result = await Service(access).TerminateAsync([denied.Info, hanging.Info, success.Info]);
        Check(failures, "権限拒否と未終了を個別に報告し、他対象の終了を継続する",
            result.Results[0].Status == VscodeProcessRecoveryStatus.AccessDenied
            && result.Results[1].Status == VscodeProcessRecoveryStatus.TimedOut
            && result.Results[2].Status == VscodeProcessRecoveryStatus.Terminated
            && result.FailedCount == 2 && result.TerminatedCount == 1);
        Check(failures, "終了待機を時間で打ち切り、残留プロセスを再検出する",
            DateTime.UtcNow - before < TimeSpan.FromSeconds(3)
            && result.RemainingScan.Processes.Select(item => item.ProcessId).Order().SequenceEqual([40, 41]));
    }

    private static async Task VerifyScanFailureAsync(List<string> failures)
    {
        var access = new FakeAccess { EnumerationFailure = new Win32Exception(5) };
        var scan = await Service(access).ScanAsync();
        Check(failures, "一覧取得そのものの失敗をプロセスなしと区別して通知する",
            scan.Processes.Count == 0 && scan.Issues.Count == 1 && scan.Issues[0].Contains("一覧を取得できません"));
    }

    private static VscodeProcessInfo Info(int id) => new(id, "Code", @"C:\VSCode\Code.exe",
        new DateTime(2026, 11, 12, 0, 0, id, DateTimeKind.Utc), 2, "S-1-current-user", "");

    private static VscodeProcessRecoveryService Service(FakeAccess access) => new(access, TimeSpan.FromMilliseconds(40));

    private static void Check(List<string> failures, string name, bool condition)
    {
        if (!condition) failures.Add($"VS Code 終了・復旧: {name}");
    }

    private sealed class FakeAccess : IVscodeRecoveryProcessAccess
    {
        public int SessionId => 2;
        public string UserSid => "S-1-current-user";
        public Dictionary<int, FakeState> States { get; } = [];
        public Exception? EnumerationFailure { get; init; }

        public FakeState Add(VscodeProcessInfo info)
        {
            var state = new FakeState(info);
            States[info.ProcessId] = state;
            return state;
        }

        public IReadOnlyList<int> EnumerateProcessIds()
        {
            if (EnumerationFailure is not null) throw EnumerationFailure;
            return States.Values.Where(state => !state.Exited).Select(state => state.Info.ProcessId).ToArray();
        }

        public IVscodeRecoveryProcess? Open(int processId) => States.TryGetValue(processId, out var state) ? new FakeProcess(state) : null;
    }

    private sealed class FakeState(VscodeProcessInfo info)
    {
        public VscodeProcessInfo Info { get; } = info;
        public bool Exited { get; set; }
        public bool ExitAfterKill { get; set; } = true;
        public int KillCalls { get; set; }
        public bool? RequestedEntireTree { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? KillFailure { get; set; }
    }

    private sealed class FakeProcess(FakeState state) : IVscodeRecoveryProcess
    {
        public bool HasExited => state.Exited;
        public VscodeProcessInfo ReadInfo()
        {
            if (state.ReadFailure is not null) throw state.ReadFailure;
            return state.Info;
        }

        public void Kill(bool entireProcessTree)
        {
            state.KillCalls++;
            state.RequestedEntireTree = entireProcessTree;
            if (state.KillFailure is not null) throw state.KillFailure;
            if (state.ExitAfterKill) state.Exited = true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => state.Exited
            ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public void Dispose() { }
    }
}
