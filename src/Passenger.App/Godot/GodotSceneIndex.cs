using System.Collections.Immutable;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>The text scenes and resources of the folder's Godot projects (<see cref="GodotScenes"/>), with the uids Godot
/// keeps of their files (<see cref="GodotUids"/>) and the texts of their GDScripts, each project's found by one walk of its folder the first time it is
/// asked about (<see cref="FolderWalk"/>, which passes over its .godot folder, whose uid cache is read alone), then
/// followed through the folder's watcher: a file written is read again, one come or gone put in or taken out, the folder
/// walked again only when changes were lost. An open file is read as the editor has it. Asked on the UI thread; every
/// read is made off it.</summary>
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

    /// <summary>Whether the open folder's index lists every file now (<see cref="Workspace.FileIndex.IsWhole"/>).</summary>
    public Func<bool> FilesWhole { get; init; } = () => false;

    /// <summary>The res:// paths of a project's files, by the index's list they were made from, made again when it changes.</summary>
    private (IReadOnlyList<string>? From, string? Folder, HashSet<string> Res) listed = (null, null, new HashSet<string>(PathNames.Comparer));

    /// <summary>Whether a Godot project's files lack a res:// path, asked of the index on the UI thread and read off it:
    /// null where the index does not list every file; a path in a folder the walk passes over, a hidden one or Godot's own
    /// beside a file is never lacked.</summary>
    public Func<string, bool>? LacksOf(string folder)
    {
        UiThread.Check();
        (string? root, IReadOnlyList<string> files) = Files();
        if (root is null || !FilesWhole())
        {
            return null;
        }
        if (!ReferenceEquals(listed.From, files) || listed.Folder != folder)
        {
            listed = (files, folder, new HashSet<string>(FilesUnder(folder).Select(f => GodotScenes.ResPathOf(folder, f)).OfType<string>(), PathNames.Comparer));
        }
        HashSet<string> known = listed.Res;
        return res => res.StartsWith("res://", StringComparison.Ordinal) && res["res://".Length..] is { Length: > 0 } relative
            && !known.Contains(res) && !IgnoredFolders.AnyIn(folder, relative) && !IgnoredFolders.AnyHidden(relative)
            && !res.EndsWith(".import", StringComparison.OrdinalIgnoreCase) && !res.EndsWith(".uid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder of the Godot project a file of the open folder is of (<see cref="ProjectSession.GodotOf"/>); null for none.</summary>
    public Func<string, string?> GameOf { get; init; } = _ => null;

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

    /// <summary>A C# file under a project asked about was written, on the UI thread: what its open scenes are read against.</summary>
    public event EventHandler? CodeChanged;

    /// <summary>A project's scenes and resources as they stand, by its Godot folder (canonical).</summary>
    public async Task<GodotScenes> ScenesAsync(string folder)
    {
        UiThread.Check();
        if (!projects.TryGetValue(folder, out Project? project))
        {
            projects[folder] = project = new Project(folder);
        }
        // One reading at a time, so none is made of a project's files while another is still putting them in.
        while (project.Reading is { IsCompleted: false } reading)
        {
            await reading;
        }
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        project.Reading = done.Task;
        try
        {
            return await ReadAsync(folder, project);
        }
        finally
        {
            done.SetResult();
        }
    }

    private async Task<GodotScenes> ReadAsync(string folder, Project project)
    {
        project.Walking ??= BackgroundWork.Run(() => FolderWalk.Files(folder, built: true).Where(Read).ToHashSet(PathNames.Comparer));
        project.Cache ??= BackgroundWork.Run(() => CacheOf(folder));
        HashSet<string> paths = await project.Walking;
        project.Paths ??= paths;
        if (project.Come.Count > 0)
        {
            List<string> come = [.. project.Come];
            project.Come.Clear();
            foreach (string file in await BackgroundWork.Run(() => come.Where(Directory.Exists).SelectMany(f => FolderWalk.Files(f, built: true)).Where(Read).ToList()))
            {
                project.Paths.Add(file);
                project.Dirty.Add(file);
            }
        }
        OpenTexts texts = Texts();
        List<(string Path, TextSnapshot? Open)> unread = Unread(project, texts);
        IReadOnlyList<(string Uid, string Res)>? cached = await project.Cache;
        if (unread.Count == 0 && project.Gone.Count == 0 && ReferenceEquals(cached, project.CacheRead) && project.Scenes is { } kept)
        {
            return kept;
        }
        IReadOnlyList<(string Path, TextSnapshot? Open, GodotFile? File, string? Uid, string? Text)> parsed = await BackgroundWork.Run(
            () => (IReadOnlyList<(string, TextSnapshot?, GodotFile?, string?, string?)>)[.. unread.Select(u => Parsed(u.Path, u.Open))]);
        bool uidsChanged = !ReferenceEquals(cached, project.CacheRead) || project.Uids is null;
        foreach ((string path, TextSnapshot? open, GodotFile? file, string? uid, string? text) in parsed)
        {
            project.Dirty.Remove(path);
            if (project.Paths.Contains(path))
            {
                uidsChanged |= project.Take(path, open, file, uid, text);
            }
        }
        foreach (string gone in project.Gone)
        {
            uidsChanged |= project.Take(gone, null, null, null, null);
        }
        project.Gone.Clear();
        if (uidsChanged)
        {
            // What the project's own files say now goes over the cache Godot's editor wrote when it last ran.
            Dictionary<string, string> uids = new(StringComparer.Ordinal);
            foreach ((string uid, string res) in cached ?? [])
            {
                uids[uid] = res;
            }
            foreach ((string path, string uid) in project.Beside)
            {
                if (GodotScenes.ResPathOf(folder, GodotUids.BesideOf(path)) is { } res)
                {
                    uids[uid] = res;
                }
            }
            project.Uids = uids;
            project.CacheRead = cached;
        }
        return project.Scenes = new GodotScenes(folder, project.Files, project.Uids!, cached is not null, project.Texts);
    }

    /// <summary>The files of a project to read again: every one the first time, then those written, come or gone, and those
    /// whose open text is not the one last read (opened, typed in, or closed since), never a walk of every file.</summary>
    private static List<(string Path, TextSnapshot? Open)> Unread(Project project, OpenTexts texts)
    {
        List<(string Path, TextSnapshot? Open)> unread = [];
        HashSet<string> paths = project.Paths!;
        if (!project.Primed)
        {
            project.Primed = true;
            foreach (string known in project.Read.Keys.Concat(project.Beside.Keys).Concat(project.Raw.Keys).Where(p => !paths.Contains(p)).ToList())
            {
                project.Gone.Add(known);
            }
            foreach (string path in paths)
            {
                unread.Add((path, texts.Open(path)));
            }
            return unread;
        }
        HashSet<string> taken = new(PathNames.Comparer);
        foreach (string path in project.Dirty)
        {
            if (paths.Contains(path) && taken.Add(path))
            {
                unread.Add((path, texts.Open(path)));
            }
        }
        foreach (string path in texts.Paths.Concat(project.FromOpen))
        {
            TextSnapshot? open = texts.Open(path);
            if (paths.Contains(path) && !ReferenceEquals(project.ReadFrom(path), open) && taken.Add(path))
            {
                unread.Add((path, open));
            }
        }
        return unread;
    }

    /// <summary>Whether a file of a Godot project is one the index reads: a text scene or resource, a file beside another
    /// giving it its uid, one defining names its code's strings give (project.godot, a shader, a translation), or a GDScript.</summary>
    private static bool Read(string path) => GodotFile.IsText(path) || GodotUids.IsBeside(path) || Raw(path);

    /// <summary>Whether a file the index reads is kept as text: one defining names, or a GDScript.</summary>
    private static bool Raw(string path) => GodotDefinitions.Reads(path) || GodotScriptDeclarations.IsScript(path);

    /// <summary>A file the index reads, from its open text or the disk: a scene or resource parsed, the uid a file beside
    /// another gives it, or another's text; none for one that cannot be read.</summary>
    private static (string Path, TextSnapshot? Open, GodotFile? File, string? Uid, string? Text) Parsed(string path, TextSnapshot? open)
    {
        string? text = open?.ToString() ?? EditorArea.ReadText(path);
        return text is null ? (path, open, null, null, null)
            : GodotUids.IsBeside(path) ? (path, open, null, GodotUids.OfBeside(path, text), null)
            : GodotFile.IsText(path) ? (path, open, GodotFile.Parse(text), null, null)
            : (path, open, null, null, text);
    }

    /// <summary>The cache of uids Godot's editor keeps under a project's folder; null for none, or one that cannot be read.</summary>
    private static IReadOnlyList<(string Uid, string Res)>? CacheOf(string folder)
    {
        string cache = Path.Combine(folder, GodotUids.CacheFile);
        try
        {
            return File.Exists(cache) ? GodotUids.ReadCache(File.ReadAllBytes(cache)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Files written: a scene or resource of a project asked about is read again when next asked, and a C# file of
    /// one is said (<see cref="CodeChanged"/>).</summary>
    public void FilesChanged(IEnumerable<string> paths)
    {
        bool changed = false;
        bool code = false;
        foreach (string path in paths)
        {
            foreach (Project project in projects.Values.Where(p => PathNames.IsUnder(path, p.Folder)))
            {
                code |= GodotChecks.IsCSharp(path);
                if (Read(path))
                {
                    project.Dirty.Add(path);
                }
                else if (PathNames.Same(path, Path.Combine(project.Folder, GodotUids.CacheFile)))
                {
                    project.Cache = null;
                }
                else
                {
                    continue;
                }
                project.Scenes = null;
                changed = true;
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (code)
        {
            CodeChanged?.Invoke(this, EventArgs.Empty);
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
                project.Primed = false;
                project.Cache = null;
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
                if (Read(path))
                {
                    if (File.Exists(path) ? known.Add(path) | project.Dirty.Add(path) : known.Remove(path) && project.Gone.Add(path))
                    {
                        project.Scenes = null;
                        changed = true;
                    }
                }
                else if (Directory.Exists(path) || known.Any(k => PathNames.IsUnder(k, path)))
                {
                    // A folder gone takes its files with it; one come is walked for its own as the project is next asked about.
                    foreach (string under in known.Where(k => PathNames.IsUnder(k, path)).ToList())
                    {
                        known.Remove(under);
                        project.Gone.Add(under);
                    }
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

    /// <summary>A file the editor opened: a scene's, resource's, GDScript's or other defining file's typing, once it pauses, is a change of its project's.</summary>
    public void Attach(OpenDocument document)
    {
        if (!(GodotFile.IsText(document.Path) || Raw(document.Path)) || typed.ContainsKey(document))
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

        /// <summary>The texts of project.godot, its shaders, translations and GDScripts, as last read, with the open text each was read from.</summary>
        public Dictionary<string, (string Text, TextSnapshot? From)> Raw { get; } = new(PathNames.Comparer);

        /// <summary>The uid each .uid and .import file gives the file beside it, as last read.</summary>
        public Dictionary<string, string> Beside { get; } = new(PathNames.Comparer);

        /// <summary>The cache of uids Godot's editor keeps, read as the project is first asked about; null within for none.</summary>
        public Task<IReadOnlyList<(string Uid, string Res)>?>? Cache { get; set; }

        public HashSet<string> Dirty { get; } = new(PathNames.Comparer);

        /// <summary>Folders come since it was walked, walked for their scenes as it is next asked about.</summary>
        public List<string> Come { get; } = [];

        /// <summary>The scenes last made, kept until a file changes.</summary>
        public GodotScenes? Scenes { get; set; }

        /// <summary>The reading under way, which another waits out.</summary>
        public Task? Reading { get; set; }

        /// <summary>Whether its files have been read once since its folder was walked, after which only those that changed are.</summary>
        public bool Primed { get; set; }

        /// <summary>Files taken out since it was last read, whose readings go.</summary>
        public HashSet<string> Gone { get; } = new(PathNames.Comparer);

        /// <summary>The files last read from an open text, read again from the disk once closed.</summary>
        public HashSet<string> FromOpen { get; } = new(PathNames.Comparer);

        /// <summary>Its scenes and resources by res:// path, changed a file at a time.</summary>
        public ImmutableDictionary<string, GodotFile> Files { get; private set; } = ImmutableDictionary.Create<string, GodotFile>(StringComparer.Ordinal);

        /// <summary>The texts of its files kept as text by res:// path, changed a file at a time.</summary>
        public ImmutableDictionary<string, string> Texts { get; private set; } = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

        /// <summary>The uids its files are known by, made again only when a uid changes.</summary>
        public IReadOnlyDictionary<string, string>? Uids { get; set; }

        /// <summary>The cache of uids the uids were last made from.</summary>
        public IReadOnlyList<(string Uid, string Res)>? CacheRead { get; set; }

        private readonly Dictionary<string, string?> resOf = new(PathNames.Comparer);

        /// <summary>The open text a file was last read from; null for the disk's or none.</summary>
        public TextSnapshot? ReadFrom(string path) => Read.TryGetValue(path, out (GodotFile File, TextSnapshot? From) read) ? read.From
            : Raw.TryGetValue(path, out (string Text, TextSnapshot? From) other) ? other.From : null;

        /// <summary>A file's reading put in its place (or taken out, for none); whether a uid it gives another changed.</summary>
        public bool Take(string path, TextSnapshot? open, GodotFile? file, string? uid, string? text)
        {
            string? res = resOf.TryGetValue(path, out string? known) ? known : resOf[path] = GodotScenes.ResPathOf(folder, path);
            string? was = Beside.GetValueOrDefault(path);
            Read.Remove(path);
            Raw.Remove(path);
            Beside.Remove(path);
            if (res is not null)
            {
                Files = Files.Remove(res);
                Texts = Texts.Remove(res);
            }
            if (file is not null)
            {
                Read[path] = (file, open);
                Files = res is null ? Files : Files.SetItem(res, file);
            }
            if (uid is not null)
            {
                Beside[path] = uid;
            }
            if (text is not null)
            {
                Raw[path] = (text, open);
                Texts = res is null ? Texts : Texts.SetItem(res, text);
            }
            if (open is not null && (file is not null || text is not null))
            {
                FromOpen.Add(path);
            }
            else
            {
                FromOpen.Remove(path);
            }
            return was != uid;
        }
    }
}
