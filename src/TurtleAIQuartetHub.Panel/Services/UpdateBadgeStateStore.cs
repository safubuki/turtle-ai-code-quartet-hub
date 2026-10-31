using System.IO;
using System.Text.Json;

namespace TurtleAIQuartetHub.Panel.Services;

/// <summary>
/// タイトルバーの更新バッジ（赤丸）の既読状態を保存する。バッジごとのキーに「最後に確認した時刻」だけを持ち、
/// 通知の内容そのものは保存しない。Pull Request 以外のアイコンも同じキー方式で使える。
/// </summary>
public sealed class UpdateBadgeStateStore
{
    public const string PullRequestsKey = "pullRequests";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Dictionary<string, DateTimeOffset> _lastSeen;

    public UpdateBadgeStateStore(string stateDirectory)
    {
        _filePath = Path.Combine(stateDirectory, "update-badges.json");
        _lastSeen = Load(_filePath);
    }

    public DateTimeOffset? GetLastSeen(string key)
    {
        return _lastSeen.TryGetValue(key, out var value) ? value : null;
    }

    public void MarkSeen(string key, DateTimeOffset seenAt)
    {
        if (_lastSeen.TryGetValue(key, out var current) && current >= seenAt)
        {
            return;
        }

        _lastSeen[key] = seenAt;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var document = new Document { LastSeen = new Dictionary<string, DateTimeOffset>(_lastSeen) };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static Dictionary<string, DateTimeOffset> Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath)
                && JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath)) is { LastSeen: { } lastSeen })
            {
                return new Dictionary<string, DateTimeOffset>(lastSeen, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write(ex);
        }

        return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    }

    private sealed class Document
    {
        public Dictionary<string, DateTimeOffset>? LastSeen { get; set; }
    }
}
