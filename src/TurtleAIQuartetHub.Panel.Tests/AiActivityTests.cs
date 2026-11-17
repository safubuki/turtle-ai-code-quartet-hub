using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// AI フックの通知から決まる作業状態（作業中・完了・解除）、スロットの特定、フック設定の足し引き、多重パイプ受信を確認する。
internal static class AiActivityTests
{
    public const int Count = 68;

    public static async Task RunAsync(List<string> failures)
    {
        VerifyParser(failures);
        VerifyTrackerTransitions(failures);
        VerifyWorkingElapsed(failures);
        VerifyCompletionRestore(failures);
        VerifySlotMatching(failures);
        VerifyInstaller(failures);
        await VerifyPipeConcurrencyAsync(failures);
    }

    private static void VerifyParser(List<string> failures)
    {
        var start = AiHookEventParser.TryParse(["--ai-event", "Claude", "UserPromptSubmit", @"C:\work\a", "s1", ""]);
        Check(failures, "通知: Claude の UserPromptSubmit は開始",
            start is { Tool: "claude", Kind: AiHookEventKind.Start, SessionId: "s1" });
        Check(failures, "通知: Copilot の userPromptSubmitted / agentStop を開始・完了として扱う",
            AiHookEventParser.Classify("userPromptSubmitted", "") == AiHookEventKind.Start
            && AiHookEventParser.Classify("agentStop", "") == AiHookEventKind.Stop);
        Check(failures, "通知: 許可や質問の確認は表示を変えない",
            AiHookEventParser.Classify("Notification", "permission_prompt") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("Notification", "elicitation_dialog") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("PermissionRequest", "") == AiHookEventKind.Unknown);
        Check(failures, "通知: 入力待ち（idle_prompt）は光らせない中断として扱う（Esc / Ctrl+C の中断で緑を残さない）",
            AiHookEventParser.Classify("Notification", "idle_prompt") == AiHookEventKind.Interrupt);
        Check(failures, "通知: ツールの前後（古い版で登録した PreToolUse / PostToolUse）は表示を変えない",
            AiHookEventParser.Classify("preToolUse", "") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("PostToolUse", "") == AiHookEventKind.Unknown);
        Check(failures, "通知: Grok の失敗・中断は完了と同じ扱い、拒否と task_complete は無視",
            AiHookEventParser.Classify("StopFailure", "") == AiHookEventKind.Stop
            && AiHookEventParser.Classify("StopCancelled", "") == AiHookEventKind.Stop
            && AiHookEventParser.TryParse(["--ai-event", "Grok", "StopCancelled", @"C:\work\a", "s1", ""]) is { Tool: "grok", Kind: AiHookEventKind.Stop }
            && AiHookEventParser.Classify("PermissionDenied", "") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("Notification", "task_complete") == AiHookEventKind.Unknown);
        var agStart = AiHookEventParser.TryParse(["--ai-event", "antigravity", "PreInvocation", @"C:\work\a", "conv-1", ""]);
        Check(failures, "通知: Antigravity の PreInvocation / Stop を開始・完了として扱う",
            agStart is { Tool: "antigravity", Kind: AiHookEventKind.Start, SessionId: "conv-1" }
            && AiHookEventParser.Classify("Stop", "NO_TOOL_CALL") == AiHookEventKind.Stop
            && AiHookEventParser.Classify("Stop", "ERROR") == AiHookEventKind.Stop);
        Check(failures, "通知: Antigravity の USER_CANCELED や cancel / abort 系は中断として扱う",
            AiHookEventParser.Classify("Stop", "USER_CANCELED") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("Stop", "user_canceled") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("Stop", "cancel") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("Stop", "cancelled") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("Stop", "aborted") == AiHookEventKind.Interrupt);
        Check(failures, "通知: Claude / Codex の SessionEnd・StopFailure と Copilot の中止/回復不能エラーを正しく分類する",
            AiHookEventParser.Classify("SessionEnd", "") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("sessionEnd", "") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("StopFailure", "") == AiHookEventKind.Stop
            && AiHookEventParser.Classify("agentStop", "complete") == AiHookEventKind.Stop
            && AiHookEventParser.Classify("agentStop", "abort") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("agentStop", "user_exit") == AiHookEventKind.Interrupt
            && AiHookEventParser.Classify("errorOccurred", "recoverable") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("errorOccurred", "fatal") == AiHookEventKind.Stop
            && AiHookEventParser.Classify("errorOccurred", "") == AiHookEventKind.Stop);
        var agTracker = new AiActivityTracker();
        agTracker.Apply(agStart!, DateTimeOffset.UtcNow);
        agTracker.Apply(AiHookEventParser.TryParse(["--ai-event", "antigravity", "Stop", @"C:\work\a", "conv-1", "USER_CANCELED"])!, DateTimeOffset.UtcNow);
        Check(failures, "状態: Antigravity の USER_CANCELED は偽の完了（青）を残さず作業中だけを止める",
            agTracker.State == AiActivityState.Idle);
        Check(failures, "通知: cwd が無い・別コマンドは受け付けない",
            AiHookEventParser.TryParse(["--ai-event", "claude", "Stop", ""]) is null
            && AiHookEventParser.TryParse(["--activate"]) is null);
    }

    private static void VerifyTrackerTransitions(List<string> failures)
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new AiActivityTracker();
        tracker.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        Check(failures, "状態: 指示を受けたら作業中", tracker.State == AiActivityState.Working);

        tracker.Apply(Event("claude", AiHookEventKind.Stop, "s1"), now);
        Check(failures, "状態: 完了したら光る（フォーカス表示中でも光らせる）", tracker.State == AiActivityState.Done);

        Check(failures, "状態: フォーカス表示を切り替えたら光が消えて元に戻る",
            tracker.Acknowledge() && tracker.State == AiActivityState.Idle);

        Check(failures, "状態: 光っていないときの切り替えでは何も変わらない", !tracker.Acknowledge());

        tracker.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        var permissionKeptWorking = !tracker.Apply(Event("claude", AiHookEventKind.Unknown, "s1"), now);
        Check(failures, "状態: 許可の確認では緑の作業中のまま",
            permissionKeptWorking && tracker.State == AiActivityState.Working);
        tracker.Apply(Event("claude", AiHookEventKind.Stop, "s1"), now);
        Check(failures, "状態: 青く光るのは応答が終わったときだけ", tracker.State == AiActivityState.Done);

