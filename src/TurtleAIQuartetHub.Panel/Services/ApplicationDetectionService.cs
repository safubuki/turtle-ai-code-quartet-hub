using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class ApplicationDetectionService
{
    private const string ShellAppsFolderPrefix = @"shell:AppsFolder\";

    public IReadOnlyList<LauncherApplication> Detect(AppConfig config)
    {
        return config.Applications
            .Select(applicationConfig =>
            {
                var application = new LauncherApplication(applicationConfig);
                var result = Resolve(application);
                application.ApplyAvailability(result.Status, result.Command, result.Detail);
                return application;
            })
            .ToList();
    }

    private static DetectionResult Resolve(LauncherApplication application)
    {
        var configuredCommand = !string.IsNullOrWhiteSpace(application.Command)
            ? Environment.ExpandEnvironmentVariables(application.Command.Trim().Trim('"'))
            : string.Empty;

        if (application.IsSingleWindowAgent
            && (string.IsNullOrWhiteSpace(configuredCommand)
                || (!LooksLikeExplicitPath(configuredCommand) && !IsShellLaunchCommand(configuredCommand))))
        {
            var packagedApp = ResolveAppxPackage(application);
            if (!string.IsNullOrWhiteSpace(packagedApp))
            {
                return DetectionResult.Installed(packagedApp, $"Windows アプリパッケージから検出しました: {packagedApp}");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            var resolvedConfiguredCommand = ResolveShellLaunchCommand(configuredCommand)
                ?? ResolveCommand(configuredCommand)
                ?? ResolveCommonInstallPath(configuredCommand, application.Detection.AppPathNames, application.Detection.StartMenuNames);
            if (!string.IsNullOrWhiteSpace(resolvedConfiguredCommand))
            {
                return DetectionResult.Installed(resolvedConfiguredCommand, $"設定コマンドから検出しました: {resolvedConfiguredCommand}");
            }

            if (LooksLikeExplicitPath(configuredCommand))
            {
                return new DetectionResult(
                    ApplicationAvailabilityStatus.ConfiguredButMissing,
                    string.Empty,
                    $"{application.DisplayName} の設定パスが見つかりません: {configuredCommand}");
            }
        }

        if (!application.IsWorkspaceCli)
        {
            var appxPath = ResolveAppxPackage(application);
            if (!string.IsNullOrWhiteSpace(appxPath))
            {
                return DetectionResult.Installed(appxPath, $"Windows アプリパッケージから検出しました: {appxPath}");
            }
        }

        if (!application.IsWorkspaceCli)
        {
            var runningProcessPath = ResolveRunningProcessPath(application.Detection.ProcessNames);
            if (!string.IsNullOrWhiteSpace(runningProcessPath))
            {
                return DetectionResult.Installed(runningProcessPath, $"起動済みプロセスから検出しました: {runningProcessPath}");
            }
        }

        foreach (var command in application.Detection.Commands)
        {
            var resolvedCommand = ResolveCommand(Environment.ExpandEnvironmentVariables(command.Trim().Trim('"')));
            if (!string.IsNullOrWhiteSpace(resolvedCommand))
            {
                return DetectionResult.Installed(resolvedCommand, $"PATH から検出しました: {resolvedCommand}");
            }
        }

        foreach (var appPathName in application.Detection.AppPathNames)
        {
            var appPath = ResolveAppPath(appPathName);
            if (!string.IsNullOrWhiteSpace(appPath))
            {
                return DetectionResult.Installed(appPath, $"App Paths から検出しました: {appPath}");
            }
        }

        var commonPath = ResolveCommonInstallPath(
            application.DisplayName,
            application.Detection.AppPathNames,
            application.Detection.StartMenuNames);
        if (!string.IsNullOrWhiteSpace(commonPath))
        {
            return DetectionResult.Installed(commonPath, $"一般的なインストール先から検出しました: {commonPath}");
        }

        foreach (var startMenuName in application.Detection.StartMenuNames)
        {
            var shortcut = ResolveStartMenuShortcut(startMenuName);
            if (!string.IsNullOrWhiteSpace(shortcut))
            {
                return DetectionResult.Installed(shortcut, $"スタートメニューから検出しました: {shortcut}");
            }
        }

        return new DetectionResult(
            ApplicationAvailabilityStatus.NotFound,
            string.Empty,
            $"{application.DisplayName} は検出できません。設定で実行ファイルまたはコマンドを指定してください。");
    }

    private static string? ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        foreach (var candidate in GetPathCandidates(command))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveShellLaunchCommand(string command)
    {
        return IsShellLaunchCommand(command) ? command : null;
    }

    private static string? ResolveRunningProcessPath(IEnumerable<string> processNames)
    {
        var normalizedNames = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedNames.Count == 0)
        {
            return null;
        }

        foreach (var process in Process.GetProcesses().OrderBy(process => process.Id))
        {
            using (process)
            {
                if (!normalizedNames.Contains(process.ProcessName))
                {
                    continue;
                }

                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)
                        && File.Exists(path)
                        && !IsExtensionHelperPath(path))
                    {
                        return path;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    DiagnosticLog.Write(ex);
                }
            }
        }

        return null;
    }

    private static string? ResolveAppPath(string appPathName)
    {
        if (string.IsNullOrWhiteSpace(appPathName))
        {
            return null;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var subKeyPath in new[]
            {
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appPathName}",
                $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{appPathName}"
            })
            {
                try
                {
                    using var key = root.OpenSubKey(subKeyPath);
                    var value = key?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    {
                        return value;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                }
            }
        }

        return null;
    }

    private static string? ResolveAppxPackage(LauncherApplication application)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var repositoryKey = root.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (repositoryKey is null)
                {
                    continue;
                }

                foreach (var packageName in repositoryKey.GetSubKeyNames())
                {
                    using var packageKey = repositoryKey.OpenSubKey(packageName);
                    var displayName = packageKey?.GetValue("DisplayName") as string;
                    var packageId = packageKey?.GetValue("PackageID") as string ?? packageName;
                    if (!IsPackageMatch(application, packageId, displayName))
                    {
                        continue;
                    }

                    var rootFolder = packageKey?.GetValue("PackageRootFolder") as string;
                    var appUserModelId = ResolveAppUserModelId(packageName, packageId, packageKey, application);
                    if (!string.IsNullOrWhiteSpace(appUserModelId))
                    {
                        return ShellAppsFolderPrefix + appUserModelId;
                    }

                    var executable = ResolveAppxExecutable(rootFolder, application.Detection.AppPathNames);
                    if (!string.IsNullOrWhiteSpace(executable))
                    {
                        return executable;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        return null;
    }

    private static string? ResolveAppUserModelId(
        string packageName,
        string packageId,
        RegistryKey? packageKey,
        LauncherApplication application)
    {
        var packageFamilyName = packageKey?.GetValue("PackageFamilyName") as string
            ?? DerivePackageFamilyName(packageId)
            ?? DerivePackageFamilyName(packageName);
        if (string.IsNullOrWhiteSpace(packageFamilyName) || packageKey is null)
        {
            return null;
        }

        var applicationIds = packageKey
            .GetSubKeyNames()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (applicationIds.Count == 0)
        {
            return null;
        }

        var candidates = GetApplicationMatchCandidates(application);
        var applicationId = applicationIds.FirstOrDefault(id =>
                candidates.Any(candidate => id.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(id, StringComparison.OrdinalIgnoreCase)))
            ?? applicationIds.FirstOrDefault();
        return string.IsNullOrWhiteSpace(applicationId)
            ? null
            : $"{packageFamilyName}!{applicationId}";
    }

    private static string? DerivePackageFamilyName(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        var publisherSeparator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (publisherSeparator < 0 || publisherSeparator + 2 >= packageFullName.Length)
        {
            return null;
        }

        var publisherId = packageFullName[(publisherSeparator + 2)..];
        var packageIdentity = packageFullName[..publisherSeparator];
        var firstSeparator = packageIdentity.IndexOf('_', StringComparison.Ordinal);
        if (firstSeparator <= 0)
        {
            return null;
        }

        return $"{packageIdentity[..firstSeparator]}_{publisherId}";
    }

    private static bool IsPackageMatch(LauncherApplication application, string packageId, string? displayName)
    {
        var candidates = GetApplicationMatchCandidates(application);

        return candidates.Any(candidate => packageId.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(displayName)
                && candidates.Any(candidate => displayName.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(displayName, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> GetApplicationMatchCandidates(LauncherApplication application)
    {
        return new[]
            {
                application.Id,
                application.DisplayName,
                application.ShortName
            }
            .Concat(application.Detection.StartMenuNames)
            .Concat(application.Detection.ProcessNames.Select(Path.GetFileNameWithoutExtension))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveAppxExecutable(string? rootFolder, IEnumerable<string> executableNames)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return null;
        }

        foreach (var executableName in executableNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            foreach (var relativePath in new[] { executableName, Path.Combine("app", executableName) })
            {
                var candidate = Path.Combine(rootFolder, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? ResolveStartMenuShortcut(string startMenuName)
    {
        if (string.IsNullOrWhiteSpace(startMenuName))
        {
            return null;
        }

        foreach (var root in GetStartMenuRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var shortcut = Directory
                    .EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(path =>
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        return name.Contains(startMenuName, StringComparison.OrdinalIgnoreCase)
                            || startMenuName.Contains(name, StringComparison.OrdinalIgnoreCase);
                    });
                if (!string.IsNullOrWhiteSpace(shortcut))
                {
                    return shortcut;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        return null;
    }

    private static string? ResolveCommonInstallPath(
        string applicationName,
        IEnumerable<string> executableNames,
        IEnumerable<string> directoryNames)
    {
        var executableCandidates = executableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (executableCandidates.Count == 0 && !string.IsNullOrWhiteSpace(applicationName))
        {
            executableCandidates.Add($"{applicationName}.exe");
        }

        var directoryCandidates = directoryNames
            .Concat(new[] { applicationName })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in GetInstallRoots())
        {
            foreach (var directoryName in directoryCandidates)
            {
                foreach (var executableName in executableCandidates)
                {
                    var candidate = Path.Combine(root, directoryName, executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPathCandidates(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || LooksLikeExplicitPath(command))
        {
            yield break;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var names = Path.HasExtension(command)
            ? new[] { command }
            : new[] { command }.Concat(extensions.Select(extension => command + extension)).ToArray();

        var directories = pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(GetCommonCommandRoots())
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }

    private static IEnumerable<string> GetCommonCommandRoots()
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
            // Claude Code's Windows installer can place the launcher here instead of npm's shim directory.
            yield return Path.Combine(userProfile, ".local", "bin");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "nodejs");
        }
    }

    private static IEnumerable<string> GetStartMenuRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
    }

    private static IEnumerable<string> GetInstallRoots()
    {
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return root;
            }
        }
    }

    private static bool LooksLikeExplicitPath(string command)
    {
        return !IsShellLaunchCommand(command)
            && (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar));
    }

    private static bool IsShellLaunchCommand(string command)
    {
        return command.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExtensionHelperPath(string path)
    {
        return path.Contains(@"\.vscode\extensions\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\.vscode-insiders\extensions\", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct DetectionResult(
        ApplicationAvailabilityStatus Status,
        string Command,
        string Detail)
    {
        public static DetectionResult Installed(string command, string detail)
        {
            return new DetectionResult(ApplicationAvailabilityStatus.Installed, command, detail);
        }
    }
}
