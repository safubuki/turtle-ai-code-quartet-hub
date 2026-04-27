using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

var options = CliOptions.Parse(args);
var config = AppConfig.Load();
var slotsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TurtleAIQuartetHub",
    "slots.json");

if (!File.Exists(slotsPath))
{
    Console.Error.WriteLine($"slots.json not found: {slotsPath}");
    return 1;
}

var storedSlots = await StoredSlotState.LoadAsync(slotsPath);
var targets = storedSlots
    .Where(slot => string.IsNullOrWhiteSpace(options.SlotName)
        || string.Equals(options.SlotName, slot.Name, StringComparison.OrdinalIgnoreCase))
    .ToList();

if (targets.Count == 0)
{
    Console.Error.WriteLine("No matching slots were found.");
    return 1;
}

var windowEnumerator = new WindowEnumerator();
var visibleWindows = windowEnumerator.GetVsCodeWindows();
var matches = WindowMatchResolver.Resolve(targets, visibleWindows, config);
var detector = new AiStatusDetector();
var results = new List<ProbeResult>();

foreach (var target in targets.OrderBy(slot => slot.Name, StringComparer.OrdinalIgnoreCase))
{
    if (!matches.TryGetValue(target.Name, out var match))
    {
        results.Add(new ProbeResult(
            target.Name,
            target.AssignedPath,
            AiStatus.Unknown.ToString(),
            "VS Code ウィンドウを現在の表示から解決できませんでした。",
            null,
            false,
            0,
            string.Empty,
            string.Empty));
        continue;
    }

    var slot = new WindowSlot(new SlotConfig
    {
        Name = target.Name,
        Path = target.AssignedPath
    })
    {
        PanelTitle = target.PanelTitle,
        SavedWorkspacePath = target.SavedWorkspacePath,
        SavedWorkspaceConfirmed = target.SavedWorkspaceConfirmed,
        CurrentWorkspacePath = target.EffectiveWorkspacePath,
        WindowHandle = match.Handle,
        WindowTitle = match.Title
    };

    var snapshot = detector.Detect(slot, config);
    results.Add(new ProbeResult(
        target.Name,
        target.AssignedPath,
        snapshot.Status.ToString(),
        snapshot.Detail,
        snapshot.EventAt,
        true,
        match.Handle.ToInt64(),
        match.Title,
        match.Reason));
}

if (options.Json)
{
    var payload = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(payload);
    return 0;
}

foreach (var result in results)
{
    var eventText = result.EventAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    var handleText = result.WindowHandle == 0 ? "-" : $"0x{result.WindowHandle:X}";
    var resolvedText = result.Resolved ? "resolved" : "unresolved";
    Console.WriteLine($"{result.Slot}|{resolvedText}|{handleText}|{result.Status}|{eventText}|{result.WindowTitle}|{result.Detail}");
}

return 0;

file sealed record ProbeResult(
    string Slot,
    string Path,
    string Status,
    string Detail,
    DateTimeOffset? EventAt,
    bool Resolved,
    long WindowHandle,
    string WindowTitle,
    string MatchReason);

file sealed record StoredSlotState(
    string Name,
    string PanelTitle,
    string AssignedPath,
    string SavedWorkspacePath,
    bool SavedWorkspaceConfirmed)
{
    public string EffectiveWorkspacePath =>
        SavedWorkspaceConfirmed && !string.IsNullOrWhiteSpace(SavedWorkspacePath)
            ? SavedWorkspacePath
            : AssignedPath;

    public static async Task<List<StoredSlotState>> LoadAsync(string slotsPath)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(slotsPath));
        if (!document.RootElement.TryGetProperty("VisibleSlots", out var visibleSlots))
        {
            return [];
        }

        return visibleSlots
            .EnumerateArray()
            .Select(item => new StoredSlotState(
                item.GetProperty("Name").GetString() ?? string.Empty,
                item.GetProperty("PanelTitle").GetString() ?? string.Empty,
                item.GetProperty("AssignedPath").GetString() ?? string.Empty,
                item.GetProperty("SavedWorkspacePath").GetString() ?? string.Empty,
                item.GetProperty("SavedWorkspaceConfirmed").GetBoolean()))
            .ToList();
    }
}

file sealed record WeightedFragment(string Fragment, int Weight, string Reason);

file sealed record WindowMatch(IntPtr Handle, string Title, int Score, string Reason);

