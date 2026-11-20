using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The folder's projects as dotnet ef takes them (<see cref="EfTargets"/>), read off the window's thread. Every read
/// finds whether EF Core reaches a project: its evaluation, or a project it references, says so, else, only where no
/// evaluation does or the read is whole, its restore (<see cref="RestoredVersions"/>). A whole read, asked only while the
/// Migrations window is shown or something asks for its lists, reads the C# files of every project EF Core reaches; any
/// other, those of the one project chosen. Each file is read again only once it changed. One read at a time, the last
/// asked for landing after it. A UI-thread object.</summary>
public sealed class EfProjects(Func<ProjectModel, RestoredVersions> restored)
{
    /// <summary>Each C# file's read, with the stamp and options it was read at; the read's thread's alone.</summary>
    private Dictionary<string, (FileStamp Stamp, CSharpParseOptions Options, EfFileRead Read)> files = new(PathNames.Comparer);

    /// <summary>What a read is asked for after the one under way.</summary>
    private Ask? asked;

    /// <summary>What the read under way reads.</summary>
    private Ask? underWay;

    /// <summary>What the last read landed read.</summary>
    private Ask? landedOf;

    private bool reading;

    /// <summary>Done as the next read lands, or ends however it ends.</summary>
    private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A read asked for: of these projects, whole or of the one chosen alone.</summary>
    private sealed record Ask(IReadOnlyList<ProjectModel> Projects, bool Whole, string? Chosen);

    /// <summary>What each project EF Core reaches gives dotnet ef, as the last whole read found it; null before one.</summary>
    public IReadOnlyList<EfProjectRead>? Whole { get; private set; }

    /// <summary>Each project's read as the last read of it found it, whole or of it alone.</summary>
    private IReadOnlyList<EfProjectRead> latest = [];

    /// <summary>Whether EF Core reaches a project here though no evaluation says so: its restore holds EF Core.</summary>
    public bool ReachedByRestore { get; private set; }

    /// <summary>A project's read as the last read of it found it; null where none read it.</summary>
    public EfProjectRead? Of(ProjectModel project) => latest.FirstOrDefault(r => PathNames.Same(r.Path, project.Path));

    /// <summary>Whether <see cref="Whole"/> is of these projects (the same evaluations), with no read asked for since.</summary>
    public bool IsWholeOf(IReadOnlyList<ProjectModel> projects) => !reading && landedOf is { Whole: true } read && Same(read.Projects, projects);

    private static bool Same(IReadOnlyList<ProjectModel> read, IReadOnlyList<ProjectModel> projects) =>
        read.SequenceEqual(projects, ReferenceEqualityComparer.Instance);

    /// <summary>A read landed, with no other asked for since.</summary>
    public event EventHandler? Landed;

    /// <summary>Reads the projects, <paramref name="whole"/> or the <paramref name="chosen"/> one's files alone, only where the
    /// last read landed, under way or asked for read them so no longer: a whole read covers one of the chosen alone.</summary>
    public void Know(IReadOnlyList<ProjectModel> projects, bool whole, ProjectModel? chosen)
    {
        Ask now = new(projects, whole, chosen?.Path);
        if ((asked ?? (reading ? underWay : landedOf)) is not { } last || !Same(last.Projects, projects)
            || (whole && !last.Whole) || (!last.Whole && !PathNames.Same(last.Chosen ?? string.Empty, now.Chosen ?? string.Empty)))
        {
            Follow(projects, whole, chosen);
        }
    }

    /// <summary>Reads the projects, their files as they now stand, after the read under way where there is one; a whole read
    /// asked for stays whole whatever is asked for after it before it begins.</summary>
    public void Follow(IReadOnlyList<ProjectModel> projects, bool whole, ProjectModel? chosen)
    {
        UiThread.Check();
        asked = new Ask(projects, whole || asked?.Whole == true, chosen?.Path);
        if (!reading)
        {
            reading = true;
            Task read = ReadAsync();
            BackgroundWork.Track(read);
            IdeErrors.Observe(read);
        }
    }

