using System.IO;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public static class SlotUserDataPaths
{
    public static string GetUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        var safeSlotName = new string(slot.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeSlotName))
        {
            safeSlotName = "slot";
        }

        return Path.Combine(config.StateDirectory, "user-data", safeSlotName);
    }
}
