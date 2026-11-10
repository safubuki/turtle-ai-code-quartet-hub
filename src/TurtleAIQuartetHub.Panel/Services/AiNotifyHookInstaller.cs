using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurtleAIQuartetHub.Panel.Services;

public enum AiNotifyHookStatus
{
    NotInstalled,
    Installed,
    ToolMissing,
    Error
}

public sealed record AiNotifyHookToolResult(string Tool, string DisplayName, AiNotifyHookStatus Status, string Message);

// Claude Code / Codex / GitHub Copilot のユーザー設定へ、応答の開始・完了をハブへ知らせるフックを足し引きする。
// 既存の設定は残し、ハブが足した項目（通知スクリプトのパスを含むコマンド）だけを扱う。
public sealed class AiNotifyHookInstaller
{
    public const string ScriptFolderName = ".turtle-ai-quartet-hub";
    public const string ScriptFileName = "ai-notify.ps1";
    public const string CopilotHookFileName = "turtle-ai-quartet-hub.json";
    private const string BackupSuffix = ".turtle-hub-backup";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Claude Code / Codex は同じ形式（event → [{ hooks: [{ type, command, timeout }] }]）。
    private static readonly string[] ClaudeEvents = ["UserPromptSubmit", "Stop", "Notification"];
    private static readonly string[] CodexEvents = ["UserPromptSubmit", "Stop", "PermissionRequest", "Interrupt"];
    private static readonly string[] CopilotEvents = ["userPromptSubmitted", "agentStop"];

    private readonly string _homeDirectory;

    public AiNotifyHookInstaller(string? homeDirectory = null)
    {
        _homeDirectory = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;
    }

    // AppData は MSIX 版だとアプリ専用の場所へ振り替えられ、AI ツールから見えないことがあるため、ホーム直下に置く。
    public string ScriptPath => Path.Combine(_homeDirectory, ScriptFolderName, ScriptFileName);

    public string ClaudeSettingsPath => Path.Combine(_homeDirectory, ".claude", "settings.json");

    public string CodexHooksPath => Path.Combine(_homeDirectory, ".codex", "hooks.json");

    public string CopilotHookPath => Path.Combine(_homeDirectory, ".copilot", "hooks", CopilotHookFileName);

    public IReadOnlyList<AiNotifyHookToolResult> GetStatus()
    {
        return
        [
            ReadStatus("claude", "Claude Code", ClaudeSettingsPath, ".claude"),
            ReadStatus("codex", "Codex", CodexHooksPath, ".codex"),
            ReadCopilotStatus()
        ];
    }

    public IReadOnlyList<AiNotifyHookToolResult> Install()
    {
        WriteScript();
        return
        [
            Run("claude", "Claude Code", ".claude", () => UpdateEventHookFile(ClaudeSettingsPath, "claude", ClaudeEvents, install: true)),
            Run("codex", "Codex", ".codex", () => UpdateEventHookFile(CodexHooksPath, "codex", CodexEvents, install: true)),
            Run("copilot", "GitHub Copilot", ".copilot", WriteCopilotHookFile)
        ];
    }

    public IReadOnlyList<AiNotifyHookToolResult> Uninstall()
    {
        var results = new List<AiNotifyHookToolResult>
        {
            Run("claude", "Claude Code", ".claude", () => UpdateEventHookFile(ClaudeSettingsPath, "claude", ClaudeEvents, install: false)),
            Run("codex", "Codex", ".codex", () => UpdateEventHookFile(CodexHooksPath, "codex", CodexEvents, install: false)),
            Run("copilot", "GitHub Copilot", ".copilot", () =>
            {
                if (File.Exists(CopilotHookPath))
                {
                    File.Delete(CopilotHookPath);
                }
            })
        };

        if (results.All(result => result.Status != AiNotifyHookStatus.Installed))
        {
            TryDeleteScript();
        }

        return results;
    }

    public string BuildCommand(string tool, string hookEvent)
    {
        // Claude Code は Windows でも Git Bash 経由で実行するため、パスは / 区切りで引用符に入れる。
        var script = ScriptPath.Replace('\\', '/');
        return $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Tool {tool} -HookEvent {hookEvent}";
    }

