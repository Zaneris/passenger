using System.Runtime.CompilerServices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>An autoload of a Godot project as its page shows it: its name and line (from zero) in project.godot, the script or
/// scene it loads (res:// and on disk, null where there is none), and whether it is a global the project's scripts name.</summary>
public sealed record GodotPageAutoload(string Name, int Line, string? Res, string? File, bool Global);

/// <summary>A layer project.godot names as its page shows it: its kind in words ("2D physics"), its number, its name, its
/// line (from zero) and its kind as project.godot writes it (<see cref="GodotDefinitions.LayerKinds"/>).</summary>
public sealed record GodotPageLayer(string Kind, int Number, string Name, int Line, string Written);

/// <summary>A scene of a Godot project its page offers for the main scene: its res:// path and the uid its header gives.</summary>
public sealed record GodotPageScene(string Res, string? Uid);

/// <summary>A change of project.godot its page asks for (<see cref="GodotPageEdits"/>): the question's title, its words and
/// its answer's, the title of its command of Undo, what is said where the person leaves it, where it changes nothing and
/// once written, and the change of the file's text, null where it changes nothing.</summary>
public sealed record GodotPageEdit(string Asked, string Question, string Yes, string Command, string Left, string Unchanged, string Done, Func<string, string?> Change)
{
    /// <summary>Why the page makes no such change, said before anything is asked; null where it asks.</summary>
    public string? Refused { get; init; }
}

/// <summary>An input action of project.godot as its page shows it: its name, its line (from zero) and its events in words.</summary>
public sealed record GodotPageAction(string Name, int Line, IReadOnlyList<string> Events);

/// <summary>An export preset as its page shows it: the preset, the templates an export of each kind lacks, and whether they
/// were looked for (a Godot that runs the project found, and the folder of its version's templates known).</summary>
public sealed record GodotPagePreset(GodotPreset Preset, IReadOnlyList<string> MissingRelease, IReadOnlyList<string> MissingDebug, bool Checked);

/// <summary>What a Godot project's page shows, read off the window's thread: its name; the Godot found, in words, and whether
/// it runs the project; the main scene; the autoloads; the input map, each action with its events; the export presets and
/// the folder their templates are looked for in; the plugins under addons/; whether Godot's editor opens its scripts in
/// this Passenger, in words; the layers named; and the project's text scenes, which the main scene is chosen from.</summary>
public sealed record GodotPageFacts(
    string Folder,
    string? Name,
    string Godot,
    bool GodotRuns,
    (string Res, int Line, string? File)? MainScene,
    IReadOnlyList<GodotPageAutoload> Autoloads,
    IReadOnlyList<GodotPageAction> Actions,
    IReadOnlyList<GodotPagePreset> Presets,
    string? Templates,
    IReadOnlyList<GodotPlugin> Plugins,
    string HandOff,
    bool HandedOff,
    IReadOnlyList<GodotPageLayer> Layers,
    IReadOnlyList<GodotPageScene> Scenes);

/// <summary>The pages of a window's Godot projects, one for each project.godot open (<see cref="GodotPageFile"/>), and what
/// they do through the window: read a project's facts, go to a place in a file of it, open a file, run an action, enable or
/// disable a plugin and change project.godot as its rows ask (<see cref="GodotPageEdits"/>), asked first; each read again as
/// its files change on disk (<see cref="Follows"/>). Made and asked on the UI thread.</summary>
public sealed class GodotProjectPages
{
    private readonly ConditionalWeakTable<OpenDocument, GodotPageFile> files = [];

    /// <summary>A project's facts as they stand now, by its folder.</summary>
    public required Func<string, Task<GodotPageFacts>> Read { get; init; }

    /// <summary>A file at a line (from zero) shown in the editor.</summary>
    public required Action<string, int> GoTo { get; init; }

    /// <summary>A file opened in the editor.</summary>
    public required Action<string> Open { get; init; }

    /// <summary>A window's action run, by its id.</summary>
    public required Action<string> Run { get; init; }

    /// <summary>The export of a project's preset asked for, by the project's folder and the preset's name.</summary>
    public required Action<string, string> Export { get; init; }

    /// <summary>A window's action's title as its registry gives it, by its id.</summary>
    public required Func<string, string> TitleOf { get; init; }

    /// <summary>A plugin enabled or disabled in its project's project.godot, asked first; null once written, else why not.</summary>
    public required Func<string, GodotPlugin, bool, Task<string?>> SetPlugin { get; init; }

    /// <summary>A change of a project's project.godot, by the project's folder, asked first; null once written, else why not.</summary>
    public required Func<string, GodotPageEdit, Task<string?>> Edit { get; init; }

    /// <summary>Whether a file written on disk changes what a page shows beside its project.godot: the export presets, or a
    /// plugin's plugin.cfg.</summary>
    public static bool Follows(string path) => Path.GetFileName(path) is var name
        && (name.Equals(GodotExports.FileName, StringComparison.OrdinalIgnoreCase) || name.Equals(GodotPlugins.ConfigName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(GodotProject.FileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The page of an open project.godot; null for any other file.</summary>
    public GodotPageFile? For(OpenDocument document) =>
        Path.GetFileName(document.Path).Equals(GodotProject.FileName, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(document.Path) is { } folder
            ? files.GetValue(document, d => new GodotPageFile(this, d, folder)) : null;

    /// <summary>Every page read again where it shows (a template installed, an export preset written).</summary>
    public void Refresh()
    {
        UiThread.Check();
        foreach ((_, GodotPageFile file) in files)
        {
            file.Follow(again: true);
        }
    }
}

/// <summary>A project.godot's page: whether its tab shows the page or the text under it, and the project's facts as last read,
/// read again while the page shows as the text pauses from typing and as it is shown.</summary>
public sealed class GodotPageFile
{
    private static readonly TimeSpan TypingSettle = TimeSpan.FromMilliseconds(400);

    private readonly Debounce typing;
    private bool stale = true;
    private int reads;

    internal GodotPageFile(GodotProjectPages pages, OpenDocument document, string folder)
    {
        Pages = pages;
        Document = document;
        Folder = folder;
        typing = new Debounce(TypingSettle, () => ShowsPage && stale ? ReadAsync() : Task.CompletedTask);
        document.Text.Changed += (_, _) =>
        {
            stale = true;
            typing.Restart();
        };
    }

    public GodotProjectPages Pages { get; }

    public OpenDocument Document { get; }

    /// <summary>The Godot project's folder, where its project.godot is.</summary>
    public string Folder { get; }

    /// <summary>Whether its tab shows the page rather than the text.</summary>
    public bool ShowsPage { get; private set; }

    /// <summary>The facts last read; null until the first read lands.</summary>
    public GodotPageFacts? Facts { get; private set; }

    /// <summary>Whether a read is under way.</summary>
    public bool IsReading => reads > 0;

    /// <summary>Raised on the UI thread as the page or text is chosen, or the facts change.</summary>
    public event EventHandler? Changed;

    /// <summary>The page or the text shown.</summary>
    public void Show(bool page)
    {
        UiThread.Check();
        if (page == ShowsPage)
        {
            return;
        }
        ShowsPage = page;
        Changed?.Invoke(this, EventArgs.Empty);
        Follow();
    }

    /// <summary>The page is about to show: read now when the project may have changed since.</summary>
    public void Follow(bool again = false)
    {
        stale |= again;
        if (stale && ShowsPage)
        {
            IdeErrors.Observe(ReadAsync());
        }
    }

    private async Task ReadAsync()
    {
        stale = false;
        reads++;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            Facts = await Pages.Read(Folder);
        }
        finally
        {
            reads--;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
