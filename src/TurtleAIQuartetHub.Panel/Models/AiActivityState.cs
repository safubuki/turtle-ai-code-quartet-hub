namespace TurtleAIQuartetHub.Panel.Models;

// AI ツールのフック（Claude Code / Codex / GitHub Copilot）から届いた明示的な通知だけで決める状態。
// 画面・ログ・チャット UI からの推定はしない。
public enum AiActivityState
{
    // 何もしていない（従来どおりの表示）。
    Idle,

    // 指示を受けて作業中（枠がゆっくり明滅）。
    Working,

    // 応答が完了した、または許可・回答を待っている（全体がぼんやり光る。フォーカス表示を切り替えると消える）。
    Done
}
