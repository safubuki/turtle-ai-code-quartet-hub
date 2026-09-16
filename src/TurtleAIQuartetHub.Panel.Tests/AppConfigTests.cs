using TurtleAIQuartetHub.Panel.Models;

internal static class AppConfigTests
{
    public static void Run(List<string> failures)
    {
        Verify(
            "旧 Gemini CLI ID は Antigravity CLI ID へ移行する",
            () => string.Equals(
                AppConfig.NormalizeApplicationId("gemini"),
                AppConfig.AntigravityCliApplicationId,
                StringComparison.OrdinalIgnoreCase),
            failures);

        Verify(
            "既定の Workspace CLI は agy を使う Antigravity CLI である",
            () =>
            {
                var config = new AppConfig();
                config.Normalize();
                var application = config.Applications.SingleOrDefault(app =>
                    string.Equals(app.Id, AppConfig.AntigravityCliApplicationId, StringComparison.OrdinalIgnoreCase));
                return application is not null
                    && application.Kind == ApplicationKind.WorkspaceCli
                    && application.Command == "agy"
                    && application.Detection.Commands.Contains("agy", StringComparer.OrdinalIgnoreCase)
                    && config.Applications.All(app => !string.Equals(app.Id, "gemini", StringComparison.OrdinalIgnoreCase));
            },
            failures);

        Verify(
            "旧 Gemini CLI 設定とスロットは agy の既定定義へ置換する",
            () =>
            {
                var config = new AppConfig
                {
                    DefaultWorkspaceApplicationId = "gemini",
                    Applications =
                    [
                        new ToolApplicationConfig
                        {
                            Id = "gemini",
                            DisplayName = "Gemini CLI",
                            ShortName = "Gemini",
                            Kind = ApplicationKind.WorkspaceCli,
                            Command = "gemini",
                            Detection = new ApplicationDetectionConfig
                            {
                                Commands = ["gemini"]
                            }
                        }
                    ],
                    Slots =
                    [
                        new SlotConfig { Name = "A", ApplicationId = "gemini" }
                    ]
                };

                config.Normalize();
                var application = config.Applications.SingleOrDefault(app =>
                    string.Equals(app.Id, AppConfig.AntigravityCliApplicationId, StringComparison.OrdinalIgnoreCase));
                return config.DefaultWorkspaceApplicationId == AppConfig.AntigravityCliApplicationId
                    && config.Slots[0].ApplicationId == AppConfig.AntigravityCliApplicationId
                    && application is not null
                    && application.DisplayName == "Antigravity CLI"
                    && application.Command == "agy"
                    && application.Detection.Commands.SequenceEqual(["agy"], StringComparer.OrdinalIgnoreCase);
            },
            failures);

        Verify(
            "明示した Antigravity CLI コマンドは維持する",
            () =>
            {
                const string customCommand = @"C:\tools\agy.exe";
                var config = new AppConfig
                {
                    Applications =
                    [
                        new ToolApplicationConfig
                        {
                            Id = AppConfig.AntigravityCliApplicationId,
                            DisplayName = "Antigravity CLI Custom",
                            ShortName = "AGY",
                            Kind = ApplicationKind.WorkspaceCli,
                            Command = customCommand,
                            Detection = new ApplicationDetectionConfig
                            {
                                Commands = [customCommand]
                            }
                        }
                    ]
                };

                config.Normalize();
                var application = config.Applications.Single(app =>
                    string.Equals(app.Id, AppConfig.AntigravityCliApplicationId, StringComparison.OrdinalIgnoreCase));
                return application.Command == customCommand
                    && application.ShortName == "AGY"
                    && application.Detection.Commands.Contains(customCommand, StringComparer.OrdinalIgnoreCase);
            },
            failures);
    }

    private static void Verify(string name, Func<bool> assertion, List<string> failures)
    {
        try
        {
            if (!assertion())
            {
                failures.Add(name);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
