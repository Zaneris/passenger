using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Rider's keymaps as the files in Rider's jars write them, for the actions the keymap tests check: each
/// keymap's parent and its own list of an action's keys, empty where it takes the parent's away, and the keys plugins'
/// descriptors add to a keymap. An action's keys resolve as IntelliJ resolves them: the keymap's own list, else its
/// parent's, Mac OS X 10.5+ taking $default's with Ctrl and Cmd swapped; then what the descriptors add.</summary>
internal static class RiderKeymaps
{
    private const string Default = "$default";
    private const string MacOS = "Mac OS X 10.5+";

    private static readonly Dictionary<string, (string? Parent, Dictionary<string, string[]> Own)> Files = new()
    {
        [Default] = (null, new()
        {
            ["EditorDuplicate"] = ["control D"],
            ["CompareTwoFiles"] = ["control D"],
            ["Diff.ShowDiff"] = ["control D"],
            ["ParameterInfo"] = ["control P"],
            ["EditorToggleCase"] = ["control shift U"],
            ["Generate"] = ["alt INSERT"],
            ["NewElement"] = ["alt INSERT"],
            ["EditorUp"] = ["UP"],
            ["EditorDown"] = ["DOWN"],
            ["EditorLeft"] = ["LEFT"],
            ["EditorRight"] = ["RIGHT"],
            ["EditorTextStart"] = ["control HOME"],
            ["EditorTextEnd"] = ["control END"],
        }),
        [MacOS] = (Default, new()
        {
            ["Generate"] = ["meta N", "control ENTER"],
            ["NewElement"] = ["meta N", "control ENTER"],
            ["EditorUp"] = ["UP", "control P"],
            ["EditorDown"] = ["DOWN", "control N"],
            ["EditorLeft"] = ["LEFT", "control B"],
            ["EditorRight"] = ["RIGHT", "control F"],
        }),
        ["Visual Studio"] = (Default, new()
        {
            ["ParameterInfo"] = ["shift control SPACE"],
            ["NewElement"] = ["control N", "alt INSERT"],
        }),
        ["Visual Studio OSX"] = (MacOS, new()
        {
            ["Generate"] = ["control meta N"],
            ["NewElement"] = ["meta N", "alt INSERT"],
        }),
        ["VSCode"] = (Default, new()
        {
            ["EditorDuplicate"] = [],
            ["EditorDuplicateLines"] = ["shift alt down"],
            ["CompareTwoFiles"] = [],
            ["Diff.ShowDiff"] = [],
            ["ParameterInfo"] = ["shift ctrl space"],
            ["EditorToggleCase"] = [],
            ["EditorCloneCaretAbove"] = ["ctrl alt up", "shift ctrl alt up"],
            ["EditorCloneCaretBelow"] = ["ctrl alt down", "shift ctrl alt down"],
        }),
        ["VSCode OSX"] = (MacOS, new()
        {
            ["EditorDuplicate"] = [],
            ["EditorDuplicateLines"] = ["shift alt down"],
            ["CompareTwoFiles"] = [],
            ["Diff.ShowDiff"] = [],
            ["ParameterInfo"] = ["shift meta space"],
            ["EditorToggleCase"] = [],
            ["Generate"] = ["ctrl enter"],
            ["NewElement"] = ["ctrl enter"],
            ["EditorTextStart"] = ["meta up"],
            ["EditorTextEnd"] = ["meta down"],
            ["EditorCloneCaretAbove"] = ["shift meta alt up", "meta alt up"],
            ["EditorCloneCaretBelow"] = ["shift meta alt down", "meta alt down"],
        }),
    };

    /// <summary>The keys plugins' descriptors add, by keymap and action: Rider's own (intellij.rider.xml).</summary>
    private static readonly (string Keymap, string Action, string Keys)[] Descriptors =
    [
        ("Visual Studio", "CloneCaretAboveWithVirtualSpace", "alt shift UP"),
        ("Visual Studio OSX", "CloneCaretAboveWithVirtualSpace", "control shift UP"),
        ("Visual Studio", "CloneCaretBelowWithVirtualSpace", "alt shift DOWN"),
        ("Visual Studio OSX", "CloneCaretBelowWithVirtualSpace", "control shift DOWN"),
    ];

    /// <summary>The keymap of Rider's that a preset is on this platform.</summary>
    public static string Of(KeymapPreset preset) => (preset, OperatingSystem.IsMacOS()) switch
    {
        (KeymapPreset.VSCode, true) => "VSCode OSX",
        (KeymapPreset.VSCode, false) => "VSCode",
        (_, true) => "Visual Studio OSX",
        _ => "Visual Studio",
    };

    /// <summary>An action's keys in one of Rider's keymaps.</summary>
    public static IReadOnlyList<Shortcut> Resolve(string keymap, string action)
    {
        (string? parent, Dictionary<string, string[]> own) = Files[keymap];
        IEnumerable<Shortcut> keys = own.TryGetValue(action, out string[]? listed) ? listed.Select(Shortcut.Parse)
            : parent is null ? []
            : keymap == MacOS && parent == Default ? Resolve(parent, action).Select(Swapped)
            : Resolve(parent, action);
        IEnumerable<Shortcut> added = Descriptors.Where(d => d.Keymap == keymap && d.Action == action).Select(d => Shortcut.Parse(d.Keys));
        return [.. keys.Concat(added).Distinct()];
    }

    private static Shortcut Swapped(Shortcut shortcut) => new(Swapped(shortcut.First), shortcut.Second is { } second ? Swapped(second) : null);

    private static Keystroke Swapped(Keystroke stroke)
    {
        KeyModifiers others = stroke.Modifiers & ~(KeyModifiers.Control | KeyModifiers.Meta);
        KeyModifiers swapped = (stroke.Modifiers.HasFlag(KeyModifiers.Control) ? KeyModifiers.Meta : KeyModifiers.None)
            | (stroke.Modifiers.HasFlag(KeyModifiers.Meta) ? KeyModifiers.Control : KeyModifiers.None);
        return stroke with { Modifiers = others | swapped };
    }
}
