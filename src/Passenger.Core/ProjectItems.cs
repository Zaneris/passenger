using System.Xml.Linq;

namespace Passenger.Core;

/// <summary>A project file's items as its text holds them, read without MSBuild: which every build sees, what an item says
/// of a name, and the path a value names from the file's folder (<see cref="ItemPaths"/>).</summary>
public static class ProjectItems
{
    /// <summary>How a project file names its own folder, as Visual Studio begins every path of a shared project's items.</summary>
    public const string ThisFolder = "$(MSBuildThisFileDirectory)";

    public static bool IsItemGroup(XElement element) => element.Name.LocalName == "ItemGroup";

    public static bool Conditioned(XElement element) => element.Attributes().Any(a => a.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether every build sees an item: neither it nor its item group has a condition, and the group is the
    /// file's own, in no Choose or Target.</summary>
    public static bool Unconditioned(XElement item) =>
        !Conditioned(item) && item.Parent is { } group && IsItemGroup(group) && !Conditioned(group) && group.Parent?.Name.LocalName == "Project";

    /// <summary>An item's attribute or metadata element of a name, XML's entities read; null where it has neither.</summary>
    public static string? Value(XElement item, [IdentifierOf(typeof(MSBuildNames))] string name) =>
        (string?)item.Attribute(name) ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value;

    /// <summary>A value without the <see cref="ThisFolder"/> it may begin with, which names the file's own folder.</summary>
    public static string FromThisFolder(string written) =>
        written.StartsWith(ThisFolder, StringComparison.OrdinalIgnoreCase) ? written[ThisFolder.Length..] : written;

    /// <summary>What a value names from the file's folder (its own folder named as such, else relative, else whole), its
    /// path made whole (<see cref="ItemPath.FromFolder"/>); null for a list or one naming anything else MSBuild expands. An
    /// Exclude and an Import read a glob only <paramref name="once"/>, an <paramref name="exclude"/> its escaped wildcards
    /// as wildcards (<see cref="ItemPaths.Read"/>).</summary>
    public static ItemPath? PathOf(string folder, string written, bool once = false, bool exclude = false) =>
        ItemPaths.Read(FromThisFolder(written.Trim()).Replace('\\', '/'), once, exclude)?.FromFolder(folder);
}
