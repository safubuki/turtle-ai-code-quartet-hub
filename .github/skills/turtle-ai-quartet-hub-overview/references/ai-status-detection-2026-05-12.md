# AI status detection update 2026-05-12

- Codex broadcast logs (`thread-stream-state-changed`) are owner-gated in both legacy and state-machine paths. Recent broadcast log activity by itself must stay suspect and must not display every slot as Running.
- Claude Code support is conservative: `Anthropic.claude-code/Claude VSCode.log` is read as a separate engine, but displayed Running needs Claude/Anthropic UI context or a recent UI-confirmed Claude run.
- UIA load guard: `VscodeChatUiStatusReader` scan cap is 220ms, element cap is lower, and `StatusStore` probes at most two slots per refresh, dropping to one after a slow refresh.
- Regression check: `tools/AiStatusSmoke --json` should show A-D Idle when no AI is active, even if old Codex or Claude logs exist.
