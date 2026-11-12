using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel.Models;

/// <summary>通知フックの保存先と、その時点で利用できる閲覧操作。</summary>
public sealed record AiNotifyHookLocation(
    string Tool,
    string DisplayName,
    AiNotifyHookStatus Status,
    string StatusText,
    string StatusDetailToolTip,
    string SettingsPath,
    string DisplayPath,
    string FolderPath,
    bool CanOpenFolder,
    bool CanViewSettings)
{
    public string ToolTip => SettingsPath;
    public string FolderToolTip => CanOpenFolder ? $"設定フォルダを開く: {FolderPath}" : $"設定フォルダはまだありません: {FolderPath}";
    public string SettingsToolTip => CanViewSettings ? $"設定内容を読み取り専用で表示: {SettingsPath}" : $"設定ファイルはまだありません: {SettingsPath}";
}

/// <summary>原文を変更せず、表示上限内だけを読み取った結果。</summary>
public sealed record AiNotifyHookSettingsPreview(bool Succeeded, string Content, string Message, bool IsTruncated);