    /// <summary>A whole read of the projects as they stand (<paramref name="projects"/> asked again as each read lands), asked
    /// for and waited for: what something asking for the Migrations window's lists waits on. A read reads files alone, so
    /// it ends however slow the disk.</summary>
    public async Task WholeAsync(Func<IReadOnlyList<ProjectModel>> projects)
    {
        // A few rounds at most: each lands only once the projects stood still through a read, and one that fails lands none.
        for (int round = 0; round < 5 && !IsWholeOf(projects()); round++)
        {
            Task landing = next.Task;
            Know(projects(), whole: true, chosen: null);
            await landing;
        }
    }

    private async Task ReadAsync()
    {
        bool landed = false;
        try
        {
            while (asked is { } ask)
            {
                asked = null;
                underWay = ask;
                (IReadOnlyList<EfProjectRead> read, bool byRestore) = await BackgroundWork.Run(() => ReadOf(ask));
                if (ask.Whole && (Whole is not { } before || before.Count != read.Count || !before.Zip(read).All(r => r.First.SameAs(r.Second))))
                {
                    Whole = read;
                }
                latest = [.. read, .. latest.Where(l => !read.Any(r => PathNames.Same(r.Path, l.Path)))];
                ReachedByRestore = byRestore;
                landedOf = ask;
                if (asked is null)
                {
                    // Ended before it is told, so what hears it may ask again.
                    landed = true;
                    reading = false;
                    Next();
                    Landed?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }
        finally
        {
            if (!landed)
            {
                reading = false;
                Next();
            }
        }
    }

    /// <summary>Tells what waits for the next read that it came, and begins the one after.</summary>
    private void Next()
    {
        TaskCompletionSource came = next;
        next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        came.TrySetResult();
    }

    /// <summary>A read: which projects EF Core reaches, and what the C# files of those it reads declare; the files no longer
    /// among them let go where it is whole.</summary>
    private (IReadOnlyList<EfProjectRead> Read, bool ByRestore) ReadOf(Ask ask)
    {
        IReadOnlyList<ProjectModel> all = ask.Projects;
        bool evaluated = all.Any(EfTargets.UsesEf);
        bool byRestore = false;
        Dictionary<string, (FileStamp, CSharpParseOptions, EfFileRead)> filesNow = new(PathNames.Comparer);
        List<(ProjectModel, IReadOnlyList<(string, EfFileRead)>)> reached = [];
        foreach (ProjectModel project in all)
        {
            bool read = ask.Whole || PathNames.Same(project.Path, ask.Chosen ?? string.Empty);
            bool reaches = EfTargets.ReachesEf(project, all);
            // The restore asked only where the evaluations say nothing of EF Core, or where the project's files are read.
            if (!reaches && (read || !evaluated && !byRestore) && restored(project).Holds(EfRestore.Core))
            {
                reaches = true;
                byRestore |= !evaluated;
            }
            if (!reaches || !read)
            {
                continue;
            }
            CSharpParseOptions options = CompilationSymbols.Of(project);
            List<(string, EfFileRead)> declared = [];
            foreach (string file in FolderWalk.ProjectFiles(project, ".cs"))
            {
                if (FileOf(file, options, filesNow) is { } found)
                {
                    declared.Add((file, found));
                }
            }
            reached.Add((project, declared));
        }
        if (ask.Whole)
        {
            files = filesNow;
        }
        else
        {
            foreach (KeyValuePair<string, (FileStamp, CSharpParseOptions, EfFileRead)> file in filesNow)
            {
                files[file.Key] = file.Value;
            }
        }
        return (EfTargets.Resolve(reached), byRestore);
    }

    /// <summary>A C# file's read, taken from the last where neither it nor its options changed; null for one that cannot be read.</summary>
    private EfFileRead? FileOf(string file, CSharpParseOptions options, Dictionary<string, (FileStamp, CSharpParseOptions, EfFileRead)> now)
    {
        if (FileStamp.Of(file) is not { } stamp)
        {
            return null;
        }
        if (files.TryGetValue(file, out (FileStamp Stamp, CSharpParseOptions Options, EfFileRead Read) known) && known.Stamp == stamp && known.Options.Equals(options))
        {
            now[file] = known;
            return known.Read;
        }
        string text;
        try
        {
            text = FileText.Load(file)?.Text ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        EfFileRead read = EfTargets.ReadOf(text, CSharpParsing.Parse(text, options, file).GetRoot());
        now[file] = (stamp, options, read);
        return read;
    }
}
