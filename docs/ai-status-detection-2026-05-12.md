# AI Status Detection Notes 2026-05-12

- Codex `thread-stream-state-changed` is broadcast-like. It must not become displayed Running unless the slot can claim ownership by foreground/focus/recent UI anchor through `IsCodexBroadcastOwner`.
- Claude Code is treated as a separate engine from `Anthropic.claude-code/Claude VSCode.log`. Displayed Running requires Claude/Anthropic UI context or a recent UI-confirmed Claude run; log-only activity remains suspect.
- UI Automation is capped for low-spec machines: the scan timeout is 220ms, each refresh probes at most two slots, and it drops to one slot after a slow refresh.
- Smoke check used: `dotnet run --project tools/AiStatusSmoke/AiStatusSmoke.csproj -- --json`. With no active AI work, all four slots remained Idle.
