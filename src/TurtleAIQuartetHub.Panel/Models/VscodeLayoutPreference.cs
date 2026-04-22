using System.Text.Json.Serialization;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed record class VscodeLayoutPreference
{
    public static VscodeLayoutPreference Empty { get; } = new();

    public int? SideBarWidth { get; init; }

    public int? AuxiliaryBarWidth { get; init; }

    public int? AuxiliarySideBarWidth { get; init; }

    [JsonIgnore]
    public bool HasAnyValue => SideBarWidth > 0 || AuxiliaryBarWidth > 0 || AuxiliarySideBarWidth > 0;
}
