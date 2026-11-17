namespace TurtleAIQuartetHub.Panel.Models;

// AI ツールのフック（Claude Code / Codex / GitHub Copilot / Grok / Antigravity）から届いた明示的な通知だけで決める状態。
// 画面・ログ・チャット UI からの推定はしない。
public enum AiActivityState
{
    // 何もしていない（従来どおりの表示）。
    Idle,

    // 指示を受けて作業中（枠の上を緑の光が一周する）。
    Working,

    // 応答が終わった（少し早めのホタル点滅で青く光る。フォーカス表示を切り替えると消える）。許可の確認中は作業中のまま。
    Done
}