        var multi = new AiActivityTracker();
        multi.Apply(Event("claude", AiHookEventKind.Start, "a"), now);
        multi.Apply(Event("copilot", AiHookEventKind.Start, "b"), now);
        multi.Apply(Event("copilot", AiHookEventKind.Stop, "b"), now);
        multi.Acknowledge();
        Check(failures, "状態: 同じスロットで別の AI が作業中なら作業中のまま", multi.State == AiActivityState.Working);
        multi.Apply(Event("claude", AiHookEventKind.Interrupt, "a"), now);
        Check(failures, "状態: 中断は光らせずに元へ戻す", multi.State == AiActivityState.Idle);

        var longRun = new AiActivityTracker();
        longRun.Apply(Event("copilot", AiHookEventKind.Start, "x"), now);
        longRun.Apply(Event("copilot", AiHookEventKind.Unknown, "x"), now.AddHours(3));
        Check(failures, "状態: 作業中は時間では片付けない（長い処理を途中で消さない）",
            longRun.State == AiActivityState.Working);

        VerifyStopButtonWithoutStopEvent(failures, now);

        var slotA = new WindowSlot(new SlotConfig { Name = "A" });
        var slotB = new WindowSlot(new SlotConfig { Name = "B" });
        slotA.AiTracker.Apply(Event("claude", AiHookEventKind.Start, "s"), now);
        WindowSlot.SwapAiActivity(slotA, slotB);
        Check(failures, "状態: スロットの入れ替えではワークスペースと一緒に移る",
            slotA.AiActivity == AiActivityState.Idle && slotB.AiActivity == AiActivityState.Working);
        slotB.ClearWindow();
        Check(failures, "状態: ウィンドウが閉じたら消す", slotB.AiActivity == AiActivityState.Idle);

        var rerun = new AiActivityTracker();
        rerun.Apply(Event("antigravity", AiHookEventKind.Start, "conv-1"), now);
        rerun.Apply(Event("antigravity", AiHookEventKind.Stop, "conv-1"), now);
        var movedToWorkingOnRerun = rerun.State == AiActivityState.Done
            && rerun.Apply(Event("antigravity", AiHookEventKind.Start, "conv-1"), now.AddSeconds(5))
            && rerun.State == AiActivityState.Working;
        Check(failures, "状態: 完了後にフォーカス切替せず同じツールを再実行すると完了を消して作業中へ戻る",
            movedToWorkingOnRerun);

        var replacedOrphan = new AiActivityTracker();
        replacedOrphan.Apply(Event("claude", AiHookEventKind.Start, "old-aborted"), now);
        replacedOrphan.Apply(Event("claude", AiHookEventKind.Start, "new-session"), now.AddSeconds(10));
        replacedOrphan.Apply(Event("claude", AiHookEventKind.Stop, "new-session"), now.AddSeconds(20));
        var ackClearedCleanly = replacedOrphan.State == AiActivityState.Done
            && replacedOrphan.Acknowledge()
            && replacedOrphan.State == AiActivityState.Idle;
        Check(failures, "状態: 同ツールの別セッション再開始後に完了を解除しても、古い未完了セッションで作業中へ戻らない",
            ackClearedCleanly);

        var emptySessionInterrupt = new AiActivityTracker();
        emptySessionInterrupt.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        emptySessionInterrupt.Apply(Event("claude", AiHookEventKind.Interrupt, ""), now.AddSeconds(3));
        Check(failures, "状態: SessionId が空の Interrupt でも同ツールの作業中を解除する",
            emptySessionInterrupt.State == AiActivityState.Idle);

        var mismatchedStop = new AiActivityTracker();
        mismatchedStop.Apply(Event("copilot", AiHookEventKind.Start, "prompt-id"), now);
        mismatchedStop.Apply(Event("copilot", AiHookEventKind.Interrupt, "other-id"), now.AddSeconds(5));
        var interruptCleared = mismatchedStop.State == AiActivityState.Idle;
        mismatchedStop.Apply(Event("copilot", AiHookEventKind.Start, "prompt-id"), now.AddSeconds(10));
        mismatchedStop.Apply(Event("copilot", AiHookEventKind.Stop, "other-id"), now.AddSeconds(15));
        mismatchedStop.Acknowledge();
        Check(failures, "状態: 完了・中断の SessionId が開始と違っても、同じツールの作業中を残さない",
            interruptCleared && mismatchedStop.State == AiActivityState.Idle);

        var unkeyedStartThenKeyedStop = new AiActivityTracker();
        unkeyedStartThenKeyedStop.Apply(Event("antigravity", AiHookEventKind.Start, ""), now);
        unkeyedStartThenKeyedStop.Apply(Event("antigravity", AiHookEventKind.Stop, "conv-9"), now.AddSeconds(5));
        unkeyedStartThenKeyedStop.Acknowledge();
        Check(failures, "状態: 開始時に SessionId が空で完了時に付与されても、解除後に作業中へ戻らない",
            unkeyedStartThenKeyedStop.State == AiActivityState.Idle);

