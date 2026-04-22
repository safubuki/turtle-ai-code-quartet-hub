using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class VscodeLayoutState
{
    private const int MinimumWideWindowWidth = 1000;
    private const int MinimumSideBarWidth = 120;
    private const int MinimumAuxiliaryBarWidth = 240;
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public static bool TryReadLayoutPreference(WindowSlot slot, AppConfig config, out VscodeLayoutPreference preference)
    {
        preference = VscodeLayoutPreference.Empty;

        var storagePath = GetStoragePath(slot, config);
        if (!File.Exists(storagePath))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(storagePath)) as JsonObject;
            if (root is null)
            {
                return false;
            }

            preference = ReadLayoutPreference(root);
            return preference.HasAnyValue;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
    }

    public static bool TryCapturePreferredLayout(
        WindowSlot slot,
        AppConfig config,
        WindowArranger arranger,
        out VscodeLayoutPreference preference)
    {
        preference = VscodeLayoutPreference.Empty;
        if (!arranger.TryGetWindowBounds(slot.WindowHandle, out var bounds)
            || bounds.Width < MinimumWideWindowWidth)
        {
            return false;
        }

        if (!TryReadLayoutPreference(slot, config, out preference))
        {
            return false;
        }

        if (!HasPreferredCaptureValue(preference))
        {
            return false;
        }

        DiagnosticLog.Write(
            $"Captured layout for slot {slot.Name}: sideBar={preference.SideBarWidth}, auxiliaryBar={preference.AuxiliaryBarWidth}, auxiliarySideBar={preference.AuxiliarySideBarWidth}");
        return true;
    }

    public static bool TryApplyPreferredLayout(WindowSlot slot, AppConfig config, VscodeLayoutPreference preference)
    {
        if (!preference.HasAnyValue)
        {
            return false;
        }

        var storagePath = GetStoragePath(slot, config);
        if (!File.Exists(storagePath))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(storagePath)) as JsonObject;
            root ??= new JsonObject();

            var changed = false;
            changed |= SetIntValue(root, preference.SideBarWidth, "windowSplash", "layoutInfo", "sideBarWidth");
            changed |= SetIntValue(root, preference.SideBarWidth, "windowSplashWorkspaceOverride", "layoutInfo", "sideBarWidth");
            changed |= SetIntValue(root, preference.AuxiliaryBarWidth, "windowSplash", "layoutInfo", "auxiliaryBarWidth");
            changed |= SetIntValue(root, preference.AuxiliaryBarWidth, "windowSplashWorkspaceOverride", "layoutInfo", "auxiliaryBarWidth");
            changed |= SetAuxiliarySideBarWidth(root, preference.AuxiliarySideBarWidth);

            if (!changed)
            {
                return false;
            }

            File.WriteAllText(storagePath, root.ToJsonString(JsonWriteOptions));
            DiagnosticLog.Write(
                $"Applied layout for slot {slot.Name}: sideBar={preference.SideBarWidth}, auxiliaryBar={preference.AuxiliaryBarWidth}, auxiliarySideBar={preference.AuxiliarySideBarWidth}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
    }

    private static bool HasPreferredCaptureValue(VscodeLayoutPreference preference)
    {
        return preference.SideBarWidth >= MinimumSideBarWidth
            || preference.AuxiliaryBarWidth >= MinimumAuxiliaryBarWidth
            || preference.AuxiliarySideBarWidth >= MinimumAuxiliaryBarWidth;
    }

    private static string GetStoragePath(WindowSlot slot, AppConfig config)
    {
        return Path.Combine(
            SlotUserDataPaths.GetEffectiveUserDataDirectory(slot, config),
            "User",
            "globalStorage",
            "storage.json");
    }

    private static VscodeLayoutPreference ReadLayoutPreference(JsonObject root)
    {
        return new VscodeLayoutPreference
        {
            SideBarWidth = FirstPositiveInt(
                GetIntValue(root, "windowSplashWorkspaceOverride", "layoutInfo", "sideBarWidth"),
                GetIntValue(root, "windowSplash", "layoutInfo", "sideBarWidth")),
            AuxiliaryBarWidth = FirstPositiveInt(
                GetIntValue(root, "windowSplashWorkspaceOverride", "layoutInfo", "auxiliaryBarWidth"),
                GetIntValue(root, "windowSplash", "layoutInfo", "auxiliaryBarWidth")),
            AuxiliarySideBarWidth = GetAuxiliarySideBarWidth(root)
        };
    }

    private static int? FirstPositiveInt(params int?[] values)
    {
        return values.FirstOrDefault(value => value > 0);
    }

    private static int? GetIntValue(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        return node is null ? null : TryGetInt(node);
    }

    private static JsonNode? GetNode(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = current?[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static JsonObject EnsureObjectPath(JsonObject root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        return current;
    }

    private static bool SetIntValue(JsonObject root, int? value, params string[] path)
    {
        if (value is not > 0 || path.Length == 0)
        {
            return false;
        }

        var parent = EnsureObjectPath(root, path[..^1]);
        var key = path[^1];
        var current = parent[key] is null ? null : TryGetInt(parent[key]!);
        if (current == value)
        {
            return false;
        }

        parent[key] = value.Value;
        return true;
    }

    private static int? GetAuxiliarySideBarWidth(JsonObject root)
    {
        var node = GetNode(root, "windowSplashWorkspaceOverride", "layoutInfo", "auxiliarySideBarWidth");
        if (node is JsonArray array && array.Count > 0)
        {
            return TryGetInt(array[0]);
        }

        return TryGetInt(node);
    }

    private static bool SetAuxiliarySideBarWidth(JsonObject root, int? value)
    {
        if (value is not > 0)
        {
            return false;
        }

        var layoutInfo = EnsureObjectPath(root, "windowSplashWorkspaceOverride", "layoutInfo");
        if (layoutInfo["auxiliarySideBarWidth"] is JsonArray array)
        {
            var current = array.Count > 0 ? TryGetInt(array[0]) : null;
            if (current == value)
            {
                return false;
            }

            if (array.Count == 0)
            {
                array.Add(value.Value);
            }
            else
            {
                array[0] = value.Value;
            }

            return true;
        }

        var existing = layoutInfo["auxiliarySideBarWidth"] is null ? null : TryGetInt(layoutInfo["auxiliarySideBarWidth"]!);
        if (existing == value)
        {
            return false;
        }

        layoutInfo["auxiliarySideBarWidth"] = value.Value;
        return true;
    }

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            try
            {
                return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
