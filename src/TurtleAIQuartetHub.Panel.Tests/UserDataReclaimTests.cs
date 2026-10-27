using System.Diagnostics;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class UserDataReclaimTests
{
    public static void Run(List<string> failures)
    {
        VerifyJunctionTargetSurvivesReclaim(failures);
    }

    // 回収対象の user-data 配下にジャンクションがあっても、リンク先の実データは消さない。
    private static void VerifyJunctionTargetSurvivesReclaim(List<string> failures)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TurtleHub_Reclaim_{Guid.NewGuid():N}");
        try
        {
            var stateDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "state")).FullName;
            var slotDirectory = Directory.CreateDirectory(Path.Combine(stateDirectory, "user-data", "A")).FullName;
            File.WriteAllBytes(Path.Combine(slotDirectory, "cache.bin"), new byte[2048]);

            var outsideDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "outside")).FullName;
            var keepPath = Path.Combine(outsideDirectory, "keep.txt");
            File.WriteAllText(keepPath, "must survive");

            var junctionPath = Path.Combine(slotDirectory, "linked");
            if (!TryCreateJunction(junctionPath, outsideDirectory))
            {
                failures.Add("ジャンクション回収テスト: mklink /J でジャンクションを作成できませんでした。");
                return;
            }

            // CodeCommand に code を含めないことで、実機の %APPDATA%\Code を回収対象にしない。
            var config = new AppConfig
            {
                CodeCommand = "notepad",
                StateDirectory = stateDirectory,
                UseDedicatedUserDataDirs = false
            };

            var reclaimed = SlotUserDataPaths.ReclaimUnusedUserData(config);

            Check(failures, "ジャンクション先のファイルは回収で削除しない", File.Exists(keepPath));
            Check(failures, "共有プロファイル運用では残存 user-data を回収する",
                !Directory.Exists(Path.Combine(stateDirectory, "user-data")));
            Check(failures, "回収量はリンク先を含めず user-data 内の実ファイルだけを数える", reclaimed == 2048);
        }
        finally
        {
            try
            {
                // リンク先を辿らず消すため、ジャンクションが残っていれば先に外す。
                var leftoverJunction = Path.Combine(tempRoot, "state", "user-data", "A", "linked");
                if (Directory.Exists(leftoverJunction))
                {
                    Directory.Delete(leftoverJunction, false);
                }

                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // 一時フォルダの後始末失敗はテスト結果に含めない。
            }
        }
    }

    internal static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(10000);
        return process.HasExited && process.ExitCode == 0 && Directory.Exists(junctionPath);
    }

    private static void Check(List<string> failures, string name, bool condition)
    {
        if (!condition)
        {
            failures.Add(name);
        }
    }
}
