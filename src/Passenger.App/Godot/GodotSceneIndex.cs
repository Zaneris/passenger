using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>The text scenes and resources of the folder's Godot projects (<see cref="GodotScenes"/>), each project's found
/// by one walk of its folder the first time it is asked about (<see cref="FolderWalk"/>, which passes over its .godot
/// folder), then followed through the folder's watcher: a file written is read again, one come or gone put in or taken
/// out, the folder walked again only when changes were lost. An open file is read as the editor has it. Asked on the UI
/// thread; every read is made off it.</summary>
public sealed class GodotSceneIndex
{
    /// <summary>How long typing in an open scene must pause before the code read against it is read again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(600);

    private readonly Dictionary<string, Project> projects = new(PathNames.Comparer);
    private readonly Dictionary<OpenDocument, EventHandler<DocumentChangedEventArgs>> typed = [];
    private Debounce? typing;

    /// <summary>The open files' texts now, which an open scene is read from.</summary>
    public Func<OpenTexts> Texts { get; init; } = () => OpenTexts.None;

    /// <summary>The open folder's files as its index has them: its root and their paths from it.</summary>
    public Func<(string? Root, IReadOnlyList<string> Files)> Files { get; init; } = () => (null, []);

    /// <summary>The files of a Godot project's folder a res:// path may name: the folder's index's under it, but those in
    /// hidden folders (.godot's imports) and Godot's own beside each file (.import, .uid).</summary>
    public IReadOnlyList<string> FilesUnder(string folder)
    {
        (string? root, IReadOnlyList<string> files) = Files();
        if (root is null || !PathNames.Within(folder, root))
        {
            return [];
        }
        return [.. files.Select(f => Path.Combine(root, f)).Where(f => PathNames.IsUnder(f, folder) && !PathNames.Same(f, folder)
            && !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Any(n => n.StartsWith('.'))
            && !f.EndsWith(".import", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".uid", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A scene or resource of a project asked about came, went or was written, on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>A project's scenes and resources as they stand, by its Godot folder (canonical).</summary>
    public async Task<GodotScenes> ScenesAsync(string folder)
    {
        UiThread.Check();
        if (!projects.TryGetValue(folder, out Project? project))
        {
            projects[folder] = project = new Project(folder);
        }
        project.Walking ??= BackgroundWork.Run(() => FolderWalk.Files(folder, built: true).Where(GodotFile.IsText).ToHashSet(PathNames.Comparer));
        HashSet<string> paths = await project.Walking;
        project.Paths ??= paths;
        if (project.Come.Count > 0)
        {
            List<string> come = [.. project.Come];
            project.Come.Clear();
            foreach (string file in await BackgroundWork.Run(() => come.Where(Directory.Exists).SelectMany(f => FolderWalk.Files(f, built: true)).Where(GodotFile.IsText).ToList()))
            {
                project.Paths.Add(file);
                project.Dirty.Add(file);
            }
        }
        OpenTexts texts = Texts();
        List<(string Path, TextSnapshot? Open)> unread = [];
        foreach (string path in project.Paths)
        {
            TextSnapshot? open = texts.Open(path);
            if (!project.Read.TryGetValue(path, out (GodotFile File, TextSnapshot? From) read) || project.Dirty.Contains(path) || !ReferenceEquals(read.From, open))
            {
                unread.Add((path, open));
            }
        }
        if (unread.Count == 0 && project.Scenes is { } kept)
        {
            return kept;
        }
        IReadOnlyList<(string Path, TextSnapshot? Open, GodotFile? File)> parsed = await BackgroundWork.Run(() => (IReadOnlyList<(string, TextSnapshot?, GodotFile?)>)
            [.. unread.Select(u => (u.Path, u.Open, (u.Open?.ToString() ?? EditorArea.ReadText(u.Path)) is { } text ? GodotFile.Parse(text) : null))]);
        foreach ((string path, TextSnapshot? open, GodotFile? file) in parsed)
        {
            project.Dirty.Remove(path);
            if (file is null)
            {
                project.Read.Remove(path);
            }
            else
            {
                project.Read[path] = (file, open);
            }
        }
        foreach (string gone in project.Read.Keys.Where(p => !project.Paths.Contains(p)).ToList())
        {
            project.Read.Remove(gone);
        }
        Dictionary<string, GodotFile> files = new(StringComparer.Ordinal);
        foreach ((string path, (GodotFile file, _)) in project.Read)
        {
            if (GodotScenes.ResPathOf(folder, path) is { } res)
            {
                files[res] = file;
            }
        }
        return project.Scenes = new GodotScenes(folder, files);
    }

    /// <summary>Files written: a scene or resource of a project asked about is read again when next asked.</summary>
    public void FilesChanged(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (string path in paths.Where(GodotFile.IsText))
        {
            foreach (Project project in projects.Values.Where(p => PathNames.IsUnder(path, p.Folder)))
            {
                project.Dirty.Add(path);
                project.Scenes = null;
                changed = true;
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Files or folders come or gone: a scene or resource put in or taken out, a folder's walked for its own; with
    /// <paramref name="every"/> (changes lost) each project's folder walked again.</summary>
    public void CameOrWent(IEnumerable<string> paths, bool every = false)
    {
        bool changed = false;
        foreach (Project project in projects.Values)
        {
            if (every)
            {
                project.Walking = null;
                project.Paths = null;
                project.Scenes = null;
                changed = true;
                continue;
            }
            foreach (string path in paths.Where(p => PathNames.IsUnder(p, project.Folder)))
            {
                if (project.Paths is not { } known)
                {
                    break;
                }
                if (GodotFile.IsText(path))
                {
                    if (File.Exists(path) ? known.Add(path) | project.Dirty.Add(path) : known.Remove(path))
                    {
                        project.Scenes = null;
                        changed = true;
                    }
                }
                else if (Directory.Exists(path) || known.Any(k => PathNames.IsUnder(k, path)))
                {
                    // A folder gone takes its files with it; one come is walked for its own as the project is next asked about.
                    known.RemoveWhere(k => PathNames.IsUnder(k, path));
                    if (Directory.Exists(path))
                    {
                        project.Come.Add(path);
                    }
                    project.Scenes = null;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A file the editor opened: a scene's or resource's typing, once it pauses, is a change of its project's.</summary>
    public void Attach(OpenDocument document)
    {
        if (!GodotFile.IsText(document.Path) || typed.ContainsKey(document))
        {
            return;
        }
        EventHandler<DocumentChangedEventArgs> handler = (_, _) =>
        {
            if (projects.Values.Any(p => p.Paths?.Contains(document.Path) == true))
            {
                (typing ??= new Debounce(Pause, () =>
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                })).Restart();
            }
        };
        typed[document] = handler;
        document.Text.Changed += handler;
    }

    public void Detach(OpenDocument document)
    {
        if (typed.Remove(document, out EventHandler<DocumentChangedEventArgs>? handler))
        {
            document.Text.Changed -= handler;
        }
    }

    /// <summary>Its typing stopped for good: the window's session ended.</summary>
    public void Stop() => typing?.Stop();

    private sealed class Project(string folder)
    {
        public string Folder => folder;

        /// <summary>The walk of its folder for its scenes, begun the first time it is asked about.</summary>
        public Task<HashSet<string>>? Walking { get; set; }

        /// <summary>Its scenes' and resources' paths, from the walk and what came and went since.</summary>
        public HashSet<string>? Paths { get; set; }

        /// <summary>Each file as last read, with the open text it was read from (null for the disk's).</summary>
        public Dictionary<string, (GodotFile File, TextSnapshot? From)> Read { get; } = new(PathNames.Comparer);

        public HashSet<string> Dirty { get; } = new(PathNames.Comparer);

        /// <summary>Folders come since it was walked, walked for their scenes as it is next asked about.</summary>
        public List<string> Come { get; } = [];

        /// <summary>The scenes last made, kept until a file changes.</summary>
        public GodotScenes? Scenes { get; set; }
    }
}