        var tempStateDir = Path.Combine(Path.GetTempPath(), "turtle-reset-all-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StatusStore(new AppConfig { StateDirectory = tempStateDir });
            store.Slots[0].AiTracker.Apply(Event("antigravity", AiHookEventKind.Start, "swarm-1"), now);
            store.Slots[1].AiTracker.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
            store.Slots[1].AiTracker.Apply(Event("claude", AiHookEventKind.Stop, "s1"), now);
            var changedNames = new List<string>();
            foreach (var s in store.Slots)
            {
                s.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(WindowSlot.AiActivity))
                    {
                        changedNames.Add(s.Name);
                    }
                };
            }

            var firstReset = store.ResetAllAiActivity();
            var secondReset = store.ResetAllAiActivity();
            Check(failures, "状態: 全スロット手動リセットで作業中と完了だけを待機に戻し、再実行は 0 件になる",
                firstReset == 2
                && secondReset == 0
                && store.Slots.All(s => s.AiActivity == AiActivityState.Idle)
                && changedNames.SequenceEqual([store.Slots[0].Name, store.Slots[1].Name]),
                $"first={firstReset}, second={secondReset}, changed={string.Join(",", changedNames)}");
        }
        finally
        {
            try { Directory.Delete(tempStateDir, recursive: true); } catch (IOException) { }
        }

        // カルテットハブを再起動しても、まだ見ていない完了（青）が同じウィンドウのスロットに戻ること。
        var restartStateDir = Path.Combine(Path.GetTempPath(), "turtle-ai-done-restore-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var completedAt = new DateTimeOffset(2026, 11, 17, 5, 5, 0, TimeSpan.Zero);
            var before = new StatusStore(new AppConfig { StateDirectory = restartStateDir });
            var doneSlot = before.Slots[0];
            doneSlot.WindowHandle = new IntPtr(0x1234);
            doneSlot.SavedWorkspacePath = @"C:\work\app";
            doneSlot.SavedWorkspaceConfirmed = true;
            doneSlot.AiTracker.Apply(new AiHookEvent("codex", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work\app", "s1"), completedAt.AddMinutes(-2));
            doneSlot.AiTracker.Apply(new AiHookEvent("codex", "Stop", AiHookEventKind.Stop, @"C:\work\app", "s1"), completedAt);
            var workingSlot = before.Slots[1];
            workingSlot.WindowHandle = new IntPtr(0x5678);
            workingSlot.AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work\b", "s2"), completedAt);
            var noWindowSlot = before.Slots[2];
            noWindowSlot.AiTracker.Apply(new AiHookEvent("grok", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work\c", "s3"), completedAt);
            noWindowSlot.AiTracker.Apply(new AiHookEvent("grok", "Stop", AiHookEventKind.Stop, @"C:\work\c", "s3"), completedAt);
            before.SaveAiCompletionState();

            var after = new StatusStore(new AppConfig { StateDirectory = restartStateDir });
            var restoredDone = after.Slots[0].AiActivity == AiActivityState.Done
                && after.Slots[0].AiTracker.CompletedAt == completedAt
                && after.Slots[0].AiTracker.CompletedDuration == TimeSpan.FromMinutes(2)
                && after.Slots[0].AiElapsedText == "2:00"
                && after.Slots[0].AiTracker.LastTool == "codex"
                && after.Slots[0].AiActivityToolTip?.Contains(completedAt.ToLocalTime().ToString("M/d H:mm"), StringComparison.Ordinal) == true;
            var workingNotRestored = after.Slots[1].AiActivity == AiActivityState.Idle;
            var noWindowNotRestored = after.Slots[2].AiActivity == AiActivityState.Idle;
            var keptForSameWorkspace = !after.Slots[0].DiscardRestoredAiCompletionIfWorkspaceChanged(@"C:\work\app\src")
                && after.Slots[0].AiActivity == AiActivityState.Done;
            Check(failures, "完了の戻し: 再起動後、同じウィンドウのスロットに完了だけを時刻ごと戻す（作業中・ウィンドウ無しは戻さない）",
                restoredDone && workingNotRestored && noWindowNotRestored && keptForSameWorkspace,
                $"done={restoredDone}, working={workingNotRestored}, noWindow={noWindowNotRestored}, sameWorkspace={keptForSameWorkspace}");

            var discarded = after.Slots[0].DiscardRestoredAiCompletionIfWorkspaceChanged(@"C:\other\project")
                && after.Slots[0].AiActivity == AiActivityState.Idle;
            after.SaveAiCompletionState();
            var third = new StatusStore(new AppConfig { StateDirectory = restartStateDir });
            Check(failures, "完了の戻し: ウィンドウが別のフォルダを開いていたら戻した完了を捨て、次の再起動でも戻さない",
                discarded && third.Slots[0].AiActivity == AiActivityState.Idle);
        }
        finally
        {
            try { Directory.Delete(restartStateDir, recursive: true); } catch (IOException) { }
        }
    }

    // Copilot、VS Code 版の Claude Code、Antigravity の一部は停止ボタンや Esc で中断しても終了の知らせを送らない（Codex は Interrupt を送る）。
    // 時間では片付けず、カードの停止ボタンと、届く知らせ（idle_prompt など）で止める。
    private static void VerifyStopButtonWithoutStopEvent(List<string> failures, DateTimeOffset now)
    {
        var manual = new AiActivityTracker();
        var idleIgnored = !manual.StopWorking();
        manual.Apply(Event("copilot", AiHookEventKind.Start, "c1"), now);
        var stoppedNow = manual.StopWorking() && manual.State == AiActivityState.Idle;
        manual.Apply(Event("copilot", AiHookEventKind.Start, "c2"), now.AddSeconds(10));
        var restartedOnPrompt = manual.State == AiActivityState.Working;
        manual.Apply(Event("copilot", AiHookEventKind.Stop, "c2"), now.AddSeconds(20));
        Check(failures, "停止ボタン（カード）: 作業中だけをすぐ止め、次の指示で作業中になり、完了の青は止めない",
            idleIgnored && stoppedNow && restartedOnPrompt
            && !manual.StopWorking() && manual.State == AiActivityState.Done);

        var otherTool = new AiActivityTracker();
        otherTool.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        otherTool.Apply(Event("copilot", AiHookEventKind.Start, "c1"), now);
        Check(failures, "停止ボタン（カード）: 同じスロットで並行していたほかの AI の作業中もまとめて止める",
            otherTool.StopWorking() && otherTool.State == AiActivityState.Idle);

        var claudeIdle = new AiActivityTracker();
        claudeIdle.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        claudeIdle.Apply(AiHookEventParser.TryParse(["--ai-event", "claude", "Notification", @"C:\work", "s1", "idle_prompt"])!, now.AddSeconds(60));
        var idleStoppedWorking = claudeIdle.State == AiActivityState.Idle;
        claudeIdle.Apply(Event("claude", AiHookEventKind.Start, "s1"), now.AddSeconds(70));
        claudeIdle.Apply(Event("claude", AiHookEventKind.Stop, "s1"), now.AddSeconds(80));
        claudeIdle.Apply(AiHookEventParser.TryParse(["--ai-event", "claude", "Notification", @"C:\work", "s1", "idle_prompt"])!, now.AddSeconds(140));
        var doneKept = claudeIdle.State == AiActivityState.Done;
        claudeIdle.Acknowledge();
        claudeIdle.Apply(AiHookEventParser.TryParse(["--ai-event", "claude", "Notification", @"C:\work", "s1", "idle_prompt"])!, now.AddSeconds(200));
        Check(failures, "停止ボタン: Claude の Esc 中断後の入力待ちで緑を止め、完了の青は消さず、消した青も戻さない",
            idleStoppedWorking && doneKept && claudeIdle.State == AiActivityState.Idle);
    }

    // 作業中の経過時間の表示。表示だけに使い、時間で状態を変えないこと。
    private static void VerifyWorkingElapsed(List<string> failures)
    {
        Check(failures, "経過時間: 時計の書き方（0:42、12:05、1:02:33）にし、漢字を使わない",
            AiWorkingElapsed.Format(TimeSpan.Zero) == "0:00"
            && AiWorkingElapsed.Format(TimeSpan.FromSeconds(-3)) == "0:00"
            && AiWorkingElapsed.Format(TimeSpan.FromSeconds(42.9)) == "0:42"
            && AiWorkingElapsed.Format(TimeSpan.FromSeconds(12 * 60 + 5)) == "12:05"
            && AiWorkingElapsed.Format(TimeSpan.FromMinutes(60)) == "1:00:00"
            && AiWorkingElapsed.Format(TimeSpan.FromSeconds(3600 + 2 * 60 + 33)) == "1:02:33"
            && AiWorkingElapsed.Format(TimeSpan.FromHours(51)) == "51:00:00",
            string.Join(",", new[] { 0, 42.9, 725, 3600, 3753, 51 * 3600 }.Select(sec => AiWorkingElapsed.Format(TimeSpan.FromSeconds(sec)))));

        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var tracker = new AiActivityTracker();
        var noneBefore = tracker.WorkingSince is null;
        tracker.Apply(new AiHookEvent("antigravity", "PreInvocation", AiHookEventKind.Start, @"C:\work", "conv"), t0);
        tracker.Apply(new AiHookEvent("antigravity", "PreInvocation", AiHookEventKind.Start, @"C:\work", "conv"), t0.AddMinutes(5));
        var keptAcrossModelCalls = tracker.WorkingSince == t0;
        tracker.Apply(new AiHookEvent("antigravity", "Stop", AiHookEventKind.Stop, @"C:\work", "conv"), t0.AddMinutes(8));
        var clearedOnStop = tracker.WorkingSince is null && tracker.State == AiActivityState.Done;
        tracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work", "s1"), t0.AddMinutes(10));
        var restartedFromDone = tracker.WorkingSince == t0.AddMinutes(10);
        tracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work", "s1"), t0.AddMinutes(30));
        var resetOnNewPrompt = tracker.WorkingSince == t0.AddMinutes(30);
        tracker.Apply(new AiHookEvent("copilot", "userPromptSubmitted", AiHookEventKind.Start, @"C:\work", "c1"), t0.AddMinutes(31));
        tracker.Apply(new AiHookEvent("copilot", "agentStop", AiHookEventKind.Stop, @"C:\work", "c1"), t0.AddMinutes(32));
        var keptWhileOtherToolWorks = tracker.WorkingSince is not null;
        tracker.Acknowledge();
        var stillWorkingAfterHours = tracker.State == AiActivityState.Working;
        tracker.StopWorking();
        Check(failures, "経過時間: 作業中になった時刻から数え、モデル呼び出しごとには数え直さず、新しい指示で数え直し、止まったら消す",
            noneBefore && keptAcrossModelCalls && clearedOnStop && restartedFromDone && resetOnNewPrompt
            && keptWhileOtherToolWorks && stillWorkingAfterHours && tracker.WorkingSince is null,
            $"{noneBefore},{keptAcrossModelCalls},{clearedOnStop},{restartedFromDone},{resetOnNewPrompt},{keptWhileOtherToolWorks},{stillWorkingAfterHours}");
    }

    // まだ見ていない完了（青）の時刻と、カルテットハブの再起動後の戻し方。古さは時間で判断しない。
    private static void VerifyCompletionRestore(List<string> failures)
    {
        var t0 = new DateTimeOffset(2026, 11, 17, 5, 5, 0, TimeSpan.Zero);
        var tracker = new AiActivityTracker();
        tracker.Apply(new AiHookEvent("codex", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work", "s1"), t0);
        var noTimeWhileWorking = tracker.CompletedAt is null;
        tracker.Apply(new AiHookEvent("codex", "Stop", AiHookEventKind.Stop, @"C:\work", "s1"), t0.AddMinutes(3));
        var stampedOnStop = tracker.CompletedAt == t0.AddMinutes(3);
        tracker.Acknowledge();
        var clearedOnAcknowledge = tracker.CompletedAt is null;
        tracker.Apply(new AiHookEvent("codex", "Stop", AiHookEventKind.Stop, @"C:\work", "s1"), t0.AddMinutes(5));
        tracker.Apply(new AiHookEvent("codex", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work", "s1"), t0.AddMinutes(6));
        var clearedOnNextPrompt = tracker.CompletedAt is null;
        Check(failures, "完了の時刻: 完了で記録し、フォーカス切替や次の指示で消す",
            noTimeWhileWorking && stampedOnStop && clearedOnAcknowledge && clearedOnNextPrompt);

        var timed = new AiActivityTracker();
        timed.Apply(new AiHookEvent("copilot", "userPromptSubmitted", AiHookEventKind.Start, @"C:\work", "c1"), t0);
        timed.Apply(new AiHookEvent("copilot", "agentStop", AiHookEventKind.Stop, @"C:\work", "c1"), t0.AddSeconds(12 * 60 + 5));
        var frozen = timed.CompletedDuration == TimeSpan.FromSeconds(12 * 60 + 5) && timed.State == AiActivityState.Done;
        timed.Apply(new AiHookEvent("copilot", "userPromptSubmitted", AiHookEventKind.Start, @"C:\work", "c1"), t0.AddMinutes(20));
        var resetOnRestart = timed.CompletedDuration is null && timed.WorkingSince == t0.AddMinutes(20);
        timed.Apply(new AiHookEvent("copilot", "agentStop", AiHookEventKind.Stop, @"C:\work", "c1"), t0.AddMinutes(21));
        var secondTask = timed.CompletedDuration == TimeSpan.FromMinutes(1);
        timed.Acknowledge();
        var clearedOnFocus = timed.CompletedDuration is null;
        var unknownStart = new AiActivityTracker();
        unknownStart.Apply(new AiHookEvent("copilot", "agentStop", AiHookEventKind.Stop, @"C:\work", "c1"), t0);
        var restoredWithDuration = new AiActivityTracker();
        restoredWithDuration.RestoreCompletion("codex", t0, TimeSpan.FromSeconds(42));
        Check(failures, "かかった時間: 完了で時計を止めて残し、次の指示で 0:00 から数え直し、フォーカス切替で消す。開始を知らない完了では出さない",
            frozen && resetOnRestart && secondTask && clearedOnFocus
            && unknownStart.CompletedDuration is null
            && restoredWithDuration.CompletedDuration == TimeSpan.FromSeconds(42));

        var restored = new AiActivityTracker();
        var restoredOk = restored.RestoreCompletion("codex", t0)
            && restored.State == AiActivityState.Done
            && restored.CompletedAt == t0
            && restored.LastTool == "codex";
        var busy = new AiActivityTracker();
        busy.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"C:\work", "s1"), t0);
        var notOverWorking = !busy.RestoreCompletion("codex", t0) && busy.State == AiActivityState.Working;
        restored.Acknowledge();
        Check(failures, "完了の戻し: 再起動前の完了を時刻ごと戻し、作業中の表示は上書きせず、フォーカス切替で消える",
            restoredOk && notOverWorking && restored.State == AiActivityState.Idle);

        Check(failures, "完了の戻し: ワークスペースの比較は同じフォルダか配下なら同じ、別フォルダは別とみなす",
            AiActivitySlotMatcher.IsSameWorkspace(@"C:\work\app", @"c:/work/app/")
            && AiActivitySlotMatcher.IsSameWorkspace(@"C:\work\app\src", @"C:\work\app")
            && !AiActivitySlotMatcher.IsSameWorkspace(@"C:\work\app", @"C:\work\application")
            && !AiActivitySlotMatcher.IsSameWorkspace("", @"C:\work\app"));
    }

    private static void VerifySlotMatching(List<string> failures)
    {
        var slots = new (string Name, string Path)[]
        {
            ("A", @"C:\git_home\turtle-ai-code-quartet-hub"),
            ("B", @"C:\git_home\turtle"),
            ("C", @"C:\git_home\turtle-ai-code-quartet-hub\src"),
            ("D", "vscode-remote://ssh-remote+host/home/user/repo")
        };

        string Match(string cwd) => string.Join(",", AiActivitySlotMatcher.FindTargets(slots, slot => [slot.Path], cwd).Select(slot => slot.Name));

        Check(failures, "特定: 大文字小文字・区切り文字の違いを無視して一致する",
            Match(@"c:/git_home/turtle-ai-code-quartet-hub/") == "A", Match(@"c:/git_home/turtle-ai-code-quartet-hub/"));
        Check(failures, "特定: 名前の前方一致（turtle と turtle-ai…）を同じフォルダとみなさない",
            Match(@"C:\git_home\turtle-ai-code-quartet-hub\docs") == "A", Match(@"C:\git_home\turtle-ai-code-quartet-hub\docs"));
        Check(failures, "特定: 入れ子のワークスペースは一番深いスロットを選ぶ",
            Match(@"C:\git_home\turtle-ai-code-quartet-hub\src\TurtleAIQuartetHub.Panel") == "C");
        Check(failures, "特定: Git Bash 形式の /c/... も同じ場所として扱う",
            Match("/c/git_home/turtle") == "B", Match("/c/git_home/turtle"));
        Check(failures, "特定: 無関係なフォルダやリモートは対象外",
            Match(@"C:\other") == string.Empty && Match("/home/user/repo") == string.Empty);
    }

    private static void VerifyInstaller(List<string> failures)
    {
        var home = Path.Combine(Path.GetTempPath(), "turtle-ai-notify-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            var installer = new AiNotifyHookInstaller(home);

            // 利用者が自分で書いた設定とフックは残ること。
            File.WriteAllText(installer.ClaudeSettingsPath, """
                {
                  "model": "opus",
                  "hooks": {
                    "Stop": [ { "hooks": [ { "type": "command", "command": "echo mine" } ] } ]
                  }
                }
                """);

            var first = installer.Install();
            installer.Install();
            var claude = JsonNode.Parse(File.ReadAllText(installer.ClaudeSettingsPath))!.AsObject();
            var stopCommands = claude["hooks"]!["Stop"]!.AsArray()
                .SelectMany(group => group!["hooks"]!.AsArray())
                .Select(handler => handler!["command"]!.GetValue<string>())
                .ToList();
            Check(failures, "フック設定: 既存の設定と自前のフックを残し、2 回設定しても重複しない",
                claude["model"]!.GetValue<string>() == "opus"
                && stopCommands.Count == 2
                && stopCommands.Contains("echo mine")
                && stopCommands.Count(AiNotifyHookInstaller.IsHubCommand) == 1,
                string.Join(" | ", stopCommands));
            Check(failures, "フック設定: 書き換え前の内容をバックアップに残す",
                File.Exists(installer.ClaudeSettingsPath + ".turtle-hub-backup"));
            Check(failures, "フック設定: Codex は hooks.json を作り、Copilot / Grok / Antigravity は未インストールなので触らない",
                File.Exists(installer.CodexHooksPath)
                && first.Single(result => result.Tool == "codex").Status == AiNotifyHookStatus.Installed
                && first.Single(result => result.Tool == "copilot").Status == AiNotifyHookStatus.ToolMissing
                && !File.Exists(installer.CopilotHookPath)
                && first.Single(result => result.Tool == "grok").Status == AiNotifyHookStatus.ToolMissing
                && !Directory.Exists(Path.Combine(home, ".grok"))
                && first.Single(result => result.Tool == "antigravity").Status == AiNotifyHookStatus.ToolMissing
                && !Directory.Exists(Path.Combine(home, ".gemini")));
            Check(failures, "フック設定: 通知スクリプトは ASCII だけ（PowerShell 5.1 の文字化け対策）",
                File.ReadAllText(installer.ScriptPath).All(character => character < 128));
            Check(failures, "フック設定: ツールの前後（PreToolUse / PostToolUse）は登録しない（ツールのたびに AI を待たせない）",
                !claude["hooks"]!.AsObject().ContainsKey("PreToolUse")
                && !claude["hooks"]!.AsObject().ContainsKey("PostToolUse"),
                claude["hooks"]!.ToJsonString());
            Check(failures, "フック設定: Claude Code は StopFailure と SessionEnd も含めて登録する",
                claude["hooks"]!.AsObject().ContainsKey("UserPromptSubmit")
                && claude["hooks"]!.AsObject().ContainsKey("Stop")
                && claude["hooks"]!.AsObject().ContainsKey("StopFailure")
                && claude["hooks"]!.AsObject().ContainsKey("SessionEnd")
                && claude["hooks"]!.AsObject().ContainsKey("Notification"));
            var codexHooks = JsonNode.Parse(File.ReadAllText(installer.CodexHooksPath))!.AsObject()["hooks"]!.AsObject();
            Check(failures, "フック設定: Codex は SessionEnd も含めて登録する",
                codexHooks.ContainsKey("UserPromptSubmit")
                && !codexHooks.ContainsKey("PreToolUse")
                && codexHooks.ContainsKey("Stop")
                && codexHooks.ContainsKey("PermissionRequest")
                && codexHooks.ContainsKey("Interrupt")
                && codexHooks.ContainsKey("SessionEnd"));
            Check(failures, "フック設定: 通知詳細は各ツールの項目名と失敗・理由・リトライにも対応する",
                AiNotifyHookInstaller.NotifyScript.Contains("notification_type", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("notificationType", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("conversationId", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("workspacePaths", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("workspaceRoot", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("terminationReason", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("termination_reason", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("$data.reason", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("recoverable", StringComparison.Ordinal)
                && AiNotifyHookInstaller.NotifyScript.Contains("$attempt", StringComparison.Ordinal));
            Check(failures, "フック設定: コマンドは / 区切りのパスを引用符で囲む（Git Bash でも動く）",
                installer.BuildCommand("claude", "Stop").Contains($"\"{installer.ScriptPath.Replace('\\', '/')}\"", StringComparison.Ordinal));

            var removed = installer.Uninstall();
            var claudeAfter = JsonNode.Parse(File.ReadAllText(installer.ClaudeSettingsPath))!.AsObject();
            var remaining = claudeAfter["hooks"]!.AsObject();
            Check(failures, "フック解除: 自分が足した項目だけを消し、利用者のフックとスクリプト以外は残す",
                remaining.Count == 1
                && remaining["Stop"]!.AsArray().Count == 1
                && claudeAfter["model"]!.GetValue<string>() == "opus"
                && !File.Exists(installer.ScriptPath)
                && removed.All(result => result.Status != AiNotifyHookStatus.Installed),
                remaining.ToJsonString());
            var codexAfter = JsonNode.Parse(File.ReadAllText(installer.CodexHooksPath))!.AsObject();
            Check(failures, "フック解除: Codex の hooks.json からハブの項目が消える", codexAfter["hooks"] is null);

            Directory.CreateDirectory(Path.Combine(home, ".copilot"));
            var copilotInstalled = installer.Install();
            var copilotHooks = JsonNode.Parse(File.ReadAllText(installer.CopilotHookPath))!.AsObject()["hooks"]!.AsObject();
            Check(failures, "フック設定: GitHub Copilot は errorOccurred と sessionEnd も含めて登録する（ツールの前後は登録しない）",
                copilotInstalled.Single(result => result.Tool == "copilot").Status == AiNotifyHookStatus.Installed
                && copilotHooks.ContainsKey("userPromptSubmitted")
                && !copilotHooks.ContainsKey("preToolUse")
                && copilotHooks.ContainsKey("agentStop")
                && copilotHooks.ContainsKey("errorOccurred")
                && copilotHooks.ContainsKey("sessionEnd"),
                copilotHooks.ToJsonString());

            // 古い版のハブが書いた Copilot 専用ファイルとスクリプトは、起動時に今の内容へ揃える。
            File.WriteAllText(installer.CopilotHookPath, """{ "version": 1, "hooks": { "userPromptSubmitted": [], "preToolUse": [], "postToolUse": [], "agentStop": [] } }""");
            File.WriteAllText(installer.ScriptPath, "# old script");
            var refreshed = installer.RefreshOwnedFiles();
            var refreshedHooks = JsonNode.Parse(File.ReadAllText(installer.CopilotHookPath))!.AsObject()["hooks"]!.AsObject();
            var refreshedAgain = installer.RefreshOwnedFiles();
            Check(failures, "フック更新: 古い Copilot 専用ファイル（ツールの前後入り）とスクリプトを今の内容へ揃え、2 回目は何もしない",
                refreshed.SequenceEqual(["script", "copilot"])
                && !refreshedHooks.ContainsKey("preToolUse")
                && !refreshedHooks.ContainsKey("postToolUse")
                && refreshedHooks.ContainsKey("agentStop")
                && File.ReadAllText(installer.ScriptPath) == AiNotifyHookInstaller.NotifyScript
                && refreshedAgain.Count == 0,
                $"{string.Join(",", refreshed)} / {string.Join(",", refreshedAgain)}");

            // 前の版で登録したツールの前後のフック（PreToolUse / PostToolUse）は、利用者の設定を残してハブの項目だけ取り除く。
            string Escape(string command) => command.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var claudeStopHub = Escape(installer.BuildCommand("claude", "Stop"));
            var claudePreHub = Escape(installer.BuildCommand("claude", "PreToolUse"));
            var claudePostHub = Escape(installer.BuildCommand("claude", "PostToolUse"));
            File.WriteAllText(installer.ClaudeSettingsPath, $$"""
                {
                  "model": "opus",
                  "hooks": {
                    "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "{{Escape(installer.BuildCommand("claude", "UserPromptSubmit"))}}", "timeout": 10 } ] } ],
                    "PreToolUse": [
                      { "hooks": [ { "type": "command", "command": "echo my-pre" } ] },
                      { "hooks": [ { "type": "command", "command": "{{claudePreHub}}", "timeout": 10, "async": true } ] }
                    ],
                    "PostToolUse": [ { "hooks": [ { "type": "command", "command": "{{claudePostHub}}", "timeout": 10, "async": true } ] } ],
                    "Stop": [
                      { "hooks": [ { "type": "command", "command": "echo mine" } ] },
                      { "hooks": [ { "type": "command", "command": "{{claudeStopHub}}", "timeout": 10 } ] }
                    ],
                    "StopFailure": [ { "hooks": [ { "type": "command", "command": "{{Escape(installer.BuildCommand("claude", "StopFailure"))}}", "timeout": 10 } ] } ],
                    "SessionEnd": [ { "hooks": [ { "type": "command", "command": "{{Escape(installer.BuildCommand("claude", "SessionEnd"))}}", "timeout": 10 } ] } ],
                    "Notification": [ { "hooks": [ { "type": "command", "command": "{{Escape(installer.BuildCommand("claude", "Notification"))}}", "timeout": 10 } ] } ]
                  }
                }
                """);
            var upgraded = installer.RefreshOwnedFiles();
            var upgradedClaude = JsonNode.Parse(File.ReadAllText(installer.ClaudeSettingsPath))!.AsObject();
            var upgradedStop = upgradedClaude["hooks"]!["Stop"]!.AsArray()
                .SelectMany(group => group!["hooks"]!.AsArray())
                .Select(handler => handler!["command"]!.GetValue<string>())
                .ToList();
            var upgradedPre = upgradedClaude["hooks"]!["PreToolUse"]?.AsArray()
                .SelectMany(group => group!["hooks"]!.AsArray())
                .Select(handler => handler!["command"]!.GetValue<string>())
                .ToList() ?? [];
            Check(failures, "フック更新: 前の版で登録したツールの前後のフックを、利用者のフックと設定を残して取り除く",
                upgraded.SequenceEqual(["claude"])
                && upgradedClaude["model"]!.GetValue<string>() == "opus"
                && upgradedPre.SequenceEqual(["echo my-pre"])
                && !upgradedClaude["hooks"]!.AsObject().ContainsKey("PostToolUse")
                && upgradedStop.Contains("echo mine")
                && upgradedStop.Count(AiNotifyHookInstaller.IsHubCommand) == 1
                && File.Exists(installer.ClaudeSettingsPath + ".turtle-hub-backup")
                && installer.RefreshOwnedFiles().Count == 0,
                $"{string.Join(",", upgraded)} / {upgradedClaude["hooks"]!.ToJsonString()}");

            installer.Uninstall();
            Check(failures, "フック更新: 解除したあと（ファイルが無いとき）は起動時の更新でも何も作らない",
                installer.RefreshOwnedFiles().Count == 0
                && !File.Exists(installer.CopilotHookPath)
                && !File.Exists(installer.ScriptPath));
            Directory.Delete(Path.Combine(home, ".copilot"), recursive: true);

            var hooksDirectory = Path.Combine(home, ".grok", "hooks");
            Directory.CreateDirectory(hooksDirectory);
            var siblingPath = Path.Combine(hooksDirectory, "user-hook.json");
            var tomlPath = Path.Combine(home, ".grok", "config.toml");
            File.WriteAllText(siblingPath, """{ "keep": true }""");
            File.WriteAllText(tomlPath, "keep = true\n");
            var siblingBefore = File.ReadAllBytes(siblingPath);
            var tomlBefore = File.ReadAllBytes(tomlPath);

            var grokInstalled = installer.Install();
            var grokHooks = JsonNode.Parse(File.ReadAllText(installer.GrokHooksPath))!.AsObject()["hooks"]!.AsObject();
            var expectedEvents = new[] { "UserPromptSubmit", "Stop", "StopFailure", "StopCancelled", "Notification" };
            var grokCommands = expectedEvents.Select(hookEvent => grokHooks[hookEvent]!.AsArray()
                .SelectMany(group => group!["hooks"]!.AsArray())
                .Select(handler => handler!["command"]!.GetValue<string>())
                .ToList()).ToList();
            Check(failures, "フック設定: Grok は専用 JSON だけを作り、ほかのフックと config.toml は残す",
                grokInstalled.Single(result => result.Tool == "grok").Status == AiNotifyHookStatus.Installed
                && grokCommands.All(commands => commands.Count == 1 && commands[0].Contains("-Tool grok ", StringComparison.Ordinal))
                && grokCommands.Select(commands => commands[0].Contains("-HookEvent ", StringComparison.Ordinal)).All(found => found)
                && expectedEvents.Select((hookEvent, index) => grokCommands[index][0].Contains($"-HookEvent {hookEvent}", StringComparison.Ordinal)).All(found => found)
                && siblingBefore.SequenceEqual(File.ReadAllBytes(siblingPath))
                && tomlBefore.SequenceEqual(File.ReadAllBytes(tomlPath))
                && Directory.GetFiles(hooksDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(["turtle-ai-quartet-hub.json", "user-hook.json"]),
                grokHooks.ToJsonString());

            var grokRemoved = installer.Uninstall();
            Check(failures, "フック解除: ハブだけの Grok 専用 JSON は消し、バックアップと隣のファイルは残す",
                !File.Exists(installer.GrokHooksPath)
                && File.Exists(installer.GrokHooksPath + ".turtle-hub-backup")
                && siblingBefore.SequenceEqual(File.ReadAllBytes(siblingPath))
                && tomlBefore.SequenceEqual(File.ReadAllBytes(tomlPath))
                && Directory.Exists(Path.Combine(home, ".grok"))
                && grokRemoved.Single(result => result.Tool == "grok").Status == AiNotifyHookStatus.NotInstalled);

            installer.Install();
            var withCustom = JsonNode.Parse(File.ReadAllText(installer.GrokHooksPath))!.AsObject();
            withCustom["custom"] = true;
            File.WriteAllText(installer.GrokHooksPath, withCustom.ToJsonString());
            installer.Uninstall();
            var kept = JsonNode.Parse(File.ReadAllText(installer.GrokHooksPath))!.AsObject();
            Check(failures, "フック解除: Grok 専用 JSON に利用者のキーがあればファイルを残す",
                kept["custom"]!.GetValue<bool>()
                && !kept.ToJsonString().Contains(AiNotifyHookInstaller.ScriptFileName, StringComparison.OrdinalIgnoreCase)
                && siblingBefore.SequenceEqual(File.ReadAllBytes(siblingPath))
                && tomlBefore.SequenceEqual(File.ReadAllBytes(tomlPath)));

            var geminiRoot = Path.Combine(home, ".gemini");
            Directory.CreateDirectory(geminiRoot);
            File.WriteAllText(Path.Combine(geminiRoot, "settings.json"), "{}");
            var geminiOnlyResult = installer.Install();
            Check(failures, "フック設定: gemini-cli の設定だけでは Antigravity を設定対象にしない",
                geminiOnlyResult.Single(result => result.Tool == "antigravity").Status == AiNotifyHookStatus.ToolMissing
                && !File.Exists(installer.AntigravityHooksPath));

            Directory.CreateDirectory(Path.GetDirectoryName(installer.AntigravityHooksPath)!);
            File.WriteAllText(installer.AntigravityHooksPath, """
                {
                  "lint-checker": {
                    "enabled": true,
                    "PostInvocation": [
                      {
                        "type": "command",
                        "command": "echo lint",
                        "timeout": 5
                      }
                    ]
                  }
                }
                """);
            installer.Install();
            var agInstalled = installer.Install();
            var agRoot = JsonNode.Parse(File.ReadAllText(installer.AntigravityHooksPath))!.AsObject();
            var agHub = agRoot[AiNotifyHookInstaller.AntigravityHookName]!.AsObject();
            var agPre = agHub["PreInvocation"]!.AsArray();
            var agStop = agHub["Stop"]!.AsArray();
            var agPreCmd = agPre[0]!["command"]!.GetValue<string>();
            var agStopCmd = agStop[0]!["command"]!.GetValue<string>();
            Check(failures, "フック設定: Antigravity は既存の名前付きフックを残し、平坦配列・無引用コマンドで重複なく書く",
                agInstalled.Single(result => result.Tool == "antigravity").Status == AiNotifyHookStatus.Installed
                && agRoot["lint-checker"]!["PostInvocation"]![0]!["command"]!.GetValue<string>() == "echo lint"
                && agHub["enabled"]!.GetValue<bool>()
                && agPre.Count == 1 && agStop.Count == 1
                && agPre[0]!["hooks"] is null && agPre[0]!["matcher"] is null
                && !agPreCmd.Contains('"', StringComparison.Ordinal) && !agStopCmd.Contains('"', StringComparison.Ordinal)
                && agPreCmd.Contains("-Tool antigravity -HookEvent PreInvocation", StringComparison.Ordinal)
                && agStopCmd.Contains("-Tool antigravity -HookEvent Stop", StringComparison.Ordinal)
                && File.Exists(installer.AntigravityHooksPath + ".turtle-hub-backup"),
                agRoot.ToJsonString());

            var agRemoved = installer.Uninstall();
            var agAfter = JsonNode.Parse(File.ReadAllText(installer.AntigravityHooksPath))!.AsObject();
            var lintPreserved = agAfter["lint-checker"]!["PostInvocation"]![0]!["command"]!.GetValue<string>() == "echo lint"
                && !agAfter.ContainsKey(AiNotifyHookInstaller.AntigravityHookName)
                && agRemoved.Single(result => result.Tool == "antigravity").Status == AiNotifyHookStatus.NotInstalled;
            File.Delete(installer.AntigravityHooksPath);
            installer.Install();
            installer.Uninstall();
            Check(failures, "フック解除: Antigravity の他フックは残し、ハブ項目だけなら空ファイルを消す",
                lintPreserved
                && !File.Exists(installer.AntigravityHooksPath)
                && File.Exists(installer.AntigravityHooksPath + ".turtle-hub-backup"));

            var spaceHome = Path.Combine(Path.GetTempPath(), "turtle ai test " + Guid.NewGuid().ToString("N"));
            var spaceCmd = new AiNotifyHookInstaller(spaceHome).BuildCommand("antigravity", "PreInvocation");
            Check(failures, "フック設定: 空白を含むホームでは Antigravity コマンドを相対パスにして引用符を付けない",
                !spaceCmd.Contains('"', StringComparison.Ordinal)
                && spaceCmd.Contains("-File ../../.turtle-ai-quartet-hub/ai-notify.ps1 -Tool antigravity -HookEvent PreInvocation", StringComparison.Ordinal)
                && AiNotifyHookInstaller.IsHubCommand(spaceCmd));
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task VerifyPipeConcurrencyAsync(List<string> failures)
    {
        var token = Guid.NewGuid().ToString("N");
        var mutexName = @"Local\TurtleHubTest_" + token;
        var pipeName = "TurtleHubPipeTest_" + token;
        var tempScript = Path.Combine(Path.GetTempPath(), $"ai-notify-test-{token}.ps1");
        try
        {
            using var coordinator = new SingleInstanceCoordinator(mutexName, pipeName);
            var received = new ConcurrentQueue<string[]>();
            var all16Received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.CommandReceived += args =>
            {
                received.Enqueue(args);
                if (received.Count >= 16)
                {
                    all16Received.TrySetResult();
                }
            };

            const int burstCount = 16;
            var sendTasks = Enumerable.Range(0, burstCount)
                .Select(i => coordinator.SendToPrimaryAsync(["--ai-event", "codex", i % 2 == 0 ? "UserPromptSubmit" : "Stop", @"C:\work\a", $"s{i}", ""]))
                .ToArray();
            var sendResults = await Task.WhenAll(sendTasks);
            await Task.WhenAny(all16Received.Task, Task.Delay(3000));
            Check(failures, "パイプ受信: 同時多発した 16 件のフック通知を 1 件も落とさず受け取れる",
                sendResults.All(ok => ok) && received.Count == burstCount,
                $"sent={sendResults.Count(ok => ok)}/{burstCount}, received={received.Count}");

            while (received.TryDequeue(out _)) { }
            var malformedDelivered = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.CommandReceived += args => malformedDelivered.TrySetResult(args);

            var testScriptContent = AiNotifyHookInstaller.NotifyScript.Replace("'TurtleAIQuartetHub.Commands'", $"'{pipeName}'", StringComparison.Ordinal);
            File.WriteAllText(tempScript, testScriptContent, new System.Text.UTF8Encoding(false));

            var workingDir = Path.Combine(Path.GetTempPath(), "turtle-ps-cwd-" + token);
            Directory.CreateDirectory(workingDir);
            try
            {
                using var ps = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\" -Tool antigravity -HookEvent Stop",
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                ps.Start();
                await ps.StandardInput.WriteAsync("{invalid-json");
                ps.StandardInput.Close();
                var stdout = (await ps.StandardOutput.ReadToEndAsync()).Trim();
                using var timeoutCts = new CancellationTokenSource(5000);
                await ps.WaitForExitAsync(timeoutCts.Token);
                var delivered = await Task.WhenAny(malformedDelivered.Task, Task.Delay(3000)) == malformedDelivered.Task
                    ? await malformedDelivered.Task
                    : null;

                Check(failures, "通知スクリプト: 不正 JSON が渡ってもクラッシュせず現在フォルダを補完してパイプ送信できる",
                    ps.ExitCode == 0
                    && stdout == "{}"
                    && delivered is { Length: >= 4 }
                    && delivered[1] == "antigravity"
                    && delivered[2] == "Stop"
                    && string.Equals(delivered[3].TrimEnd('\\'), workingDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    $"exit={ps.ExitCode}, stdout={stdout}, delivered={string.Join(",", delivered ?? [])}");
            }
            finally
            {
                try { Directory.Delete(workingDir, recursive: true); } catch (IOException) { }
            }
        }
        finally
        {
            try { File.Delete(tempScript); } catch (IOException) { }
        }
    }

    private static AiHookEvent Event(string tool, AiHookEventKind kind, string session) =>
        new(tool, kind.ToString(), kind, @"C:\work", session);

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(detail is null ? name : $"{name}: {detail}");
        }
    }
}
