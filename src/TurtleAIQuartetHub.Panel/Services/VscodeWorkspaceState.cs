using System.IO;
using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class VscodeWorkspaceState
{
    public static string? TryReadCurrentWorkspacePath(WindowSlot slot, AppConfig config)
    {
        return TryReadCurrentWorkspacePath(slot.Name, slot.WindowTitle, config);
    }

    public static string? TryReadCurrentWorkspacePath(string slotName, string windowTitle, AppConfig config)
    {
        var workspacePath = TryReadLastWorkspacePath(slotName, config);
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        return IsWorkspaceVisibleInWindowTitle(windowTitle, workspacePath)
            ? workspacePath
            : null;
    }

    public static string? TryReadLastWorkspacePath(WindowSlot slot, AppConfig config)
    {
        return TryReadLastWorkspacePath(slot.Name, config);
    }

    public static string? TryReadLastWorkspacePath(string slotName, AppConfig config)
    {
        var workspaceStorageDirectory = Path.Combine(
            SlotUserDataPaths.GetUserDataDirectory(slotName, config),
            "User",
            "workspaceStorage");

        if (!Directory.Exists(workspaceStorageDirectory))
        {
            return null;
        }

        try
        {
            string? latestWorkspacePath = null;
            var latestWorkspaceTime = DateTime.MinValue;

            foreach (var file in Directory.EnumerateFiles(workspaceStorageDirectory, "workspace.json", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path)))
            {
                var workspacePath = TryReadWorkspaceJson(file.FullName);
                if (!string.IsNullOrWhiteSpace(workspacePath) && file.LastWriteTimeUtc >= latestWorkspaceTime)
                {
                    latestWorkspacePath = workspacePath;
                    latestWorkspaceTime = file.LastWriteTimeUtc;
                }
            }

            return latestWorkspacePath;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return null;
    }

    public static bool IsWorkspaceVisibleInWindowTitle(string? windowTitle, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        foreach (var candidate in GetWorkspaceTitleCandidates(workspacePath))
        {
            if (windowTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetWorkspaceTitleCandidates(string workspacePath)
    {
        var normalizedPath = GetComparableWorkspacePath(workspacePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield break;
        }

        var fileName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        if (Path.HasExtension(normalizedPath))
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedPath);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                && !string.Equals(fileNameWithoutExtension, fileName, StringComparison.OrdinalIgnoreCase))
            {
                yield return fileNameWithoutExtension;
            }
        }

        if (TryCreateNonFileUri(workspacePath, out var uri))
        {
            var authority = GetReadableRemoteAuthority(uri.Authority);
            if (!string.IsNullOrWhiteSpace(authority))
            {
                yield return authority;
            }
        }
    }

    private static string? TryReadWorkspaceJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (TryReadWorkspaceLocation(root, "folder", out var folderPath))
            {
                return folderPath;
            }

            if (TryReadWorkspaceLocation(root, "workspace", out var workspacePath))
            {
                return workspacePath;
            }

            if (root.TryGetProperty("workspace", out var workspaceElement)
                && workspaceElement.ValueKind == JsonValueKind.Object
                && TryReadWorkspaceLocation(workspaceElement, "configPath", out var configPath))
            {
                return configPath;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return null;
    }

    private static bool TryReadWorkspaceLocation(JsonElement element, string propertyName, out string? path)
    {
        path = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        path = ToWorkspaceLocation(property.GetString());
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string? ToWorkspaceLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var localPath = uri.LocalPath;
            if (localPath.Length >= 3 && localPath[0] == '/' && char.IsLetter(localPath[1]) && localPath[2] == ':')
            {
                localPath = localPath[1..];
            }

            return localPath.Replace('/', Path.DirectorySeparatorChar);
        }

        if (TryCreateNonFileUri(value, out var nonFileUri))
        {
            return nonFileUri.AbsoluteUri;
        }

        return value;
    }

    private static string GetComparableWorkspacePath(string workspacePath)
    {
        if (!TryCreateNonFileUri(workspacePath, out var uri))
        {
            return workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var uriPath = Uri.UnescapeDataString(uri.AbsolutePath);
        return uriPath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryCreateNonFileUri(string value, out NonFileUriInfo uri)
    {
        uri = default;
        if (IsWindowsPath(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var parsedUri)
            && parsedUri is not null
            && !string.IsNullOrWhiteSpace(parsedUri.Scheme)
            && !parsedUri.IsFile)
        {
            uri = new NonFileUriInfo(parsedUri.Scheme, parsedUri.Authority, parsedUri.AbsolutePath, parsedUri.AbsoluteUri);
            return true;
        }

        return TryParseUriParts(value, out uri)
            && !string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUriParts(string value, out NonFileUriInfo uri)
    {
        uri = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        var scheme = value[..schemeSeparatorIndex];
        if (!IsValidUriScheme(scheme))
        {
            return false;
        }

        var remainder = value[(schemeSeparatorIndex + 3)..];
        var pathIndex = remainder.IndexOf('/');
        var authority = pathIndex >= 0 ? remainder[..pathIndex] : remainder;
        var absolutePath = pathIndex >= 0 ? remainder[pathIndex..] : "/";
        uri = new NonFileUriInfo(scheme, authority, absolutePath, value);
        return true;
    }

    private static bool IsValidUriScheme(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme) || !char.IsLetter(scheme[0]))
        {
            return false;
        }

        for (var index = 1; index < scheme.Length; index++)
        {
            var character = scheme[index];
            if (!char.IsLetterOrDigit(character)
                && character != '+'
                && character != '-'
                && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string GetReadableRemoteAuthority(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return string.Empty;
        }

        var plusIndex = authority.IndexOf('+', StringComparison.Ordinal);
        return Uri.UnescapeDataString(plusIndex >= 0 && plusIndex < authority.Length - 1
            ? authority[(plusIndex + 1)..]
            : authority);
    }

    private static bool IsWindowsPath(string value)
    {
        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }

    private readonly record struct NonFileUriInfo(string Scheme, string Authority, string AbsolutePath, string AbsoluteUri);
}
