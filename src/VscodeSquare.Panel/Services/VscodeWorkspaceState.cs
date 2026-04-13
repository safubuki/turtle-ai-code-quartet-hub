using System.IO;
using System.Text.Json;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public static class VscodeWorkspaceState
{
    public static string? TryReadLastWorkspacePath(WindowSlot slot, AppConfig config)
    {
        var workspaceStorageDirectory = Path.Combine(
            SlotUserDataPaths.GetUserDataDirectory(slot, config),
            "User",
            "workspaceStorage");

        if (!Directory.Exists(workspaceStorageDirectory))
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(workspaceStorageDirectory, "workspace.json", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                var workspacePath = TryReadWorkspaceJson(file.FullName);
                if (!string.IsNullOrWhiteSpace(workspacePath))
                {
                    return workspacePath;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return null;
    }

    private static string? TryReadWorkspaceJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (TryReadFileUri(root, "folder", out var folderPath))
            {
                return folderPath;
            }

            if (TryReadFileUri(root, "workspace", out var workspacePath))
            {
                return workspacePath;
            }

            if (root.TryGetProperty("workspace", out var workspaceElement)
                && workspaceElement.ValueKind == JsonValueKind.Object
                && TryReadFileUri(workspaceElement, "configPath", out var configPath))
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

    private static bool TryReadFileUri(JsonElement element, string propertyName, out string? path)
    {
        path = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        path = ToLocalPath(property.GetString());
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string? ToLocalPath(string? value)
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

        return value;
    }
}
