using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class ApplicationLauncher
{
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(150);
    private readonly WindowEnumerator _windowEnumerator;
    private readonly VscodeLauncher _vscodeLauncher;

    public ApplicationLauncher(WindowEnumerator windowEnumerator, VscodeLauncher vscodeLauncher)
    {
        _windowEnumerator = windowEnumerator;
        _vscodeLauncher = vscodeLauncher;
    }

    public bool CanLaunchWorkspaceApplication(LauncherApplication application, AppConfig config)
    {
        if (!application.IsAvailable)
        {
            return false;
        }

        return !application.IsVsCode || _vscodeLauncher.IsCodeCommandAvailable(config.CodeCommand);
    }

    public async Task<IReadOnlyList<WindowAssignment>> LaunchMissingAsync(
        IReadOnlyList<WindowSlot> slots,
        AppConfig config,
        LauncherApplication application,
        CancellationToken cancellationToken)
    {
        if (application.IsVsCode)
        {
            return await _vscodeLauncher.LaunchMissingAsync(slots, config, cancellationToken);
        }

        if (application.IsWorkspaceCli)
        {
            return await LaunchWorkspaceCliApplicationAsync(slots, config, application, cancellationToken);
        }

        return await LaunchGenericWorkspaceApplicationAsync(slots, config, application, cancellationToken);
    }

    public async Task<ApplicationOpenResult> LaunchSingleWindowApplicationAsync(
        LauncherApplication application,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        if (!application.IsAvailable)
        {
            return new ApplicationOpenResult(false, false, $"{application.DisplayName} は検出できません。");
        }

        try
        {
            await Task.Run(() => StartApplication(application, []), cancellationToken);
            return new ApplicationOpenResult(true, false, $"{application.DisplayName} の起動コマンドを送信しました。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            DiagnosticLog.Write(ex);
            return new ApplicationOpenResult(false, false, $"{application.DisplayName} を起動できませんでした: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<WindowAssignment>> LaunchGenericWorkspaceApplicationAsync(
        IReadOnlyList<WindowSlot> slots,
        AppConfig config,
        LauncherApplication application,
        CancellationToken cancellationToken)
    {
        if (!application.IsAvailable)
        {
            return [];
        }

        var launchTargets = slots
            .Where(slot => slot.WindowHandle == IntPtr.Zero || !_windowEnumerator.IsLiveWindow(slot.WindowHandle))
            .Take(4)
            .ToList();
        if (launchTargets.Count == 0)
        {
            return [];
        }

        foreach (var slot in launchTargets)
        {
            slot.WindowStatus = SlotWindowStatus.Launching;
        }

        var knownHandles = GetKnownHandles(application);
        var assignments = new List<WindowAssignment>();
        var timeout = TimeSpan.FromSeconds(config.LaunchTimeoutSeconds);

        foreach (var slot in launchTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            knownHandles.UnionWith(GetKnownHandles(application));

            var launchPath = GetLaunchPath(slot, config);
            var arguments = BuildArguments(application, slot, launchPath);
            DiagnosticLog.Write($"Starting {application.DisplayName} for slot {slot.Name}: {application.ResolvedCommand} {string.Join(" ", arguments)}");
            _ = StartApplication(application, arguments);
            var window = await WaitForNewApplicationWindowAsync(application, knownHandles, expectedProcessId: null, timeout, cancellationToken);
            if (window is null)
            {
                DiagnosticLog.Write(LogLevel.Warn, $"No new {application.DisplayName} window detected for slot {slot.Name} within {timeout.TotalSeconds:0} seconds.");
                slot.WindowStatus = SlotWindowStatus.Missing;
                continue;
            }

            knownHandles.Add(window.Handle);
            assignments.Add(new WindowAssignment(slot, window));
        }

        return assignments;
    }

    private async Task<IReadOnlyList<WindowAssignment>> LaunchWorkspaceCliApplicationAsync(
        IReadOnlyList<WindowSlot> slots,
        AppConfig config,
        LauncherApplication application,
        CancellationToken cancellationToken)
    {
        if (!application.IsAvailable)
        {
            return [];
        }

        var launchTargets = slots
            .Where(slot => slot.WindowHandle == IntPtr.Zero || !_windowEnumerator.IsLiveWindow(slot.WindowHandle))
            .Take(4)
            .ToList();
        if (launchTargets.Count == 0)
        {
            return [];
        }

        foreach (var slot in launchTargets)
        {
            slot.WindowStatus = SlotWindowStatus.Launching;
        }

        var knownHandles = GetKnownHandles(application);
        var assignments = new List<WindowAssignment>();
        var timeout = TimeSpan.FromSeconds(config.LaunchTimeoutSeconds);
        var pendingLaunches = new List<(WindowSlot Slot, string ExpectedTitle)>();

        foreach (var slot in launchTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var launchPath = GetLaunchPath(slot, config);
            var workingDirectory = GetWorkingDirectory(launchPath);
            var expectedTitle = BuildCliWindowTitle(slot, application);
            DiagnosticLog.Write($"Starting {application.DisplayName} for slot {slot.Name}: cwd={workingDirectory}, title={expectedTitle}");
            _ = StartCliApplication(application, slot, workingDirectory, launchPath, expectedTitle);
            pendingLaunches.Add((slot, expectedTitle));
        }

        var stopwatch = Stopwatch.StartNew();
        while (pendingLaunches.Count > 0 && stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var windows = _windowEnumerator
                .GetApplicationWindows(application.ProcessNames)
                .Where(candidate => !knownHandles.Contains(candidate.Handle))
                .OrderBy(candidate => candidate.ProcessId)
                .ToList();
            foreach (var window in windows)
            {
                var pendingIndex = pendingLaunches.FindIndex(launch =>
                    window.Title.Contains(launch.ExpectedTitle, StringComparison.OrdinalIgnoreCase));
                if (pendingIndex < 0)
                {
                    continue;
                }

                var launch = pendingLaunches[pendingIndex];
                knownHandles.Add(window.Handle);
                assignments.Add(new WindowAssignment(launch.Slot, window));
                pendingLaunches.RemoveAt(pendingIndex);
            }

            foreach (var window in windows.Where(window => !knownHandles.Contains(window.Handle)).ToList())
            {
                if (pendingLaunches.Count == 0)
                {
                    break;
                }

                var launch = pendingLaunches[0];
                DiagnosticLog.Write($"Assigning {application.DisplayName} terminal without title match for slot {launch.Slot.Name}: title={window.Title}");
                knownHandles.Add(window.Handle);
                assignments.Add(new WindowAssignment(launch.Slot, window));
                pendingLaunches.RemoveAt(0);
            }

            if (pendingLaunches.Count == 0)
            {
                break;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < WindowPollInterval ? remaining : WindowPollInterval, cancellationToken);
        }

        foreach (var launch in pendingLaunches)
        {
            DiagnosticLog.Write(LogLevel.Warn, $"No new {application.DisplayName} terminal window detected for slot {launch.Slot.Name} within {timeout.TotalSeconds:0} seconds.");
            launch.Slot.WindowStatus = SlotWindowStatus.Missing;
        }

        return assignments;
    }

    private HashSet<IntPtr> GetKnownHandles(LauncherApplication application)
    {
        return _windowEnumerator
            .GetApplicationWindows(application.ProcessNames)
            .Select(window => window.Handle)
            .ToHashSet();
    }

    private async Task<WindowInfo?> WaitForNewApplicationWindowAsync(
        LauncherApplication application,
        HashSet<IntPtr> knownHandles,
        uint? expectedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = _windowEnumerator
                .GetApplicationWindows(application.ProcessNames)
                .Where(candidate => !knownHandles.Contains(candidate.Handle))
                .Where(candidate => !expectedProcessId.HasValue
                    || candidate.ProcessId == expectedProcessId.Value
                    || !application.SupportsMultipleWindows
                    || application.IsWorkspaceCli)
                .OrderBy(candidate => candidate.ProcessId)
                .FirstOrDefault();
            if (window is not null)
            {
                return window;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < WindowPollInterval ? remaining : WindowPollInterval, cancellationToken);
        }

        return null;
    }

    private static uint? StartApplication(LauncherApplication application, IReadOnlyList<string> arguments)
    {
        var command = !string.IsNullOrWhiteSpace(application.ResolvedCommand)
            ? application.ResolvedCommand
            : application.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var startInfo = CreateStartInfo(command, arguments);
        using var process = Process.Start(startInfo);
        return process is null ? null : (uint)process.Id;
    }

    private static uint? StartCliApplication(
        LauncherApplication application,
        WindowSlot slot,
        string workingDirectory,
        string? launchPath,
        string windowTitle)
    {
        var commandLine = BuildCliCommandLine(application, slot, launchPath);
        var pathCommand = BuildCliPathCommand(application);
        var title = EscapeCmdMeta(windowTitle);
        var cdCommand = $"cd /d {QuoteForCmdArgument(workingDirectory)}";
        var initialCommands = new[] { pathCommand, cdCommand, $"title {title}", commandLine }
            .Where(command => !string.IsNullOrWhiteSpace(command));
        var initialCommand = string.Join(" && ", initialCommands);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /k \"{initialCommand}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        using var process = Process.Start(startInfo);
        return process is null ? null : (uint)process.Id;
    }

    private static string BuildCliWindowTitle(WindowSlot slot, LauncherApplication application)
    {
        return $"Turtle {slot.Name} - {application.ShortName}";
    }

    private static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> arguments)
    {
        if (IsShellLaunchCommand(command))
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = string.Join(" ", new[] { command }.Concat(arguments.Select(Quote))),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        var extension = Path.GetExtension(command);
        if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var useShellExecute = IsWindowsAppsPath(command);
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = useShellExecute
            };

            if (useShellExecute)
            {
                startInfo.Arguments = string.Join(" ", arguments.Select(Quote));
            }
            else
            {
                AddArguments(startInfo.ArgumentList, arguments);
            }

            return startInfo;
        }

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", arguments.Select(Quote)),
                UseShellExecute = true
            };
        }

        var wrappedCommand = string.Join(
            " ",
            new[] { QuoteForCommandShell(command) }.Concat(arguments.Select(QuoteForCommandShell)));
        return new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{wrappedCommand}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static void AddArguments(Collection<string> argumentList, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            argumentList.Add(argument);
        }
    }

    private static List<string> BuildArguments(LauncherApplication application, WindowSlot slot, string? launchPath)
    {
        var values = application.Arguments.Count > 0
            ? application.Arguments
            : string.IsNullOrWhiteSpace(launchPath)
                ? []
                : ["{workspacePath}"];

        var arguments = new List<string>();
        foreach (var template in values)
        {
            var value = template
                .Replace("{workspacePath}", launchPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{slotName}", slot.Name, StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.Add(value);
            }
        }

        return arguments;
    }

    private static string BuildCliCommandLine(LauncherApplication application, WindowSlot slot, string? launchPath)
    {
        var resolvedCommand = application.ResolvedCommand.Trim();
        var configuredCommand = application.Command.Trim();
        var command = !string.IsNullOrWhiteSpace(resolvedCommand)
            ? resolvedCommand
            : configuredCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var commandText = LooksLikeExplicitPath(command)
            ? QuoteForCmdArgument(command)
            : command;
        var arguments = BuildConfiguredArguments(application, slot, launchPath)
            .Select(QuoteForCmdArgument);
        return string.Join(" ", new[] { commandText }.Concat(arguments));
    }

    private static string BuildCliPathCommand(LauncherApplication application)
    {
        var roots = GetCliPathRoots(application)
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            return string.Empty;
        }

        var pathPrefix = EscapeCmdMeta(string.Join(Path.PathSeparator, roots));
        return $"path {pathPrefix};%PATH%";
    }

    private static IEnumerable<string> GetCliPathRoots(LauncherApplication application)
    {
        if (!string.IsNullOrWhiteSpace(application.ResolvedCommand)
            && LooksLikeExplicitPath(application.ResolvedCommand)
            && File.Exists(application.ResolvedCommand))
        {
            var resolvedDirectory = Path.GetDirectoryName(application.ResolvedCommand);
            if (!string.IsNullOrWhiteSpace(resolvedDirectory))
            {
                yield return resolvedDirectory;
            }
        }

        foreach (var root in GetCommonCliCommandRoots())
        {
            yield return root;
        }
    }

    private static IEnumerable<string> GetCommonCliCommandRoots()
    {
        var npmPrefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(npmPrefix))
        {
            yield return npmPrefix;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "npm");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "npm");
            yield return Path.Combine(localAppData, "pnpm");
            yield return Path.Combine(localAppData, "Volta", "bin");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "bin");
            yield return Path.Combine(userProfile, ".grok", "bin");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "nodejs");
        }
    }

    private static List<string> BuildConfiguredArguments(LauncherApplication application, WindowSlot slot, string? launchPath)
    {
        var arguments = new List<string>();
        foreach (var template in application.Arguments)
        {
            var value = template
                .Replace("{workspacePath}", launchPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{slotName}", slot.Name, StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.Add(value);
            }
        }

        return arguments;
    }

    private static string? GetLaunchPath(WindowSlot slot, AppConfig config)
    {
        if (config.ReopenLastWorkspace
            && slot.SavedWorkspaceConfirmed
            && !string.IsNullOrWhiteSpace(slot.SavedWorkspacePath))
        {
            return slot.SavedWorkspacePath;
        }

        return slot.Path;
    }

    private static string GetWorkingDirectory(string? launchPath)
    {
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            var normalizedPath = launchPath.Trim();
            if (Directory.Exists(normalizedPath))
            {
                return normalizedPath;
            }

            if (File.Exists(normalizedPath))
            {
                return Path.GetDirectoryName(normalizedPath) ?? GetDefaultWorkingDirectory();
            }
        }

        return GetDefaultWorkingDirectory();
    }

    private static string GetDefaultWorkingDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? Environment.CurrentDirectory : userProfile;
    }

    private static string Quote(string value)
    {
        return value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
    }

    private static string QuoteForCommandShell(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string QuoteForCmdArgument(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string EscapeCmdMeta(string value)
    {
        return value
            .Replace("^", "^^", StringComparison.Ordinal)
            .Replace("&", "^&", StringComparison.Ordinal)
            .Replace("|", "^|", StringComparison.Ordinal)
            .Replace("<", "^<", StringComparison.Ordinal)
            .Replace(">", "^>", StringComparison.Ordinal)
            .Replace("(", "^(", StringComparison.Ordinal)
            .Replace(")", "^)", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal);
    }

    private static bool LooksLikeExplicitPath(string command)
    {
        return Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool IsWindowsAppsPath(string path)
    {
        return path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellLaunchCommand(string command)
    {
        return command.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ApplicationOpenResult(bool Success, bool FocusedExisting, string Message);
