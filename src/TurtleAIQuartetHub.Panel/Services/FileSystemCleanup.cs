using System.IO;

namespace TurtleAIQuartetHub.Panel.Services;

// 再生成できるキャッシュや更新ファイルを消すための共通削除処理。
// リンク先の実データを巻き込まないことと、ロック中のファイルで止まらないことを優先する。
internal static class FileSystemCleanup
{
    // 削除できたバイト数を返す。消せなかったファイル・フォルダは残し、次回の回収に回す。
    public static long DeleteDirectoryBestEffort(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        var reclaimed = 0L;
        try
        {
            // ジャンクション/シンボリックリンクは辿らずリンク自体だけ外す。
            // 辿ると、キャッシュ外へ逃がした実データまで削除してしまう。
            if (new DirectoryInfo(directoryPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(directoryPath, false);
                return 0;
            }

            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                reclaimed += DeleteFileBestEffort(filePath);
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
            {
                reclaimed += DeleteDirectoryBestEffort(childDirectory);
            }

            Directory.Delete(directoryPath, false);
        }
        catch (IOException)
        {
            // ロック中のファイルが残ったフォルダは次回の回収に回す。階層ごとに
            // 「ディレクトリが空ではありません」のスタックトレースで panel.log を埋めない。
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return reclaimed;
    }

    public static long DeleteFileBestEffort(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            // シンボリックリンクの Length はリンク自体の長さなので、リンク先の容量を回収量に数えない。
            var length = info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? 0 : info.Length;
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }

            File.Delete(filePath);
            return length;
        }
        catch
        {
            // ロック中のキャッシュファイルは次回の回収に回す。大量の個別失敗で panel.log を埋めない。
            return 0;
        }
    }

    // ディレクトリ配下の実ファイル合計。リンクは辿らない。
    public static long MeasureDirectoryBytes(string directoryPath, CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var total = 0L;
        foreach (var file in new DirectoryInfo(directoryPath).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 走査中に消えたファイルは数えない。
            }
        }

        return total;
    }
}
