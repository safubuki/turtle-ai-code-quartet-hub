using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class AppConfig
{
    public const string VsCodeApplicationId = "vscode";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string CodeCommand { get; set; } = "code";

    public string Monitor { get; set; } = "primary";

    public int Gap { get; set; } = 8;

    public bool UseDedicatedUserDataDirs { get; set; } = true;

    public bool InheritMainUserState { get; set; } = true;

    public bool ReopenLastWorkspace { get; set; } = true;

    public string StateDirectory { get; set; } = GetDefaultStateDirectory();

    public int LaunchTimeoutSeconds { get; set; } = 40;

    public int RemoteReconnectTimeoutSeconds { get; set; } = 15;

    public int StatusRefreshIntervalMilliseconds { get; set; } = 1000;

    public string DefaultWorkspaceApplicationId { get; set; } = VsCodeApplicationId;

    public List<ToolApplicationConfig> Applications { get; set; } = DefaultApplications();

    public List<SlotConfig> Slots { get; set; } = DefaultSlots();

    [JsonIgnore]
    public string ConfigSource { get; private set; } = "built-in defaults";

    public static AppConfig Load()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            config.ConfigSource = path;
            config.Normalize();
            return config;
        }

        var fallback = new AppConfig();
        fallback.Normalize();
        return fallback;
    }

    public void Normalize()
    {
        CodeCommand = string.IsNullOrWhiteSpace(CodeCommand) ? "code" : CodeCommand.Trim();
        Monitor = string.IsNullOrWhiteSpace(Monitor) ? "primary" : Monitor.Trim();
        Gap = Math.Clamp(Gap, 0, 64);
        StateDirectory = string.IsNullOrWhiteSpace(StateDirectory)
            ? GetDefaultStateDirectory()
            : Environment.ExpandEnvironmentVariables(StateDirectory.Trim());
        LaunchTimeoutSeconds = Math.Clamp(LaunchTimeoutSeconds, 5, 120);
        RemoteReconnectTimeoutSeconds = Math.Clamp(RemoteReconnectTimeoutSeconds, 1, LaunchTimeoutSeconds);
        StatusRefreshIntervalMilliseconds = Math.Clamp(StatusRefreshIntervalMilliseconds, 250, 5000);
        DefaultWorkspaceApplicationId = NormalizeApplicationId(DefaultWorkspaceApplicationId, VsCodeApplicationId);
        Applications = NormalizeApplications(Applications, CodeCommand);

        var configuredSlots = Slots ?? DefaultSlots();
        Slots = configuredSlots
            .Where(slot => slot is not null && !string.IsNullOrWhiteSpace(slot.Name))
            .Take(4)
            .Select(slot => new SlotConfig
            {
                Name = slot.Name.Trim(),
                Path = slot.Path?.Trim() ?? string.Empty,
                ApplicationId = NormalizeApplicationId(slot.ApplicationId, DefaultWorkspaceApplicationId)
            })
            .ToList();

        var defaults = DefaultSlots();
        while (Slots.Count < 4)
        {
            Slots.Add(defaults[Slots.Count]);
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(GetDefaultStateDirectory(), "config", "turtle-ai-quartet-hub.json");

        var roots = GetSearchRoots().ToList();

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "config", "turtle-ai-quartet-hub.json");
        }

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "config", "turtle-ai-quartet-hub.example.json");
        }
    }

    private static string GetDefaultStateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurtleAIQuartetHub");
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }

    private static List<SlotConfig> DefaultSlots()
    {
        return
        [
            new() { Name = "A", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "B", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "C", Path = string.Empty, ApplicationId = VsCodeApplicationId },
            new() { Name = "D", Path = string.Empty, ApplicationId = VsCodeApplicationId }
        ];
    }

    public static string NormalizeApplicationId(string? applicationId, string fallback = VsCodeApplicationId)
    {
        return string.IsNullOrWhiteSpace(applicationId)
            ? fallback
            : applicationId.Trim().ToLowerInvariant();
    }

    private static List<ToolApplicationConfig> NormalizeApplications(
        IEnumerable<ToolApplicationConfig>? configuredApplications,
        string codeCommand)
    {
        var defaults = DefaultApplications();
        var byId = new Dictionary<string, ToolApplicationConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in defaults)
        {
            byId[NormalizeApplicationId(app.Id)] = app;
        }

        if (configuredApplications is not null)
        {
            foreach (var app in configuredApplications.Where(app => app is not null && !string.IsNullOrWhiteSpace(app.Id)))
            {
                var id = NormalizeApplicationId(app.Id);
                byId[id] = MergeApplication(byId.TryGetValue(id, out var fallback) ? fallback : null, app);
            }
        }

        if (byId.TryGetValue(VsCodeApplicationId, out var vsCode)
            && string.IsNullOrWhiteSpace(vsCode.Command))
        {
            vsCode.Command = codeCommand;
        }

        return byId.Values
            .Select(NormalizeApplication)
            .OrderBy(app => GetApplicationSortOrder(app.Id))
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ToolApplicationConfig MergeApplication(ToolApplicationConfig? fallback, ToolApplicationConfig configured)
    {
        configured.Detection ??= new ApplicationDetectionConfig();
        configured.Arguments ??= [];
        configured.Detection.Commands ??= [];
        configured.Detection.ProcessNames ??= [];
        configured.Detection.StartMenuNames ??= [];
        configured.Detection.AppPathNames ??= [];
        if (fallback is not null)
        {
            fallback.Arguments ??= [];
            fallback.Detection ??= new ApplicationDetectionConfig();
            fallback.Detection.Commands ??= [];
            fallback.Detection.ProcessNames ??= [];
            fallback.Detection.StartMenuNames ??= [];
            fallback.Detection.AppPathNames ??= [];
        }

        if (fallback is null)
        {
            return configured;
        }

        return new ToolApplicationConfig
        {
            Id = configured.Id,
            DisplayName = string.IsNullOrWhiteSpace(configured.DisplayName) ? fallback.DisplayName : configured.DisplayName,
            ShortName = string.IsNullOrWhiteSpace(configured.ShortName) ? fallback.ShortName : configured.ShortName,
            Kind = fallback.Kind,
            Command = string.IsNullOrWhiteSpace(configured.Command) ? fallback.Command : configured.Command,
            Arguments = configured.Arguments.Count > 0 ? configured.Arguments : fallback.Arguments,
            SupportsMultipleWindows = configured.SupportsMultipleWindows || fallback.SupportsMultipleWindows,
            Detection = new ApplicationDetectionConfig
            {
                Commands = configured.Detection.Commands.Count > 0 ? configured.Detection.Commands : fallback.Detection.Commands,
                ProcessNames = configured.Detection.ProcessNames.Count > 0 ? configured.Detection.ProcessNames : fallback.Detection.ProcessNames,
                StartMenuNames = configured.Detection.StartMenuNames.Count > 0 ? configured.Detection.StartMenuNames : fallback.Detection.StartMenuNames,
                AppPathNames = configured.Detection.AppPathNames.Count > 0 ? configured.Detection.AppPathNames : fallback.Detection.AppPathNames
            }
        };
    }

    private static ToolApplicationConfig NormalizeApplication(ToolApplicationConfig app)
    {
        app.Id = NormalizeApplicationId(app.Id);
        app.DisplayName = string.IsNullOrWhiteSpace(app.DisplayName) ? app.Id : app.DisplayName.Trim();
        app.ShortName = string.IsNullOrWhiteSpace(app.ShortName) ? app.DisplayName : app.ShortName.Trim();
        app.Command = Environment.ExpandEnvironmentVariables(app.Command?.Trim() ?? string.Empty);
        app.Arguments ??= [];
        app.Arguments = app.Arguments
            .Where(argument => argument is not null)
            .Select(argument => argument.Trim())
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .ToList();
        app.Detection ??= new ApplicationDetectionConfig();
        app.Detection.Commands ??= [];
        app.Detection.ProcessNames ??= [];
        app.Detection.StartMenuNames ??= [];
        app.Detection.AppPathNames ??= [];
        app.Detection.Commands = NormalizeStringList(app.Detection.Commands);
        app.Detection.ProcessNames = NormalizeStringList(app.Detection.ProcessNames);
        app.Detection.StartMenuNames = NormalizeStringList(app.Detection.StartMenuNames);
        app.Detection.AppPathNames = NormalizeStringList(app.Detection.AppPathNames);

        if (app.Detection.Commands.Count == 0 && !string.IsNullOrWhiteSpace(app.Command))
        {
            app.Detection.Commands.Add(app.Command);
        }

        if (app.Detection.ProcessNames.Count == 0)
        {
            app.Detection.ProcessNames.Add(app.DisplayName);
        }

        return app;
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private static int GetApplicationSortOrder(string applicationId)
    {
        return NormalizeApplicationId(applicationId) switch
        {
            VsCodeApplicationId => 0,
            "antigravity" => 1,
            "codex" => 2,
            "claude" => 3,
            _ => 100
        };
    }

    private static List<ToolApplicationConfig> DefaultApplications()
    {
        return
        [
            new()
            {
                Id = VsCodeApplicationId,
                DisplayName = "VS Code",
                ShortName = "VS Code",
                Kind = ApplicationKind.WorkspaceIde,
                Command = "code",
                Arguments = ["--new-window", "{workspacePath}"],
                SupportsMultipleWindows = true,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["code", "code-insiders"],
                    ProcessNames = ["Code", "Code - Insiders", "VSCodium", "Codium"],
                    StartMenuNames = ["Visual Studio Code", "Visual Studio Code - Insiders", "VSCodium"],
                    AppPathNames = ["Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Codium.exe"]
                }
            },
            new()
            {
                Id = "antigravity",
                DisplayName = "Antigravity",
                ShortName = "Antigravity",
                Kind = ApplicationKind.WorkspaceIde,
                Command = "antigravity",
                Arguments = ["--new-window", "{workspacePath}"],
                SupportsMultipleWindows = true,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = ["antigravity"],
                    ProcessNames = ["Antigravity"],
                    StartMenuNames = ["Antigravity", "Google Antigravity"],
                    AppPathNames = ["Antigravity.exe"]
                }
            },
            new()
            {
                Id = "codex",
                DisplayName = "Codex",
                ShortName = "Codex",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["Codex"],
                    StartMenuNames = ["Codex", "OpenAI Codex"],
                    AppPathNames = ["Codex.exe"]
                }
            },
            new()
            {
                Id = "claude",
                DisplayName = "Claude",
                ShortName = "Claude",
                Kind = ApplicationKind.SingleWindowAgent,
                Command = string.Empty,
                Arguments = [],
                SupportsMultipleWindows = false,
                Detection = new ApplicationDetectionConfig
                {
                    Commands = [],
                    ProcessNames = ["Claude"],
                    StartMenuNames = ["Claude"],
                    AppPathNames = ["Claude.exe"]
                }
            }
        ];
    }
}