    public static bool IsHubCommand(string? command)
    {
        return !string.IsNullOrEmpty(command)
            && command.Contains(ScriptFolderName, StringComparison.OrdinalIgnoreCase)
            && command.Contains(ScriptFileName, StringComparison.OrdinalIgnoreCase);
    }

    private AiNotifyHookToolResult Run(string tool, string displayName, string toolFolder, Action action)
    {
        if (!Directory.Exists(Path.Combine(_homeDirectory, toolFolder)))
        {
            return new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.ToolMissing, "未インストール");
        }

        try
        {
            action();
            return tool switch
            {
                "claude" => ReadStatus(tool, displayName, ClaudeSettingsPath, toolFolder),
                "codex" => ReadStatus(tool, displayName, CodexHooksPath, toolFolder),
                _ => ReadCopilotStatus()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            DiagnosticLog.Write(ex);
            return new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.Error, ex.Message);
        }
    }

    private AiNotifyHookToolResult ReadStatus(string tool, string displayName, string path, string toolFolder)
    {
        if (!Directory.Exists(Path.Combine(_homeDirectory, toolFolder)))
        {
            return new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.ToolMissing, "未インストール");
        }

        try
        {
            if (!File.Exists(path))
            {
                return new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.NotInstalled, "未設定");
            }

            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
            var installed = root?["hooks"] is JsonObject hooks && CountHubEntries(hooks) > 0;
            return installed && File.Exists(ScriptPath)
                ? new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.Installed, "設定済み")
                : new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.NotInstalled, "未設定");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AiNotifyHookToolResult(tool, displayName, AiNotifyHookStatus.Error, "設定ファイルを読めません");
        }
    }

    private AiNotifyHookToolResult ReadCopilotStatus()
    {
        if (!Directory.Exists(Path.Combine(_homeDirectory, ".copilot")))
        {
            return new AiNotifyHookToolResult("copilot", "GitHub Copilot", AiNotifyHookStatus.ToolMissing, "未インストール");
        }

        return File.Exists(CopilotHookPath) && File.Exists(ScriptPath)
            ? new AiNotifyHookToolResult("copilot", "GitHub Copilot", AiNotifyHookStatus.Installed, "設定済み")
            : new AiNotifyHookToolResult("copilot", "GitHub Copilot", AiNotifyHookStatus.NotInstalled, "未設定");
    }

    private void UpdateEventHookFile(string path, string tool, IReadOnlyList<string> events, bool install)
    {
        JsonObject root;
        string? originalText = null;
        if (File.Exists(path))
        {
            originalText = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(originalText)
                ? new JsonObject()
                : JsonNode.Parse(originalText, documentOptions: ReadOptions) as JsonObject
                    ?? throw new InvalidOperationException($"{path} が JSON オブジェクトではありません");
        }
        else
        {
            if (!install)
            {
                return;
            }

            root = new JsonObject();
        }

        if (root["hooks"] is not null and not JsonObject)
        {
            throw new InvalidOperationException($"{path} の hooks が想定外の形式です");
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var removed = RemoveHubEntries(hooks);
        if (install)
        {
            foreach (var hookEvent in events)
            {
                if (hooks[hookEvent] is not JsonArray groups)
                {
                    if (hooks[hookEvent] is not null)
                    {
                        throw new InvalidOperationException($"{path} の hooks.{hookEvent} が想定外の形式です");
                    }

                    groups = [];
                    hooks[hookEvent] = groups;
                }

                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = BuildCommand(tool, hookEvent),
                            ["timeout"] = 10
                        }
                    }
                });
            }
        }
        else if (removed == 0)
        {
            return;
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
        else if (root["hooks"] is null)
        {
            root["hooks"] = hooks;
        }

        WriteWithBackup(path, originalText, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private void WriteCopilotHookFile()
    {
        var hooks = new JsonObject();
        foreach (var hookEvent in CopilotEvents)
        {
            var command = BuildCommand("copilot", hookEvent);
            hooks[hookEvent] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["powershell"] = command,
                    ["command"] = command,
                    ["timeoutSec"] = 10
                }
            };
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = hooks
        };

        Directory.CreateDirectory(Path.GetDirectoryName(CopilotHookPath)!);
        File.WriteAllText(CopilotHookPath, root.ToJsonString(WriteOptions) + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static int RemoveHubEntries(JsonObject hooks)
    {
        var removed = 0;
        foreach (var (eventName, node) in hooks.ToList())
        {
            if (node is not JsonArray groups)
            {
                continue;
            }

            for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (groups[groupIndex] is not JsonObject group || group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
                {
                    if (handlers[handlerIndex] is JsonObject handler && IsHubCommand(ReadCommand(handler)))
                    {
                        handlers.RemoveAt(handlerIndex);
                        removed++;
                    }
                }

                if (handlers.Count == 0)
                {
                    groups.RemoveAt(groupIndex);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        return removed;
    }

    private static int CountHubEntries(JsonObject hooks)
    {
        return hooks
            .Select(pair => pair.Value)
            .OfType<JsonArray>()
            .SelectMany(groups => groups.OfType<JsonObject>())
            .Select(group => group["hooks"])
            .OfType<JsonArray>()
            .SelectMany(handlers => handlers.OfType<JsonObject>())
            .Count(handler => IsHubCommand(ReadCommand(handler)));
    }

    private static string? ReadCommand(JsonObject handler)
    {
        return handler["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;
    }

    private static void WriteWithBackup(string path, string? originalText, string newText)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (originalText is not null)
        {
            File.WriteAllText(path + BackupSuffix, originalText, new UTF8Encoding(false));
        }

        // 途中で失敗しても元の設定を半端に壊さないよう、一時ファイルに書いてから置き換える。
        var temporaryPath = path + ".turtle-hub-tmp";
        File.WriteAllText(temporaryPath, newText, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void WriteScript()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath)!);
        File.WriteAllText(ScriptPath, NotifyScript, new UTF8Encoding(false));
    }

    private void TryDeleteScript()
    {
        try
        {
            if (File.Exists(ScriptPath))
            {
                File.Delete(ScriptPath);
            }

            var folder = Path.GetDirectoryName(ScriptPath)!;
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // Windows PowerShell 5.1 は BOM なし UTF-8 を ANSI として読むため、スクリプトは ASCII だけで書く。
    // $Event は PowerShell の自動変数なので引数名は HookEvent にする。
    internal const string NotifyScript = """
        # Turtle AI Quartet Hub - AI activity notifier (written by the hub; safe to delete via the hub settings).
        # AI tool hooks call this when a prompt starts or a response ends.
        # It only sends the tool name, the event name and the working folder to the running hub.
        # It never changes the AI's behavior: it always prints {} and exits 0.
        param([string]$Tool = '', [string]$HookEvent = '')
        $ErrorActionPreference = 'SilentlyContinue'
        $pipeName = 'TurtleAIQuartetHub.Commands'
        try {
            # Hub not running: finish immediately instead of waiting for the connect timeout.
            if (-not ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains ('\\.\pipe\' + $pipeName))) {
                Write-Output '{}'
                exit 0
            }
            $raw = ''
            if ([Console]::IsInputRedirected) {
                $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), (New-Object System.Text.UTF8Encoding($false)))
                $raw = $reader.ReadToEnd()
            }
            $data = $null
            if ($raw) { $data = $raw | ConvertFrom-Json }
            $cwd = ''
            $session = ''
            $detail = ''
            if ($data) {
                if ($data.cwd) { $cwd = [string]$data.cwd }
                if ($data.session_id) { $session = [string]$data.session_id } elseif ($data.sessionId) { $session = [string]$data.sessionId }
                if ($data.notification_type) { $detail = [string]$data.notification_type }
            }
            if (-not $cwd) { $cwd = (Get-Location).Path }
            $payload = ConvertTo-Json -Compress -InputObject @('--ai-event', $Tool, $HookEvent, $cwd, $session, $detail)
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
            $pipe.Connect(800)
            $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
            $writer.Write($payload)
            $writer.Flush()
            $pipe.Dispose()
        } catch { }
        Write-Output '{}'
        exit 0

        """;
}