file static class WindowMatchResolver
{
    public static IReadOnlyDictionary<string, WindowMatch> Resolve(
        IReadOnlyList<StoredSlotState> slots,
        IReadOnlyList<WindowInfo> windows,
        AppConfig config)
    {
        var remainingWindows = windows.ToDictionary(window => window.Handle, window => window);
        var matches = new Dictionary<string, WindowMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots
                     .Select(slot => (Slot: slot, Fragments: BuildFragments(slot, config)))
                     .OrderByDescending(item => item.Fragments.Count)
                     .ThenBy(item => item.Slot.Name, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = remainingWindows.Values
                .Select(window => ScoreWindow(window, slot.Fragments))
                .Where(match => match is not null)
                .Cast<WindowMatch>()
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Title.Length)
                .ToList();

            if (candidates.Count == 0)
            {
                continue;
            }

            if (candidates.Count > 1 && candidates[0].Score == candidates[1].Score)
            {
                continue;
            }

            var best = candidates[0];
            matches[slot.Slot.Name] = best;
            remainingWindows.Remove(best.Handle);
        }

        return matches;
    }

    private static List<WeightedFragment> BuildFragments(StoredSlotState slot, AppConfig config)
    {
        var fragments = new List<WeightedFragment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFragmentsFromWorkspace(slot.EffectiveWorkspacePath, 140, "saved-workspace", fragments, seen);
        AddFragmentsFromWorkspace(slot.AssignedPath, 110, "assigned-path", fragments, seen);

        if (!slot.SavedWorkspaceConfirmed || string.IsNullOrWhiteSpace(slot.SavedWorkspacePath))
        {
            AddFragmentsFromWorkspace(VscodeWorkspaceState.TryReadLastWorkspacePath(slot.Name, config), 90, "workspace-storage", fragments, seen);
        }

        var trimmedTitle = slot.PanelTitle.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedTitle) && seen.Add(trimmedTitle))
        {
            fragments.Add(new WeightedFragment(trimmedTitle, 70, "panel-title"));
        }

        return fragments;
    }

    private static void AddFragmentsFromWorkspace(
        string? workspacePath,
        int baseWeight,
        string reason,
        List<WeightedFragment> fragments,
        HashSet<string> seen)
    {
        foreach (var fragment in GetWorkspaceTitleCandidates(workspacePath))
        {
            if (seen.Add(fragment))
            {
                fragments.Add(new WeightedFragment(fragment, baseWeight + Math.Min(fragment.Length, 20), reason));
            }
        }
    }

    private static IEnumerable<string> GetWorkspaceTitleCandidates(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            yield break;
        }

        var trimmed = workspacePath.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) && !absoluteUri.IsFile)
        {
            if (!string.IsNullOrWhiteSpace(absoluteUri.Host))
            {
                yield return absoluteUri.Host;
            }

            var remotePath = absoluteUri.AbsolutePath.TrimEnd('/', '\\');
            var remoteLeaf = Path.GetFileName(remotePath);
            if (!string.IsNullOrWhiteSpace(remoteLeaf))
            {
                yield return remoteLeaf;
            }

            yield break;
        }

        var normalized = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        if (Path.HasExtension(normalized))
        {
            var fileStem = Path.GetFileNameWithoutExtension(normalized);
            if (!string.IsNullOrWhiteSpace(fileStem) && !string.Equals(fileStem, fileName, StringComparison.OrdinalIgnoreCase))
            {
                yield return fileStem;
            }
        }
    }

    private static WindowMatch? ScoreWindow(WindowInfo window, IReadOnlyList<WeightedFragment> fragments)
    {
        var score = 0;
        var reasons = new List<string>();

        foreach (var fragment in fragments)
        {
            if (!window.Title.Contains(fragment.Fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            score += fragment.Weight;
            reasons.Add($"{fragment.Reason}:{fragment.Fragment}");
        }

        if (score == 0)
        {
            return null;
        }

        return new WindowMatch(window.Handle, window.Title, score, string.Join(", ", reasons));
    }
}

file sealed class CliOptions
{
    public string? SlotName { get; private init; }
    public bool Json { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        string? slotName = null;
        var json = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--slot" when index + 1 < args.Length:
                    slotName = args[++index];
                    break;
                case "--json":
                    json = true;
                    break;
            }
        }

        return new CliOptions
        {
            SlotName = slotName,
            Json = json
        };
    }
}
