using System.Text.Json.Nodes;
using Passenger.App.Editors;
using Passenger.App.State;
using Passenger.Editor;

namespace Passenger.App.Workspace;

/// <summary>What a folder's window had open, kept in the folder's place in the store: the editors' splits, tabs (pinned
/// or not) and carets, the editor windows, the recent files, the projects unloaded, the target frameworks chosen, the
/// files' folds, the Explorer's view and whether Godot's script editor was offered, so opening the folder again brings them
/// back. Only the window a person uses keeps it, as with the layout.</summary>
public sealed record WorkspaceState : IStateDocument<WorkspaceState>
{
    public EditorLayout? Editors { get; init; }

    /// <summary>The editor windows (tabs taken out into windows of their own), each where it stood with its splits.</summary>
    public List<KeptEditorWindow> EditorWindows { get; init; } = [];

    /// <summary>The files opened lately, the latest first, for Recent Files and Go to File.</summary>
    public List<string> RecentFiles { get; init; } = [];

    /// <summary>The projects unloaded in the Solution view, by path.</summary>
    public List<string> UnloadedProjects { get; init; } = [];

    /// <summary>The target framework chosen for each multi-targeted project, by its path (<see cref="TargetFrameworkChoices"/>).</summary>
    public Dictionary<string, string> TargetFrameworks { get; init; } = [];

    /// <summary>Where each project of devices, emulators or simulators runs, by its path (<see cref="Run.Devices.DeviceChoices"/>).</summary>
    public Dictionary<string, Run.Devices.KeptTarget> RunTargets { get; init; } = [];

    /// <summary>The folds of the files opened lately, each as it closed, the latest first.</summary>
    public List<KeptFolds> Folds { get; init; } = [];

    /// <summary>Whether the banner's offer of Passenger as Godot's script editor was taken or dismissed, never to come again.</summary>
    public bool GodotEditorOffered { get; init; }

    /// <summary>The project style whose C# script templates for Godot were last written or offered, which a style changed
    /// since offers to write again, once.</summary>
    public string? GodotTemplatesStyle { get; init; }

    /// <summary>The Explorer's view the person last switched to, which it opens in: the Solution view unless they chose the
    /// Files view.</summary>
    public Explorer.ExplorerViewKind ExplorerView { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>A tab or a path without its file is left out, and a window without editors; the editors bring the rest (a
    /// caret, a split's share, a window with no room, which opens over the IDE, or off every screen) into range as they
    /// open them. A file's folds are left out without its path, and a fold that is not a start and an end in order.</summary>
    public WorkspaceState Clamped() => this with
    {
        ExplorerView = Enum.IsDefined(ExplorerView) ? ExplorerView : Explorer.ExplorerViewKind.Solution,
        Folds = [.. (Folds ?? []).Where(f => f is { Path.Length: > 0 }).Take(FoldMemory.Kept).Select(f => f with
        {
            Folded = [.. (f.Folded ?? []).Where(r => r is [>= 0 and var start, var end] && end > start)],
        })],
        Editors = Editors is { } editors ? Clamp(editors) : null,
        EditorWindows = [.. (EditorWindows ?? []).Where(w => w?.Editors is not null).Select(w => w with
        {
            Editors = Clamp(w.Editors!),
            Bounds = w.Bounds is [_, _, > 0, > 0] ? w.Bounds : null,
        })],
        RecentFiles = [.. (RecentFiles ?? []).Where(f => !string.IsNullOrEmpty(f))],
        UnloadedProjects = [.. (UnloadedProjects ?? []).Where(p => !string.IsNullOrEmpty(p))],
        TargetFrameworks = (TargetFrameworks ?? []).Where(p => p.Key is { Length: > 0 } && p.Value is { Length: > 0 })
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        RunTargets = (RunTargets ?? []).Where(p => p.Key is { Length: > 0 } && p.Value is { Key.Length: > 0, Name.Length: > 0 } t
                && Enum.IsDefined(t.Platform) && Enum.IsDefined(t.Kind))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };

    private static EditorLayout Clamp(EditorLayout layout) => layout with
    {
        First = layout.First is { } first ? Clamp(first) : null,
        Second = layout.Second is { } second ? Clamp(second) : null,
        Tabs = layout.Tabs is { } tabs ? [.. tabs.Where(t => t is { Path.Length: > 0 })] : null,
    };
}

/// <summary>A file's folds as it closed: the regions folded, each its start and end in the file's text.</summary>
public sealed record KeptFolds
{
    public string Path { get; init; } = string.Empty;

    public List<int[]> Folded { get; init; } = [];
}

/// <summary>The folds of the files a folder's window opened: each file's as it last closed, the latest first, so one
/// opened again opens folded as it was, after a restart too (the window keeps them in
/// <see cref="WorkspaceState.Folds"/>); the oldest go past <see cref="Kept"/>.</summary>
public sealed class FoldMemory
{
    public const int Kept = 200;

    private readonly List<KeptFolds> files = [];

    /// <summary>Which regions a file opens folded: those it closed with, by where they start and end; null for a file
    /// it has none of.</summary>
    public Func<FoldRegion, bool>? Of(string path)
    {
        if (files.FirstOrDefault(f => f.Path == path) is not { } kept)
        {
            return null;
        }
        HashSet<(int Start, int End)> folded = [.. kept.Folded.Select(f => (f[0], f[1]))];
        return region => folded.Contains((region.Start, region.End));
    }

    /// <summary>Keeps a file's folds as they are, once its regions have been found.</summary>
    public void Remember(OpenDocument document)
    {
        if (!document.FoldsFound)
        {
            return;
        }
        files.RemoveAll(f => f.Path == document.Path);
        files.Insert(0, new KeptFolds { Path = document.Path, Folded = [.. document.FoldRegions.Where(r => r.Folded).Select(r => new[] { r.Start, r.End })] });
        if (files.Count > Kept)
        {
            files.RemoveRange(Kept, files.Count - Kept);
        }
    }

    /// <summary>Every file's folds, the open files' as they stand.</summary>
    public List<KeptFolds> Capture(IEnumerable<OpenDocument> open)
    {
        foreach (OpenDocument document in open.Reverse())
        {
            Remember(document);
        }
        return [.. files];
    }

    public void Restore(IEnumerable<KeptFolds> kept)
    {
        files.Clear();
        files.AddRange(kept.Take(Kept));
    }

    public void Clear() => files.Clear();
}
