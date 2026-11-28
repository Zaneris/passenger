using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Docking;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Packages;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;
using DatabaseDiscovery = Passenger.App.Database.DatabaseDiscovery;

namespace Passenger.App.Migrations;

/// <summary>What the Migrations window works with in the IDE, among them the questions it asks the person
/// (<c>Choose</c>: a title, a question and its answers, the default among them; null when they cancel).</summary>
public sealed record MigrationsParts(
    EditorArea Editors,
    ToolViews Views,
    DockHost Dock,
    ActionWork Work,
    StatusSay Say,
    Func<ProjectSession> Session,
    Func<BuildRunController> Builds,
    Func<string, string, IReadOnlyList<string>, string?, Task<string?>> Choose,
    Func<ProjectModel, string, string, CommandHistory.Recording, Task<PackageOutcome>> Install,
    Func<ProjectModel, IReadOnlyList<(string Id, string Version)>, CommandHistory.Recording, IReadOnlyList<(string Id, string Version)>?, Task<PackageOutcome>> InstallSet,
    Func<Task<IPackageFeed>> Feed,
    Action<IReadOnlyList<string>> FollowWritten,
    Func<string, string?> OpenFile,
    Func<string> StateFolder,
    Action<IReadOnlyList<Problem>> ShowPending)
{
    /// <summary>Whether nobody is at the window to ask (an agent's own IDE): a gap is then said with its fix, not asked about.</summary>
    public Func<bool> AsksNoOne { get; init; } = () => false;
}

/// <summary>A migration as the Migrations window lists it: its id (its timestamp and name), its name, and whether the
/// database has it (null until dotnet ef could ask the database).</summary>
public sealed record MigrationRow(string Id, string Name, bool? Applied);

/// <summary>A context the project holds, as <c>dotnet ef dbcontext list</c> names it.</summary>
public sealed record EfContext(string Name, string FullName);

/// <summary>What one of the window's commands came to: whether it was done, and what the status bar said of it.</summary>
public sealed record EfDone(bool Done, Words Said);

/// <summary>What a command runs against: the project holding the context and its migrations, the startup project dotnet ef
/// makes it from (with the framework, folder and variables of the run configuration that runs it, the database the run
/// uses, and the environment chosen in the window), the context, and the tool.</summary>
internal sealed record EfSetting(ProjectModel Project, ProjectModel Startup, string? Framework, string Folder, IReadOnlyDictionary<string, string> Environment,
    string? Context, EfTool Tool, string Runtime)
{
    /// <summary>The database of a compose run or an AppHost that runs the startup project, as this machine reaches it; null
    /// for the one its configuration gives.</summary>
    public EfRunDatabase? Database { get; init; }

    /// <summary>The output folders dotnet ef loads the startup project's and the project's assemblies from, as the build
    /// before the command makes them; null when MSBuild could not say, so every read runs.</summary>
    public IReadOnlyList<string>? Outputs { get; init; }

    /// <summary>ef.dll started as dotnet ef would start it; null where dotnet ef runs.</summary>
    public EfLaunch? Launch { get; init; }
}

/// <summary>The database a context's migrations go to: its connection string as dotnet ef reads it, the name it has, what
/// it is used by, the folder a SQLite file's path is read from, and the startup project's packages, which tell its kind.</summary>
public sealed record EfConnection(string Name, string Value, string Used, string Folder, IReadOnlyList<string> Packages)
{
    /// <summary>Whether a compose run or an AppHost gave it, so it goes with the run.</summary>
    public bool Running { get; init; }
}

/// <summary>The Migrations window's model and commands over each session it binds: the folder's EF Core projects, the
/// project, startup project and context chosen, that context's migrations with which the database has, and what keeps
/// dotnet ef from working, each with its fix. A command is made ready first (<see cref="EfGaps.Before"/>, each gap fixed
/// as one step of global undo), built, then run as <c>dotnet ef … --no-build</c>, its failure read into one line with its
/// fix. One command at a time. A UI-thread object.</summary>
public sealed partial class MigrationsController(MigrationsParts parts)
{
    private ProjectSession? bound;
    private Working? running;

    /// <summary>The window's lists or choices changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The folder's projects dotnet ef makes migrations in: those declaring a context or holding its migrations, as
    /// the folder's whole read of them found (<see cref="EfTargets.Projects"/>), made while the window is shown or a command
    /// asks; before it, or where none declares one yet, those referencing EF Core.</summary>
    public IReadOnlyList<ProjectModel> Projects { get; private set; } = [];

    /// <summary>The project chosen, whose contexts and migrations are listed; null with none.</summary>
    public ProjectModel? Project { get; private set; }

    /// <summary>The projects dotnet ef can make the chosen project's contexts from (<see cref="EfTargets.Startups"/>): those
    /// that load it (that reference it, or it itself) and run on .NET, the programs first.</summary>
    public IReadOnlyList<ProjectModel> Startups { get; private set; } = [];

    public ProjectModel? Startup { get; private set; }

    /// <summary>The environments offered for the startup project: the one dotnet ef runs it in where none is chosen first,
    /// with what names it, then those its settings files and launch profiles name, then the one chosen where it is none of
    /// these.</summary>
    public IReadOnlyList<EfEnvironment> Environments { get; private set; } = [];

    /// <summary>The environment every dotnet ef the window starts runs the startup project in (<see cref="EnvironmentOf"/>);
    /// null with no startup project.</summary>
    public EfEnvironment? Environment => Startup is { } startup ? EnvironmentOf(startup) : null;

    /// <summary>The environments the startup project's files name, as last read (<see cref="EfEnvironmentChoices.Named"/>).</summary>
    private IReadOnlyList<string> named = [];

    /// <summary>The chosen project's contexts, once dotnet ef has listed them.</summary>
    public IReadOnlyList<EfContext> Contexts { get; private set; } = [];

    /// <summary>The context chosen; null for the project's one, before they are listed.</summary>
    public string? Context { get; private set; }

    /// <summary>The chosen context's migrations, oldest first, once listed.</summary>
    public IReadOnlyList<MigrationRow> Migrations { get; private set; } = [];

    /// <summary>Whether the migrations were listed for the choices as they are.</summary>
    public bool Listed { get; private set; }

    /// <summary>What keeps dotnet ef from working before any command, each with its fix, as last found.</summary>
    public IReadOnlyList<EfGap> Gaps { get; private set; } = [];

    /// <summary>The contexts whose model has changes no migration covers, as last checked (after a build, a listing or a
    /// command that changes the migrations).</summary>
    public IReadOnlyList<EfPending> Pending { get; private set; } = [];

    /// <summary>Where the chosen project's contexts are declared, as the last read of its C# files found them: where a gap's
    /// fixes are offered, beside the startup project's file.</summary>
    public IReadOnlyList<EfPending> Declared { get; private set; } = [];

    /// <summary>The code Problems lists a context's pending model changes under.</summary>
    public const string PendingCode = "Migrations";

    /// <summary>Add Migration asked for by a gap's fix, for a context's pending model changes.</summary>
    public event EventHandler? AddAsked;

    /// <summary>Where the last command reached the database, when a compose run or an AppHost gave it; null for the one
    /// the configuration gives.</summary>
    public string? Database { get; private set; }

    /// <summary>The last command's failure, read into one line with its fix; null once one succeeds.</summary>
    public EfGap? Failure { get; private set; }

    /// <summary>The command under way, or null.</summary>
    public Words? Busy => running?.Stage;

    /// <summary>Follows the session the window binds: its projects read again, its listing forgotten.</summary>
    public void Bind(ProjectSession session)
    {
        UiThread.Check();
        bound = session;
        // A choice made or the folder's kept ones taken: what was listed was of another environment.
        session.MigrationEnvironments.Changed += (_, _) =>
        {
            if (bound == session)
            {
                Forget();
                Follow();
            }
        };
        Pending = [];
        Project = null;
        Startup = null;
        Context = null;
        projectChosen = false;
        startupChosen = false;
        defaults = null;
        named = [];
        Forget();
        Follow();
    }

    /// <summary>The defaults EF Core 11's dotnet ef reads in the folder open, as last read; null with none.</summary>
    private EfDefaults? defaults;

    /// <summary>Whether the person chose the project, or the startup project, in the window, which the defaults give way to.</summary>
    private bool projectChosen;

    private bool startupChosen;

    /// <summary>The folder's projects read again (an evaluation landed, a package changed, a build ended, the read of what they
    /// declare landed), the choices kept where they still stand, else those the tool's defaults name, and the gaps found again.</summary>
    public void Follow()
    {
        UiThread.Check();
        IReadOnlyList<ProjectModel> all = bound?.Projects ?? [];
        ProjectModel? wasProject = Project;
        ProjectModel? wasStartup = Startup;
        Projects = EfTargets.Projects(all, bound?.EfProjects.Whole);
        Project = Chosen(Projects, wasProject, projectChosen, defaults?.Project);
        Declared = Project is { } chosen ? bound?.EfProjects.Of(chosen)?.Contexts ?? [] : [];
        bound?.EfProjects.Know(all, Looked, Project);
        Startups = Project is null ? [] : EfTargets.Startups(Project, all);
        Startup = Chosen(Startups, wasStartup, startupChosen, defaults?.StartupProject);
        if (!PathNames.Same(Startup?.Path ?? string.Empty, wasStartup?.Path ?? string.Empty))
        {
            named = [];
        }
        if (!PathNames.Same(Project?.Path ?? string.Empty, wasProject?.Path ?? string.Empty))
        {
            Context = null;
            Forget();
        }
        else if (!PathNames.Same(Startup?.Path ?? string.Empty, wasStartup?.Path ?? string.Empty))
        {
            Forget();
        }
        if (bound?.Folder is { } folder)
        {
            BackgroundWork.Track(ReadDefaultsAsync(bound, folder));
        }
        FindGaps();
        ReadEnvironments();
        Offer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the window is shown, so what every project EF Core reaches declares is read, not the chosen one's alone.</summary>
    private bool Looked => parts.Dock.IsShown(ToolWindowIds.Migrations);

    /// <summary>What the projects declare read again, their files as they now stand: a build may come with a context written
    /// since.</summary>
    public void ReadAgain()
    {
        if (bound is { } session)
        {
            session.EfProjects.Follow(session.Projects, Looked, Project);
        }
    }

    /// <summary>The environments the startup project's files name read again off the UI thread, offered once they land while
    /// it is still the one chosen.</summary>
    public void ReadEnvironments()
    {
        if (Startup is { } startup)
        {
            BackgroundWork.Track(ReadEnvironmentsAsync(startup));
        }
    }

    private async Task ReadEnvironmentsAsync(ProjectModel startup)
    {
        IReadOnlyList<string> read = await BackgroundWork.Run(() => EfEnvironmentChoices.Named(startup));
        if (Startup == startup && !read.SequenceEqual(named, StringComparer.Ordinal))
        {
            named = read;
            Offer();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The environments offered made again from the startup project's choice, its run configuration and its files.</summary>
    private void Offer()
    {
        if (Startup is not { } startup)
        {
            Environments = [];
            return;
        }
        EfEnvironment otherwise = DefaultOf(startup);
        string? chosen = parts.Session().MigrationEnvironments.Of(startup);
        EfEnvironment[] offered = [otherwise, .. named.Append(chosen).OfType<string>().Where(n => n != otherwise.Name).Distinct(StringComparer.Ordinal)
            .Select(n => new EfEnvironment(n))];
        if (!offered.SequenceEqual(Environments))
        {
            Environments = offered;
        }
    }

    /// <summary>The one place deciding the environment dotnet ef runs a startup project in: the one chosen for it in the
    /// window, else the default (<see cref="DefaultOf"/>).</summary>
    private EfEnvironment EnvironmentOf(ProjectModel startup) =>
        parts.Session().MigrationEnvironments.Of(startup) is { } chosen ? new EfEnvironment(chosen) : DefaultOf(startup);

    /// <summary>The environment a startup project runs in where none is chosen, as its run does: the one its run configuration
    /// names, else the one Passenger's own environment names (which a run inherits), else Development, as dotnet ef takes it.</summary>
    private EfEnvironment DefaultOf(ProjectModel startup) =>
        RunsOf(startup) is { } runs && AspNetCoreVariables.EnvironmentOf(runs.Environment.GetValueOrDefault, string.Empty) is { Length: > 0 } configured
            ? new EfEnvironment(configured, "the run configuration's")
            : AspNetCoreVariables.EnvironmentOf(System.Environment.GetEnvironmentVariable, string.Empty) is { Length: > 0 } inherited
            ? new EfEnvironment(inherited, "Passenger's own")
            : new EfEnvironment("Development", "the default");

    /// <summary>Chooses the environment dotnet ef runs the startup project in, kept with the folder: the default's name goes
    /// back to none chosen, following the run configuration. Null once chosen, else why not, said.</summary>
    public Words? ChooseEnvironment(string name)
    {
        UiThread.Check();
        if (Startup is not { } startup)
        {
            return Said(new EfDone(false, Refusals.NoEfCore)).Said;
        }
        if (EfEnvironmentChoices.WhyNot(name) is { } why)
        {
            return Said(new EfDone(false, why)).Said;
        }
        parts.Session().MigrationEnvironments.Choose(startup, name == DefaultOf(startup).Name ? null : name);
        return null;
    }

    /// <summary>Of projects, the one chosen before while it is still there if the person chose it, else the one the tool's
    /// defaults name, else the first: a choice of the window's own follows the lists as the projects' evaluations and the
    /// read of what they declare land, one by one.</summary>
    private static ProjectModel? Chosen(IReadOnlyList<ProjectModel> projects, ProjectModel? before, bool chosen, string? named) =>
        (chosen && before is not null ? projects.FirstOrDefault(p => PathNames.Same(p.Path, before.Path)) : null)
        ?? projects.FirstOrDefault(p => EfDefaults.Names(named, p)) ?? projects.FirstOrDefault();

    /// <summary>The tool's defaults in the folder read off the UI thread (the tool found, the file looked for up from the
    /// folder), and the choices made again from them when they changed while the session is still the one bound.</summary>
    private async Task ReadDefaultsAsync(ProjectSession session, string folder)
    {
        EfDefaults? read = await BackgroundWork.Run(() => EfDefaults.Find(folder, EfTools.Find(folder)));
        if (bound == session && read != defaults)
        {
            defaults = read;
            Follow();
        }
    }

    /// <summary>Chooses the project whose contexts are listed, by path; its startup project chosen afresh.</summary>
    public void ChooseProject(string path)
    {
        if (Projects.FirstOrDefault(p => PathNames.Same(p.Path, path)) is not { } chosen || chosen == Project)
        {
            return;
        }
        Project = chosen;
        Startup = null;
        Context = null;
        projectChosen = true;
        startupChosen = false;
        Forget();
        Follow();
    }

    public void ChooseStartup(string path)
    {
        if (Startups.FirstOrDefault(p => PathNames.Same(p.Path, path)) is not { } chosen || chosen == Startup)
        {
            return;
        }
        Startup = chosen;
        startupChosen = true;
        Forget();
        Follow();
    }

    public void ChooseContext(string name)
    {
        if (name == Context)
        {
            return;
        }
        Context = name;
        Migrations = [];
        Listed = false;
        migrationsRead = null;
        reached = null;
        told = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for the startup project or context to be chosen in the window, a fix that is the person's choice.</summary>
    public event EventHandler<EfFixKind>? ChoiceAsked;

    /// <summary>Whether the window, shown, lists the migrations by itself now: once for each choice, while nothing it shows
    /// stops dotnet ef, nothing else runs, and the choices are made from the read of the projects as they stand.</summary>
    public bool WantsListing => !Listed && !tried && running is null && Startup is not null && gapsLanded == gapsAsked && Gaps.All(g => !g.Blocks)
        && bound is { } session && session.EfProjects.IsWholeOf(session.Projects);

    /// <summary>Whether the window listed by itself for the choices as they are, however it ended.</summary>
    private bool tried;

    /// <summary>Marks the listing the window starts by itself, which it starts once for the choices as they are.</summary>
    public void ListingTried() => tried = true;

    private void Forget()
    {
        tried = false;
        Contexts = [];
        Migrations = [];
        Listed = false;
        Failure = null;
        contextsRead = null;
        migrationsRead = null;
        reached = null;
        told = null;
        checks.Clear();
    }

    /// <summary>What the contexts were last listed from (<see cref="ReadOf"/>): listed again only once it changed.</summary>
    private string? contextsRead;

    /// <summary>What the migrations were listed from; null when a command's own output made the list, which no build has yet.</summary>
    private string? migrationsRead;

    /// <summary>Whether dotnet ef reached the database as it listed or updated the chosen context's migrations: a listing
    /// of none says so only by its warning; null when not known.</summary>
    private bool? reached;

    /// <summary>What the last command that wrote or deleted a migration's files said, until a build has them.</summary>
    private EfTold? told;

    /// <summary>Each context's model check, with what it read: its answer stands while that does.</summary>
    private readonly Dictionary<string, (string Read, bool Changed)> checks = [];

    /// <summary>The gaps as the restore and the tool stand, found again without running anything: read off the UI thread (a
    /// project.assets.json runs to megabytes, and every build and evaluation asks again), told once they land while no
    /// later look was asked for.</summary>
    private void FindGaps() => BackgroundWork.Track(FindGapsAsync());

    private int gapsAsked;
    private int gapsLanded;

    private async Task FindGapsAsync()
    {
        int asked = ++gapsAsked;
        if (Startup is not { } startup)
        {
            Gaps = [];
            gapsLanded = asked;
            return;
        }
        IReadOnlyList<EfGap> found = await BackgroundWork.Run(() => EfRestore.Of(startup.Path) is { } restore
            ? EfGaps.Before(EfTools.Find(startup.Folder), restore, startup.Name)
            : (IReadOnlyList<EfGap>)[]);
        if (asked == gapsAsked && Startup == startup)
        {
            Gaps = found;
            gapsLanded = asked;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Lists the chosen project's contexts and the chosen context's migrations, with which the database has: built
    /// first, as every command is. Never asks: what is missing stays in the window with its fix. Without
    /// <paramref name="rereads"/>, the contexts, the migrations and the model's check are taken from their last reads where
    /// what those read stands.</summary>
    public Task<EfDone> ListAsync(string action, CancellationToken cancel = default, bool build = true, bool rereads = true)
    {
        Task<EfDone> listed = ListingAsync(action, cancel, build, rereads);
        if (!listed.IsCompleted)
        {
            listing = listed;
        }
        return listed;
    }

    /// <summary>The listing under way, which a command asked for meanwhile waits for.</summary>
    private Task? listing;

    private Task<EfDone> ListingAsync(string action, CancellationToken cancel, bool build, bool rereads) =>
        CommandAsync(action, MigrationsWords.ListingStage, asks: false, cancel, async (setting, work) =>
        {
            string? read = await BackgroundWork.Run(() => ReadOf(setting));
            if (await ReadAsync(setting, read, work, contexts: true, list: rereads || read is null || read != migrationsRead, fresh: rereads) is { } failed)
            {
                return failed;
            }
            if (Context is null)
            {
                return Done(MigrationsWords.NoDbContext(setting.Project.Name));
            }
            int pending = Migrations.Count(m => m.Applied == false);
            return Done(Migrations.Count == 0 ? MigrationsWords.NoMigrations(Context)
                : Migrations.All(m => m.Applied is null) ? MigrationsWords.MigrationsUnread(Context, Migrations.Count)
                : pending == 0 ? MigrationsWords.MigrationsApplied(Context, Migrations.Count)
                : MigrationsWords.MigrationsPending(Context, Migrations.Count, pending));
        }, build: build);

    /// <summary>Why a migration cannot take a name, as dotnet ef makes it a C# class of that name; null where it can.</summary>
    public static Words? WhyNotNamed(string name) =>
        SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? null
        : name.Length == 0 ? MigrationsWords.Unnamed : MigrationsWords.NoClassName(name);

    /// <summary>The folders, from the chosen project's, holding the chosen context's migrations as the project was last
    /// read, the last one's first (<see cref="EfOutputFolders.Holding"/>).</summary>
    public IReadOnlyList<string> OutputFolders => Project is { } project ? EfOutputFolders.Holding(bound?.EfProjects.Of(project), project, Context) : [];

    /// <summary>The folder of the chosen context's last migration, beside which dotnet ef puts its next given none; null for a
    /// context with none (<see cref="EfOutputFolders.LastIn"/>).</summary>
    public string? LastFolder => Project is { } project ? EfOutputFolders.LastIn(bound?.EfProjects.Of(project), project, Context) : null;

    /// <summary>The folder Add Migration was last given for the chosen context, kept with the folder; null for none.</summary>
    public string? KeptFolder => Project is { } project && Context is { } context ? parts.Session().MigrationFolders.Of(project, context) : null;

    /// <summary>Adds a migration of the given name to the chosen context, into <paramref name="folder"/> (from the project's,
    /// as dotnet ef's <c>--output-dir</c> takes it; null for the one dotnet ef chooses), then lists again; once added, the
    /// folder is kept as the context's last given.</summary>
    public async Task<EfDone> AddAsync(string action, string name, string? folder = null, CancellationToken cancel = default)
    {
        if (WhyNotNamed(name) is { } unnamed)
        {
            return Said(new EfDone(false, unnamed));
        }
        string? asked = Project?.Path;
        EfDone done = await AddingAsync(action, name, folder, cancel);
        // Kept for a context dotnet ef took itself too, which the listing after it names.
        if (done.Done && Project is { } project && PathNames.Same(project.Path, asked ?? string.Empty) && Context is { } context)
        {
            parts.Session().MigrationFolders.Choose(project, context, folder);
        }
        return done;
    }

    private Task<EfDone> AddingAsync(string action, string name, string? folder, CancellationToken cancel) =>
        CommandAsync(action, MigrationsWords.AddingStage(name), asks: true, cancel, async (setting, work) =>
        {
            (string Others, string Built, IReadOnlyList<Host.Stamp> Reads)? before = await BeforeAsync(setting, work);
            EfOutcome added = await RunAsync(setting, ["migrations", "add", name, .. folder is null ? (string[])[] : ["--output-dir", folder], "--json"], work);
            if (!added.Succeeded)
            {
                return Failed(setting, added);
            }
            IReadOnlyList<string> written = Written(added);
            parts.FollowWritten(written);
            // The folders holding the context's migrations read again, with or without a build after.
            ReadAgain();
            string? made = MigrationId(added);
            string id = made ?? Path.GetFileNameWithoutExtension(written.FirstOrDefault() ?? name);
            // Add's answer says the listing, with whether the database has the migration where the listing said it was reached.
            if (Listed && setting.Context is { } context && context == Context && made is not null && reached is { } database
                && (Migrations.Count == 0 || string.CompareOrdinal(Migrations[^1].Id, id) < 0))
            {
                Migrations = [.. Migrations, new MigrationRow(id, name, database ? false : null)];
                migrationsRead = null;
                checks.Remove(context);
                await TellAsync(setting, context, before, written, changed: false);
                await ShowPendingAsync(setting, context, changed: false);
                then = Relist.No;
            }
            else
            {
                Migrations = [.. Migrations, new MigrationRow(id, name, false)];
            }
            string? landed = Answer(added)?.GetValueOrDefault("migrationFile") is { Length: > 0 } file && Path.GetDirectoryName(file) is { } into
                ? PathNames.Relative(into, setting.Project.Folder) : null;
            return Done(landed is null ? MigrationsWords.Added(name, setting.Context ?? setting.Project.Name)
                : MigrationsWords.AddedIn(name, setting.Context ?? setting.Project.Name, $"{setting.Project.Name}/{landed}"));
        }, Relist.Built | Relist.Listed);

    /// <summary>Removes the chosen context's last migration (once the person agrees), then lists again: with EF Core 11's
    /// tool, one the database was not read to have is removed offline, never reaching the database.</summary>
    public async Task<EfDone> RemoveLastAsync(string action, CancellationToken cancel = default)
    {
        if (Listed && Migrations.LastOrDefault() is { } last && !parts.AsksNoOne() && Mcp.AgentCall.Current is null
            && await parts.Choose("Remove Last Migration", $"Remove {last.Name}? Its files are deleted and the model snapshot goes back to the migration before it."
                + (last.Applied is null ? " The database could not be read, so whether it has the migration is not known." : string.Empty),
                ["Remove"], null) is null)
        {
            return new EfDone(false, MigrationsWords.RemoveCancelled);
        }
        return await CommandAsync(action, MigrationsWords.RemovingStage, asks: true, cancel, async (setting, work) =>
        {
            bool offline = EfDefaults.ReadsDefaults(setting.Tool) && Listed && setting.Context == Context && Migrations.LastOrDefault() is { Applied: not true };
            (string Others, string Built, IReadOnlyList<Host.Stamp> Reads)? before = await BeforeAsync(setting, work);
            EfOutcome removed = await RunAsync(setting, ["migrations", "remove", "--json", .. offline ? (string[])["--offline"] : []], work);
            if (!removed.Succeeded)
            {
                return Failed(setting, removed);
            }
            ReadAgain();
            // What remove deleted says the listing: the last one gone; the model's check, of any left, waits for a build without it.
            if (Listed && setting.Context is { } context && context == Context && Migrations.Count > 0 && MigrationId(removed) == Migrations[^1].Id)
            {
                Migrations = [.. Migrations.SkipLast(1)];
                migrationsRead = null;
                // The context's model snapshot, which remove writes back without naming, wherever it is kept.
                IReadOnlyList<string> snapshots = [.. bound?.EfProjects.Of(setting.Project)?.MigrationsOf(context).Where(m => m.Id is null).Select(m => m.File) ?? []];
                await TellAsync(setting, context, before, [.. Written(removed), .. snapshots], changed: null);
                then = Migrations.Count > 0 ? Relist.Built | Relist.Checked : Relist.No;
            }
            return Done(MigrationsWords.Removed(setting.Context ?? setting.Project.Name));
        }, Relist.Built | Relist.Listed);
    }

    /// <summary>Updates the database to the latest migration, or to the one given ("0" for none), asking first when that
    /// rolls migrations back, then lists again.</summary>
    public async Task<EfDone> UpdateAsync(string action, string? target, CancellationToken cancel = default)
    {
        if (target is not null && Listed && RolledBack(target) is { Count: > 0 } undone && !parts.AsksNoOne() && Mcp.AgentCall.Current is null)
        {
            string names = string.Join(", ", undone.Select(m => m.Name));
            string? answer = await parts.Choose($"Roll Back {Plural.Of(undone.Count, "Migration")}",
                $"Updating the database to {(target == "0" ? "before the first migration" : target)} runs the Down method of {names} against it, which can lose the data they hold.",
                ["Roll Back"], null);
            if (answer is null)
            {
                return new EfDone(false, MigrationsWords.UpdateCancelled);
            }
        }
        string to = target is null ? "latest" : target == "0" ? "none" : "other";
        return await CommandAsync(action, MigrationsWords.UpdatingStage(to, target ?? string.Empty), asks: true, cancel, async (setting, work) =>
        {
            EfOutcome updated = await RunAsync(setting, target is null ? ["database", "update"] : ["database", "update", target], work);
            if (!updated.Succeeded)
            {
                return Failed(setting, updated);
            }
            // Its success says the database: the listed migrations up to the target applied, while the listing is of this build.
            if (Listed && setting.Context == Context && migrationsRead is { } listedFrom && UpdatedTo(target) is { } rows
                && await BackgroundWork.Run(() => ReadOf(setting)) == listedFrom)
            {
                Migrations = rows;
                reached = true;
                then = Relist.Checked;
            }
            return Done(MigrationsWords.Updated(to, setting.Context ?? setting.Project.Name, target ?? string.Empty));
        }, Relist.Listed);
    }

    /// <summary>The listed migrations as an update to the target leaves the database (null for the latest, "0" for none):
    /// those up to it applied, the rest not; null when the target is not among them.</summary>
    private IReadOnlyList<MigrationRow>? UpdatedTo(string? target)
    {
        int at = target is null ? Migrations.Count - 1
            : target == "0" ? -1
            : Migrations.ToList().FindIndex(m => m.Id == target) is >= 0 and var id ? id
            : Migrations.ToList().FindIndex(m => m.Name == target);
        return target is not null && target != "0" && at < 0 ? null : [.. Migrations.Select((m, i) => m with { Applied = i <= at })];
    }

    /// <summary>The files a command's <c>--json</c> answer names (add's and remove's); none when it said nothing that reads,
    /// the files being on disk whatever it said.</summary>
    private static IReadOnlyList<string> Written(EfOutcome outcome) =>
        Answer(outcome) is { } answer ? [.. answer.Values] : [];

    /// <summary>The migration add made or remove deleted, by its file's name (its id); null when the answer does not say.</summary>
    private static string? MigrationId(EfOutcome outcome) =>
        Answer(outcome)?.GetValueOrDefault("migrationFile") is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file) : null;

    /// <summary>A command's <c>--json</c> answer's text values by name; null when it said nothing that reads.</summary>
    private static Dictionary<string, string>? Answer(EfOutcome outcome)
    {
        if (outcome.Data.Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(outcome.Data);
            return read.RootElement.ValueKind != JsonValueKind.Object ? null
                : read.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The applied migrations an update to the target takes back, newest first.</summary>
    public IReadOnlyList<MigrationRow> RolledBack(string target)
    {
        int at = target == "0" ? -1 : Migrations.ToList().FindIndex(m => m.Id == target || m.Name == target);
        return [.. Migrations.Skip(at + 1).Where(m => m.Applied == true).Reverse()];
    }

    /// <summary>Scripts the migrations from one to another (null for the start and the latest), idempotent or not, into a
    /// file of the state's opened in an editor tab.</summary>
    public Task<EfDone> ScriptAsync(string action, string? from, string? to, bool idempotent, CancellationToken cancel = default) =>
        CommandAsync(action, MigrationsWords.ScriptingStage, asks: true, cancel, async (setting, work) =>
        {
            string folder = Directory.CreateDirectory(Path.Combine(parts.StateFolder(), "cache", "migrations")).FullName;
            string file = Path.Combine(folder, $"{setting.Context ?? setting.Project.Name}-{from ?? "0"}-{to ?? "latest"}{(idempotent ? "-idempotent" : string.Empty)}.sql");
            List<string> arguments = ["migrations", "script", from ?? "0"];
            if (to is not null)
            {
                arguments.Add(to);
            }
            arguments.AddRange(["--output", file]);
            if (idempotent)
            {
                arguments.Add("--idempotent");
            }
            EfOutcome scripted = await RunAsync(setting, arguments, work);
            if (!scripted.Succeeded)
            {
                return Failed(setting, scripted, idempotent);
            }
            parts.OpenFile(PathNames.Canonical(file));
            return Done(MigrationsWords.Scripted(idempotent ? "yes" : "no", setting.Context ?? setting.Project.Name, PathNames.Canonical(file)));
        });

    /// <summary>Bundles the chosen context's migrations into a program that applies them, in the startup project's output.</summary>
    public Task<EfDone> BundleAsync(string action, CancellationToken cancel = default) =>
        CommandAsync(action, MigrationsWords.BundlingStage, asks: true, cancel, async (setting, work) =>
        {
            string bundle = Path.Combine(setting.Startup.Folder, "bin", "Migrations", OperatingSystem.IsWindows() ? "efbundle.exe" : "efbundle");
            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            EfOutcome bundled = await RunAsync(setting, ["migrations", "bundle", "--force", "--output", bundle], work);
            return bundled.Succeeded ? Done(MigrationsWords.Bundled(setting.Context ?? setting.Project.Name, PathNames.Shown(bundle, parts.Session().Folder)))
                : Failed(setting, bundled);
        });

    /// <summary>What a command's end reads again: nothing, the model's check, the listing with it, each once built again
    /// where the command changed the code (a migration added or removed is in no build yet).</summary>
    [Flags]
    private enum Relist
    {
        No = 0,
        Checked = 1,
        Listed = 2 | Checked,
        Built = 4,
    }

    /// <summary>What the command under way reads again as it ends, lowered by a command whose own output said the rest.</summary>
    private Relist then;

    /// <summary>One command of the window: one at a time, under its action's work; found ready (asking about each gap when
    /// <paramref name="asks"/>, the person's commands; listing only says them), built, then run; the listing read again
    /// after a command that changes it.</summary>
    private async Task<EfDone> CommandAsync(string action, Words stage, bool asks, CancellationToken cancel, Func<EfSetting, Working, Task<EfDone>> run,
        Relist relist = Relist.No, bool build = true)
    {
        UiThread.Check();
        ProjectSession session = parts.Session();
        // A command asked for as the window lists by itself goes on once the listing is done.
        if (running is not null && action != AppActions.MigrationsRefresh && listing is { IsCompleted: false } pending)
        {
            await pending;
        }
        if (running is { } under)
        {
            return Said(new EfDone(false, ActionWork.Busy(under)));
        }
        if (parts.Work.Begin(action, MigrationsWords.Going(stage)) is not { } work)
        {
            return Said(new EfDone(false, ActionWork.Busy(parts.Work.Of(action)!)));
        }
        running = work;
        commandStage = stage;
        then = relist;
        settled = false;
        // The caller's giving up stops this command alone, never the window's other work.
        using CancellationTokenRegistration stopping = cancel.Register(work.Cancel);
        try
        {
            // The lists made from what every project declares, read now where the window is hidden (an agent's command).
            if (!session.EfProjects.IsWholeOf(session.Projects))
            {
                work.Say(MigrationsWords.ReadingContexts);
                await session.EfProjects.WholeAsync(() => session.Projects);
                if (session == bound)
                {
                    Follow();
                }
            }
            (EfSetting? setting, Words? why) = await PrepareAsync(session, work, asks, build);
            if (setting is null)
            {
                return Said(new EfDone(false, why!));
            }
            await SettleAsync(setting, work);
            EfDone done = await run(setting, work);
            if (done.Done && then != Relist.No && !work.IsStopped && session == parts.Session()
                && (!then.HasFlag(Relist.Built) || await BuildAsync(setting.Project, setting.Startup, setting.Framework, work) is null))
            {
                await SettleAsync(setting, work);
                string? read = await BackgroundWork.Run(() => ReadOf(setting));
                // The contexts too where none was known, as a command run before any listing leaves it to dotnet ef's only one.
                await ReadAsync(setting with { Context = Context }, read, work, contexts: Context is null, list: then.HasFlag(Relist.Listed));
            }
            return Said(done);
        }
        finally
        {
            running = null;
            toolRestore = null;
            commandStage = null;
            work.Dispose();
            if (session == parts.Session())
            {
                // Awaited, so what the command answers has the gaps as they now stand.
                await FindGapsAsync();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private EfDone Said(EfDone done)
    {
        parts.Say(done.Said, done.Done ? null : StatusPlace.Window(parts.Dock, ToolWindowIds.Migrations));
        return done;
    }

    private EfDone Done(Words said)
    {
        Failure = null;
        return new EfDone(true, said);
    }

    private EfDone Failed(EfSetting setting, EfOutcome outcome, bool idempotent = false)
    {
        Failure = EfGaps.After(outcome, setting.Startup.Name, setting.Runtime, idempotent);
        return new EfDone(false, Failure.Fixes.Count > 0 ? MigrationsWords.FailedFix(Failure.Said, Failure.Fixes[0].Title) : Failure.Said);
    }

    /// <summary>What a command runs against, made ready: the choices, the restore (built first when there is none), each gap
    /// asked about and fixed (or refused, saying it), then Passenger's own build of the startup project and the project.</summary>
    private async Task<(EfSetting? Setting, Words? Why)> PrepareAsync(ProjectSession session, Working work, bool asks, bool build = true)
    {
        if (session.Folder is null)
        {
            return (null, Refusals.NoFolder);
        }
        if (Project is not { } project || Startup is not { } startup)
        {
            return (null, Refusals.NoEfCore);
        }
        (string? framework, string folder, IReadOnlyDictionary<string, string> environment) = await RunOfAsync(startup, work);
        if (EfRestore.Of(startup.Path, framework) is not { Runtime: not null })
        {
            if (await BuildAsync(project, startup, framework, work) is { } unbuilt)
            {
                return (null, unbuilt);
            }
        }
        EfRestore? restore = EfRestore.Of(startup.Path, framework);
        if (restore?.Runtime is not { } runtime)
        {
            return (null, MigrationsWords.NoEfCoreIn(startup.Name));
        }
        bool changed = false;
        foreach (EfGap gap in EfGaps.Before(EfTools.Find(startup.Folder), restore, startup.Name))
        {
            if (gap.Fixes.Count == 0 && !gap.Blocks)
            {
                continue;
            }
            if (!asks || parts.AsksNoOne() || Mcp.AgentCall.Current is not null)
            {
                if (!gap.Blocks)
                {
                    continue;
                }
                return (null, gap.Fixes.Count > 0 ? MigrationsWords.FixFirst(gap.Said, gap.Fixes[0].Title) : gap.Said);
            }
            List<string> answers = [.. gap.Fixes.Where(f => f.Kind is not (EfFixKind.ChooseStartup or EfFixKind.ChooseContext)).Select(f => f.Title)];
            if (!gap.Blocks)
            {
                answers.Add("Go On");
            }
            string? answer = await parts.Choose(Title(work.Stage.Shown), $"{gap.Said}.{NetworkNote(gap)}", answers, answers.FirstOrDefault());
            if (work.IsStopped || session != parts.Session())
            {
                return (null, MigrationsWords.Stopped);
            }
            if (answer is null)
            {
                return (null, gap.Said);
            }
            if (gap.Fixes.FirstOrDefault(f => f.Title == answer) is not { } fix)
            {
                continue;
            }
            if (await FixAsync(fix, session, startup, runtime, work) is { } unfixed)
            {
                return (null, unfixed);
            }
            changed = true;
        }
        if (work.IsStopped || session != parts.Session())
        {
            return (null, MigrationsWords.Stopped);
        }
        if ((build || changed) && await BuildAsync(project, startup, framework, work) is { } failed)
        {
            return (null, failed);
        }
        if (changed)
        {
            restore = EfRestore.Of(startup.Path, framework) ?? restore;
        }
        if (EfTools.Find(startup.Folder) is not { } tool)
        {
            return (null, MigrationsWords.NoEfTool(EfGaps.PinFix(runtime).Title));
        }
        // The run's database where the environment is the run's: a compose run's or an AppHost's, as this machine reaches it.
        EfRunDatabase? database = await RunDatabaseAsync(session, startup, work.Stopping);
        if (database is not null)
        {
            environment = new Dictionary<string, string>(environment.Concat(database.Environment).GroupBy(e => e.Key).Select(g => g.Last()));
        }
        Database = database?.Where;
        // The run's working folder once built, which a build makes where it is the output folder.
        folder = Directory.Exists(folder) ? folder : startup.Folder;
        (IReadOnlyList<string>? outputs, EfLaunch? launch) = await BuiltAsync(project, startup, framework, tool, defaults, folder, parts.Session().Projects, work.Stopping);
        // With none chosen yet, the context the tool's defaults name, as dotnet ef would take it.
        string? context = Context ?? (EfDefaults.ReadsDefaults(tool) ? defaults?.Context : null);
        return (new EfSetting(project, startup, framework, folder, environment, context, tool, restore.Runtime ?? runtime)
            { Database = database, Outputs = outputs, Launch = launch }, null);
    }

    /// <summary>The database a compose run's or an AppHost's run of the startup project gives it, as this machine reaches it,
    /// where the environment is the run's (none chosen in the window); null for the one its configuration gives.</summary>
    private static async Task<EfRunDatabase?> RunDatabaseAsync(ProjectSession session, ProjectModel startup, CancellationToken cancel)
    {
        if (session.MigrationEnvironments.Of(startup) is not null)
        {
            return null;
        }
        return (session.ContainerRun is { IsStopped: false } containers ? await EfDatabases.OfComposeAsync(containers, startup, cancel) : null)
            ?? EfDatabases.OfAspire(session.Aspire, startup);
    }

    /// <summary>The database the chosen context's migrations go to, as dotnet ef reaches it: the connection its design-time
    /// factory, else its registration or the context, names (<see cref="EfConnections"/>), read from the startup project's run as a command gives it (a compose run's or an AppHost's
    /// database first) then the settings its environment reads; else the connection string its code writes whole. Why not
    /// where none is told or the window is busy, said.</summary>
    public async Task<(EfConnection? Found, Words? Why)> ConnectionAsync(string action)
    {
        UiThread.Check();
        ProjectSession session = parts.Session();
        if (Project is not { } project || Startup is not { } startup)
        {
            return (null, Said(new EfDone(false, Refusals.NoEfCore)).Said);
        }
        // Asked before any listing, the project's contexts are listed first, as dotnet ef names them; asked as one runs, after it.
        if (running is not null && listing is { IsCompleted: false } pending)
        {
            await pending;
        }
        if (Context is null && Contexts.Count == 0 && running is null && await ListAsync(AppActions.MigrationsRefresh) is { Done: false } unlisted)
        {
            return (null, unlisted.Said);
        }
        if ((Context ?? (Contexts is [var only] ? only.Name : null)) is not { } context)
        {
            return (null, Said(new EfDone(false, Refusals.NoEfContext)).Said);
        }
        if (parts.Work.Begin(action, MigrationsWords.FindingDatabase(context)) is not { } work)
        {
            return (null, Said(new EfDone(false, ActionWork.Busy(parts.Work.Of(action)!))).Said);
        }
        using (work)
        {
            (_, _, IReadOnlyDictionary<string, string> environment) = await RunOfAsync(startup, work);
            // dotnet ef runs in the startup project's folder, where a SQLite file's relative path is read.
            string folder = startup.Folder;
            EfRunDatabase? database = await RunDatabaseAsync(session, startup, work.Stopping);
            string named = EnvironmentOf(startup).Name;
            CSharpParseOptions options = CompilationSymbols.Of(project);
            ProjectModel[] reading = [.. new[] { startup, project }.Distinct()];
            (EfConnection? found, Words? why) = await BackgroundWork.Run(() =>
            {
                Dictionary<string, string> values = DatabaseDiscovery.Configured(startup, named);
                foreach ((string key, string given) in environment.Concat(database?.Environment ?? new Dictionary<string, string>()))
                {
                    DatabaseDiscovery.Take(values, key, given);
                }
                (string Path, string Text)[] code = [.. reading.SelectMany(p => FolderWalk.ProjectFiles(p, ".cs")).Distinct(PathNames.Comparer).Select(f => (f, Code(f)))];
                // The provider's package may be the context's project's alone, which the startup project only references.
                string[] packages = [.. reading.SelectMany(p => p.Packages).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (EfConnections.NameOf(context, code, options, [.. values.Keys]) is { } told && values.TryGetValue(told.Name, out string? value))
                {
                    return ((EfConnection?)new EfConnection(told.Name, value, database is null ? $"used by {context}'s migrations" : $"used by {context}'s migrations: {database.Where}",
                        folder, packages) { Running = database is not null && database.Environment.ContainsKey($"ConnectionStrings__{told.Name}") }, (Words?)null);
                }
                return EfConnections.WrittenOf(context, code, options) is { } written
                    ? (new EfConnection(EfConnections.Assumed(context), written, $"used by {context}'s migrations", folder, packages), null)
                    : (null, EfConnections.FactoryOf(context, code, options) is { } factory ? Refusals.NoEfFactoryConnection(context, factory) : Refusals.NoEfConnection(context, named));
            }, work.Stopping);
            if (found is null)
            {
                return (null, Said(new EfDone(false, why!)).Said);
            }
            work.Tell(true, MigrationsWords.MigrationsUse(context, found.Name));
            return (found, null);
        }
    }

    /// <summary>The folders a build puts the startup project's assembly in, and the project's where the startup project does
    /// not reference it (else its copy is in the startup project's), as MSBuild evaluates them with what a build of each
    /// is given, null when either cannot be said; and ef.dll's launch from the same evaluations (<see cref="EfLaunch"/>),
    /// null where dotnet ef runs.</summary>
    private static async Task<(IReadOnlyList<string>? Outputs, EfLaunch? Launch)> BuiltAsync(ProjectModel project, ProjectModel startup, string? framework, EfTool tool,
        EfDefaults? defaults, string working, IReadOnlyList<ProjectModel> all, CancellationToken cancel)
    {
        List<string> folders = [];
        Dictionary<ProjectModel, IReadOnlyDictionary<string, string>> evaluated = [];
        foreach (ProjectModel each in project == startup ? [startup] : new[] { startup, project })
        {
            Dictionary<string, string> globals = global::Passenger.App.Workspace.ProjectModels.Properties(null, each);
            if (framework is not null)
            {
                globals[MSBuildNames.TargetFramework] = framework;
            }
            try
            {
                ProjectHost host = await ProjectHosts.ForAsync(each.Folder, cancel);
                Host.PropertiesAnswer answer = await host.PropertiesAsync(each.Path, globals, [Host.CarriedProperties.TargetPath, .. EfLaunch.Properties], cancel);
                if (answer.Values.GetValueOrDefault(Host.CarriedProperties.TargetPath) is not { Length: > 0 } target)
                {
                    return (null, null);
                }
                evaluated[each] = answer.Values;
                if (each == startup || !EfTargets.References(startup, project, all))
                {
                    folders.Add(Path.GetDirectoryName(PathNames.Canonical(target))!);
                }
            }
            catch (ProjectHostFailed)
            {
                return (null, null);
            }
        }
        EfLaunch? launch = await BackgroundWork.Run(() => EfLaunch.Of(tool, defaults, project.Path, evaluated[project], startup.Path, evaluated[startup], working));
        return (folders, launch);
    }

    /// <summary>What dotnet ef reads of a setting's build, which none of its reads answers otherwise while it stands: the
    /// projects, framework, working folder and environment (<see cref="RestOf"/>), and the assemblies, symbols and settings
    /// in the output folders (<see cref="BuiltOf"/>); null when the outputs are not known. On any thread.</summary>
    private static string? ReadOf(EfSetting setting) => BuiltOf(setting) is { } built ? $"{RestOf(setting)}\n{built}" : null;

    /// <summary>The output folders' assemblies, symbols and settings (<see cref="BuiltInputs.Stamp"/>), which a build changes.</summary>
    private static string? BuiltOf(EfSetting setting) => setting.Outputs is { } outputs ? string.Join('\n', outputs.Select(BuiltInputs.Stamp)) : null;

    /// <summary>What dotnet ef reads but the build's output: the projects, framework, working folder (and its assemblies,
    /// symbols and settings, where it is not an output folder) and environment.</summary>
    private static string RestOf(EfSetting setting) =>
        string.Join('\n', (string[])[setting.Project.Path, setting.Startup.Path, setting.Framework ?? string.Empty, setting.Folder,
            .. setting.Environment.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"),
            setting.Outputs?.Any(o => PathNames.Same(o, setting.Folder)) == true ? string.Empty : BuiltInputs.Stamp(setting.Folder)]);

    /// <summary>What the build a command runs on made, what else dotnet ef reads, and what the build reads, while the listing
    /// is of that build, so what the command says of the listing can stand for a later build (<see cref="EfTold"/>); null
    /// otherwise, or where the host cannot say.</summary>
    private async Task<(string Others, string Built, IReadOnlyList<Host.Stamp> Reads)?> BeforeAsync(EfSetting setting, Working work)
    {
        if (!Listed || setting.Context != Context || migrationsRead is not { } listedFrom
            || await BackgroundWork.Run(() => (RestOf(setting), BuiltOf(setting))) is not (string rest, string built) || $"{rest}\n{built}" != listedFrom)
        {
            return null;
        }
        return await EfTold.ReadsAsync(setting, work.Stopping) is { } reads ? (rest, built, reads) : null;
    }

    /// <summary>Keeps what a command that wrote or deleted these files said of the listing (and of the model's check, where
    /// it knows it), to take as read once a build has them (<see cref="SettleAsync"/>).</summary>
    private async Task TellAsync(EfSetting setting, string context, (string Others, string Built, IReadOnlyList<Host.Stamp> Reads)? before, IReadOnlyList<string> files,
        bool? changed)
    {
        told = before is { } was && files.Count > 0 && await BackgroundWork.Run(() => EfTold.LeftOf(files, setting.Project.Folder, was.Reads)) is { } left
            ? new EfTold(context, was.Others, was.Built, was.Reads, left, changed)
            : null;
    }

    /// <summary>What the last command said of the listing taken as read from a build since, where all dotnet ef reads but the
    /// output is as it was, and the build's reads are those before the command but for what it left: the contexts too where
    /// their listing was of the build before it. Kept while no build since has changed the output.</summary>
    private async Task SettleAsync(EfSetting setting, Working work)
    {
        if (told is not { } said || said.Context != Context
            || await BackgroundWork.Run(() => (RestOf(setting), BuiltOf(setting))) is not (string rest, string built) || built == said.Built)
        {
            return;
        }
        told = null;
        if (rest == said.Others && await EfTold.ReadsAsync(setting, work.Stopping) is { } reads && said.Stands(reads) && Listed && said.Context == Context)
        {
            string read = $"{rest}\n{built}";
            migrationsRead = read;
            if (contextsRead == $"{said.Others}\n{said.Built}")
            {
                contextsRead = read;
            }
            if (said.Changed is { } changed)
            {
                checks[said.Context] = (read, changed);
            }
        }
    }

    /// <summary>Whether dotnet ef warned that it could not reach the database, going on without it.</summary>
    private static bool Unreached(string warning) => warning.Contains("An error occurred while accessing the database", StringComparison.Ordinal);

    private static string Title(string stage)
    {
        string words = stage.TrimEnd('…');
        return words.Length == 0 ? "Migrations" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>What a fix reaches beyond the folder, said in its question, since asking before the network is the rule.</summary>
    private static string NetworkNote(EfGap gap) =>
        gap.Fixes.Any(f => f.Kind == EfFixKind.AlignProvider) ? " Its fix reads the folder's package sources for the versions that fit, and downloads what the package cache lacks."
        : gap.Fixes.Any(f => Reaches(f.Kind)) ? " Its fix downloads from the folder's package sources what the package cache lacks."
        : string.Empty;

    /// <summary>Whether a fix can reach the network: a tool installed or restored, a package added or moved.</summary>
    private static bool Reaches(EfFixKind kind) => kind is EfFixKind.PinTool or EfFixKind.UpdateGlobalTool or EfFixKind.AddDesign or EfFixKind.RestoreTools
        or EfFixKind.AlignProvider;

    /// <summary>Puts a gap right as its fix says, from the window's button or a command's question: a file change is one
    /// step of global undo. Null once fixed, else why not.</summary>
    public async Task<Words?> FixAsync(EfFix fix)
    {
        UiThread.Check();
        ProjectSession session = parts.Session();
        if (fix.Kind is EfFixKind.ChooseStartup or EfFixKind.ChooseContext)
        {
            ChoiceAsked?.Invoke(this, fix.Kind);
            return null;
        }
        if (fix.Kind == EfFixKind.AddMigration)
        {
            AddAsked?.Invoke(this, EventArgs.Empty);
            return null;
        }
        if (Startup is not { } startup || EfRestore.Of(startup.Path)?.Runtime is not { } runtime)
        {
            return Said(new EfDone(false, MigrationsWords.BuildStartupFirst)).Said;
        }
        // A fix asked for as the window lists by itself goes on once the listing is done.
        if (running is not null && listing is { IsCompleted: false } pending)
        {
            await pending;
        }
        if (running is { } under)
        {
            return Said(new EfDone(false, ActionWork.Busy(under))).Said;
        }
        if (parts.Work.Begin(AppActions.MigrationsFix, $"{fix.Title}…") is not { } work)
        {
            return Said(new EfDone(false, ActionWork.Busy(parts.Work.Of(AppActions.MigrationsFix)!))).Said;
        }
        running = work;
        try
        {
            if (Reaches(fix.Kind) && !parts.AsksNoOne() && Mcp.AgentCall.Current is null
                && Gaps.Concat(Failure is { } failed ? [failed] : []).FirstOrDefault(g => g.Fixes.Contains(fix)) is { } gap
                && await parts.Choose(fix.Title, $"{gap.Said}.{NetworkNote(gap)}", [fix.Title], fix.Title) is null)
            {
                return MigrationsWords.FixCancelled;
            }
            fixedSaid = null;
            Words? why = await FixAsync(fix, session, startup, runtime, work);
            parts.Say(why ?? fixedSaid ?? MigrationsWords.FixDone(fix.Title));
            work.Tell(why is null, why ?? fixedSaid ?? MigrationsWords.FixDone(fix.Title));
            return why;
        }
        finally
        {
            running = null;
            work.Dispose();
            if (session == parts.Session())
            {
                // Awaited, so what the command answers has the gaps as they now stand.
                await FindGapsAsync();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The files the last fix wrote, which an agent's apply_code_action names.</summary>
    public IReadOnlyList<string> Touched { get; private set; } = [];

    /// <summary>What the fix under way says once done, where it says more than that it is done.</summary>
    private Words? fixedSaid;

    private async Task<Words?> FixAsync(EfFix fix, ProjectSession session, ProjectModel startup, string runtime, Working work)
    {
        work.Say(MigrationsWords.Going(fix.Title));
        Touched = [];
        switch (fix.Kind)
        {
            case EfFixKind.PinTool:
                return await PinToolAsync(session, startup, runtime, work);
            case EfFixKind.UpdateGlobalTool:
                return await ToolCommandAsync(["tool", "update", "--global", EfTools.Id, "--version", runtime, "--allow-downgrade"], startup.Folder, work);
            case EfFixKind.RestoreTools:
                return await ToolCommandAsync(["tool", "restore"], startup.Folder, work);
            case EfFixKind.AddDesign:
                return await AddPackageAsync(startup, EfRestore.DesignPackage, runtime);
            case EfFixKind.AlignProvider:
                return await AlignAsync(startup, runtime, work);
            case EfFixKind.AddFactory:
                return await AddFactoryAsync();
            case EfFixKind.ScriptPlain:
                return null;
            case EfFixKind.AddMigration:
                AddAsked?.Invoke(this, EventArgs.Empty);
                return null;
            default:
                ChoiceAsked?.Invoke(this, fix.Kind);
                return null;
        }
    }

    /// <summary>The local tool pinned to the project's EF Core, in the nearest manifest inside the folder, else one Passenger
    /// makes at the folder's root as its .editorconfig asks: one step of global undo, which takes the manifest back (or
    /// away). The manifest dotnet tool writes anew keeps the line breaks, encoding and byte order mark it had.</summary>
    private async Task<Words?> PinToolAsync(ProjectSession session, ProjectModel startup, string runtime, Working work)
    {
        string root = session.Folder!;
        string? manifest = EfTools.NearestManifest(startup.Folder) is { } near && PathNames.IsUnder(near, root) ? near : null;
        EfTool? local = manifest is null ? null : EfTools.Local(startup.Folder);
        using CommandHistory.Recording recording = parts.Editors.Commands.RecordWrites($"Pin {EfTools.Id} to {runtime}");
        FileText? had;
        if (manifest is null)
        {
            manifest = Path.Combine(root, "dotnet-tools.json");
            recording.Watch(manifest);
            had = FileText.SaveNew(manifest, EfTools.NewManifest).Written;
        }
        else
        {
            recording.Watch(manifest);
            had = FileText.Load(manifest);
        }
        IReadOnlyList<string> arguments = local is null
            ? ["tool", "install", EfTools.Id, "--version", runtime, "--tool-manifest", manifest]
            : ["tool", "update", EfTools.Id, "--version", runtime, "--allow-downgrade", "--tool-manifest", manifest];
        Words? failed = await ToolCommandAsync(arguments, Path.GetDirectoryName(manifest)!, work);
        if (had is not null && FileText.Load(manifest) is { } now && LineEndings.In(now.Text, had.Break) is var kept
            && (kept != now.Text || now.EncodingName != had.EncodingName || now.CrBreaks != had.CrBreaks))
        {
            // dotnet tool writes it with '\n' and no byte order mark, whatever it had.
            FileText.SaveOver(manifest, had, kept);
        }
        parts.FollowWritten([manifest]);
        Touched = [manifest];
        return failed;
    }

    /// <summary>A <c>dotnet tool</c> command, its output in the Build window: null once it succeeded, else its last words.</summary>
    private async Task<Words?> ToolCommandAsync(IReadOnlyList<string> arguments, string folder, Working work)
    {
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            start = ChildProcesses.DotnetCommand(arguments, folder);
        }
        catch (FileNotFoundException missing)
        {
            return missing.Message;
        }
        parts.Views.Build.Append($"{ChildProcesses.Shown(start)}\n");
        Task<EfOutcome> running = EfRun.RunAsync(start, parts.Views.Build.Sink, work.Stopping);
        BackgroundWork.Track(running);
        EfOutcome outcome = await running;
        return outcome.ExitCode == 0 ? null
            : outcome.Lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() is { } last ? Words.Verbatim(last)
            : MigrationsWords.ToolEnded(arguments[0], arguments[1], ExitCodes.Said(outcome.ExitCode));
    }

    /// <summary>A package added to a project through the NuGet window's one way, as one step of global undo.</summary>
    private async Task<Words?> AddPackageAsync(ProjectModel project, string id, string version)
    {
        using CommandHistory.Recording recording = parts.Editors.Commands.RecordWrites($"Add {id} {version} to {project.Name}");
        PackageOutcome outcome = await parts.Install(project, id, version, recording);
        Touched = outcome.Changed;
        return outcome.Failure($"{id} could not be added to {project.Name}");
    }

    /// <summary>A provider behind the startup project's EF Core brought into step, as the feeds' metadata says fits
    /// (<see cref="EfProviders.AlignAsync"/>): every project's packages moved as one step of global undo. A project's move
    /// that fails is put back, and the moves of the projects before it are said, for Undo to take back.</summary>
    private async Task<Words?> AlignAsync(ProjectModel startup, string runtime, Working work)
    {
        if (EfRestore.Of(startup.Path) is not { Behind: { } behind } restore)
        {
            return null;
        }
        work.Say(MigrationsWords.ReadingVersions(behind.Id));
        IReadOnlyList<ProjectModel> projects = [.. new[] { startup, Project ?? startup }.Distinct()];
        EfAlignment? alignment;
        try
        {
            alignment = await EfProviders.AlignAsync(await parts.Feed(), projects, behind, runtime, restore.Framework, work.Stopping);
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed) && !work.IsStopped)
        {
            return MigrationsWords.FeedsUnread(failed.Message);
        }
        if (alignment is null)
        {
            return MigrationsWords.NoFittingVersion(behind.Id, runtime);
        }
        work.Say(MigrationsWords.Going(alignment.Title));
        using CommandHistory.Recording recording = parts.Editors.Commands.RecordWrites(alignment.Title);
        foreach (EfMove move in alignment.Moves)
        {
            IReadOnlyList<string> moved = Touched;
            PackageOutcome outcome = await parts.InstallSet(move.Project, move.Packages, recording, null);
            Touched = [.. Touched, .. outcome.Changed];
            if (outcome.Failure($"{alignment.Title} failed in {move.Project.Name}") is { } failed)
            {
                return moved.Count == 0 ? failed : MigrationsWords.PartlyAligned(failed, PackageOutcome.Named(moved));
            }
        }
        return null;
    }

    /// <summary>A design-time factory for the context, beside the project's file, from a template: the provider's own
    /// <c>Use…</c> with the connection string of the context's name (<see cref="EfConnections"/>), read from the startup
    /// project's configuration as dotnet ef runs, in the environment the window runs it in; where nothing tells that name,
    /// the context's own, said.</summary>
    private async Task<Words?> AddFactoryAsync()
    {
        if (Project is not { } project || Startup is not { } startup)
        {
            return Refusals.NoEfCore;
        }
        string? named = (Failure?.Fixes.FirstOrDefault(f => f.Kind == EfFixKind.AddFactory)?.Title)?.Split(' ').Last() ?? Context;
        EfContext? context = Contexts.FirstOrDefault(c => c.Name == named || c.FullName == named) ?? (named is null ? null : new EfContext(named, named));
        if (context is null)
        {
            return Refusals.NoEfContext;
        }
        EfRestore? restore = EfRestore.Of(project.Path);
        string file = Path.Combine(project.Folder, $"{context.Name}Factory.cs");
        if (File.Exists(file))
        {
            return MigrationsWords.FileThere(Path.GetFileName(file));
        }
        CSharpParseOptions options = CompilationSymbols.Of(project);
        string environment = EnvironmentOf(startup).Name;
        (string Name, string Told)? told = await BackgroundWork.Run(() => EfConnections.NameOf(context.Name,
            new[] { startup, project }.Distinct().SelectMany(p => FolderWalk.ProjectFiles(p, ".cs")).Distinct(PathNames.Comparer).Select(f => (f, Code(f))),
            options, EfConnections.Configured(startup.Folder, environment)));
        if (File.Exists(file))
        {
            return MigrationsWords.FileThere(Path.GetFileName(file));
        }
        string connection = told?.Name ?? EfConnections.Assumed(context.Name);
        using CommandHistory.Recording recording = parts.Editors.Commands.Record($"Add a Design-Time Factory for {context.Name}");
        recording.Watch(file);
        FileText.SaveNew(file, EfTemplates.Factory(context, restore, connection));
        fixedSaid = told is { } found
            ? MigrationsWords.FactoryAdded(Path.GetFileName(file), connection, found.Told)
            : MigrationsWords.FactoryAddedUnnamed(Path.GetFileName(file), connection, startup.Name, context.Name);
        Touched = [file];
        parts.FollowWritten([file]);
        parts.OpenFile(PathNames.Canonical(file));
        return null;
    }

    /// <summary>A C# file's text on disk; none for one that cannot be read.</summary>
    private static string Code(string file)
    {
        try
        {
            return FileText.Load(file)?.Text ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Builds the project and its startup project with Passenger's own build, the Build window and Problems
    /// showing it as any build's: null once built, else why not.</summary>
    private async Task<Words?> BuildAsync(ProjectModel project, ProjectModel startup, string? framework, Working work)
    {
        settled = false;
        work.Say(MigrationsWords.Building(startup.Name));
        BuildRunController builds = parts.Builds();
        bool covers = project == startup || startup.ProjectReferences.Count > 0 && EfTargets.References(startup, project, parts.Session().Projects);
        if (!covers)
        {
            BuildOutcome first = await builds.BuildForAsync(new BuildStep(project.Path, null), work);
            if (!first.Succeeded)
            {
                return first.Message;
            }
        }
        BuildOutcome built = await builds.BuildForAsync(new BuildStep(startup.Path, framework), work);
        return built.Succeeded ? null : built.Message;
    }

    /// <summary>What the run of the startup project would have: the framework, working folder (which a build may make) and
    /// variables of the run configuration that runs it (the chosen one first), so dotnet ef reaches the database the run
    /// uses, the project's folder without one; its environment the one the window runs dotnet ef in (<see cref="EnvironmentOf"/>).</summary>
    private async Task<(string? Framework, string Folder, IReadOnlyDictionary<string, string> Environment)> RunOfAsync(ProjectModel startup, Working work)
    {
        string? framework = startup.TargetFrameworks.Count > 1 ? startup.TargetFrameworks[0] : null;
        RunConfiguration? runs = RunsOf(startup);
        // The framework the tool's defaults name, where the startup project targets it, as dotnet ef takes it.
        string? given = defaults?.Framework is { } framed && startup.TargetFrameworks.Contains(framed, StringComparer.OrdinalIgnoreCase) ? framed : null;
        string environment = EnvironmentOf(startup).Name;
        if (runs is null)
        {
            return (given ?? framework, startup.Folder, In(new Dictionary<string, string>(), environment));
        }
        work.Say(MigrationsWords.ReadingRun(runs.Name));
        (LaunchPlan? plan, _) = await parts.Builds().ResolvePlanAsync(runs, null, work.Stopping);
        return plan is null ? (given ?? framework, startup.Folder, In(runs.Environment, environment))
            : (given ?? plan.Framework ?? framework, plan.WorkingDirectory, In(plan.Environment, environment));
    }

    /// <summary>The run configuration that runs the startup project: the chosen one where it does, else the first that runs
    /// the project itself; null with none.</summary>
    private RunConfiguration? RunsOf(ProjectModel startup)
    {
        RunConfigurations configurations = parts.Session().Configurations;
        return configurations.Chosen is { Kind: RunKind.Project } chosen && PathNames.Same(chosen.Project, startup.Path) ? chosen
            : configurations.All.FirstOrDefault(c => c.Kind == RunKind.Project && c.Program.Length == 0 && PathNames.Same(c.Project, startup.Path));
    }

    /// <summary>A run's variables with the environment given as both ASP.NET Core and the .NET host read it, in place of any
    /// the run names.</summary>
    private static Dictionary<string, string> In(IReadOnlyDictionary<string, string> variables, string environment) =>
        new(variables.Where(v => !v.Key.Equals(AspNetCoreVariables.AspNetCoreEnvironment, StringComparison.OrdinalIgnoreCase)
            && !v.Key.Equals(AspNetCoreVariables.DotnetEnvironment, StringComparison.OrdinalIgnoreCase)), StringComparer.Ordinal)
        {
            [AspNetCoreVariables.AspNetCoreEnvironment] = environment,
            [AspNetCoreVariables.DotnetEnvironment] = environment,
        };

    /// <summary>Runs dotnet ef with the setting's projects, framework and context, never building (its ef.dll alone where
    /// the setting has its launch), its lines in the Build window as they come; EF's warning that the tool is older than the
    /// runtime kept as a gap.</summary>
    private async Task<EfOutcome> RunAsync(EfSetting setting, IReadOnlyList<string> verb, Working work, bool retried = false)
    {
        IReadOnlyList<string> context = setting.Context is { } named ? ["--context", named] : [];
        List<string> arguments = [.. verb, "--project", setting.Project.Path, "--startup-project", setting.Startup.Path, "--no-build", "--prefix-output"];
        if (setting.Framework is { } framework)
        {
            arguments.AddRange(["--framework", framework]);
        }
        arguments.AddRange(context);
        // A run's database goes only in the environment: any process can read a command line.
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            if (setting.Launch is { } launch)
            {
                await launch.ReadyAsync();
                start = launch.Command([.. verb, .. context]);
            }
            else
            {
                start = setting.Tool.Command(arguments, setting.Folder);
            }
        }
        catch (FileNotFoundException missing)
        {
            return EfOutcome.Unstarted(missing.Message);
        }
        ChildProcesses.WithVariables(start, setting.Environment);
        work.Say(MigrationsWords.RunningEf(commandStage ?? MigrationsWords.ListingStage, string.Join(' ', verb.TakeWhile(v => !v.StartsWith('-')))));
        // The database a run gives is said by where it reaches, its connection string being the environment's.
        parts.Views.Build.Append($"\n{ChildProcesses.Shown(start)}\n"
            + (setting.Database is { } database ? $"Against {database.Where}\n" : string.Empty));
        Task<EfOutcome> running = EfRun.RunAsync(start, parts.Views.Build.Sink, work.Stopping);
        BackgroundWork.Track(running);
        EfOutcome outcome = await running;
        // A manifest's tool never restored, whose package the cache holds: restored once, reaching no feed, and run again.
        if (!outcome.Succeeded && !retried && setting.Tool is { Place: EfToolPlace.Local } local && NotRestored(outcome)
            && EfTools.Cached(local.Version, setting.Startup.Folder))
        {
            toolRestore ??= ToolCommandAsync(["tool", "restore", "--tool-manifest", local.Where], Path.GetDirectoryName(local.Where)!, work);
            if (await toolRestore is null)
            {
                return await RunAsync(setting, verb, work, retried: true);
            }
        }
        settled |= outcome.Succeeded;
        if (outcome.Succeeded && EfGaps.Warned(outcome, setting.Startup.Name) is { } older && !Gaps.Any(g => g.Said == older.Said))
        {
            Gaps = [.. Gaps, older];
        }
        return outcome;
    }

    /// <summary>The contexts (with <paramref name="contexts"/>, unless what they were listed from stands), the chosen
    /// context's migrations (<paramref name="list"/>, or a context their answer chose; none chosen, the setting's, else the
    /// only one's) and its model's check, side by side where ef.dll starts alone (its build host copied first) or once a run
    /// has left dotnet ef nothing to copy into the output (<see cref="settled"/>), else one by one. What the build stands as
    /// (<paramref name="read"/>, <see cref="ReadOf"/>) is kept with each answer; <paramref name="fresh"/> takes none earlier.</summary>
    private async Task<EfDone?> ReadAsync(EfSetting setting, string? read, Working work, bool contexts, bool list = true, bool fresh = false)
    {
        string? remembered = fresh ? null : read;
        bool together = settled || setting.Launch is not null;
        string? context = Context ?? setting.Context;
        Task<EfOutcome>? naming = contexts && (remembered is null || remembered != contextsRead) ? RunAsync(setting with { Context = null }, ["dbcontext", "list", "--json"], work) : null;
        if (naming is not null && !together)
        {
            if (Named(setting, await naming, read) is { } unnamed)
            {
                return unnamed;
            }
            naming = null;
            context = Context;
            together = settled || setting.Launch is not null;
        }
        bool none = contexts && naming is null && Context is null;
        Task<EfOutcome>? listing = list && !none ? RunAsync(setting with { Context = context }, ["migrations", "list", "--json"], work) : null;
        // Beside the listing where the context had migrations; its answer is kept while the listing finds some.
        Task<bool>? checking = (together || listing is null) && context is not null && Migrations.Count > 0
            ? PendingAsync(setting with { Context = context }, read, work, fresh) : null;
        await Task.WhenAll(new Task?[] { naming, listing, checking }.OfType<Task>());
        if (naming is not null)
        {
            if (Named(setting, await naming, read) is { } unnamed)
            {
                return unnamed;
            }
            // Listed with no context named, dotnet ef took the only one; with another, it is read again for this one.
            if (Context != context && !(context is null && Contexts.Count == 1))
            {
                checking = null;
                listing = Context is not null ? RunAsync(setting with { Context = Context }, ["migrations", "list", "--json"], work) : null;
            }
            context = Context;
        }
        if (contexts && Context is null)
        {
            Migrations = [];
            Listed = true;
            migrationsRead = read;
            reached = null;
            return null;
        }
        if (listing is not null)
        {
            EfOutcome listed = await listing;
            if (!listed.Succeeded)
            {
                return Failed(setting, listed);
            }
            Migrations = ReadMigrations(listed.Data);
            Listed = true;
            migrationsRead = read;
            reached = Migrations.Count > 0 ? Migrations.Any(m => m.Applied is not null) : !listed.Warnings.Any(Unreached);
        }
        if (Migrations.Count > 0 && context is not null)
        {
            await ShowPendingAsync(setting, context, await (checking ?? PendingAsync(setting with { Context = context }, read, work, fresh)));
        }
        return null;
    }

    /// <summary>The contexts dotnet ef listed taken as the project's, the chosen one kept while it is among them, else the
    /// one the tool's defaults name, else the first; null once taken, else the failure.</summary>
    private EfDone? Named(EfSetting setting, EfOutcome named, string? read)
    {
        if (!named.Succeeded)
        {
            return Failed(setting, named);
        }
        Contexts = ReadContexts(named.Data);
        contextsRead = read;
        if (Context is null || !Contexts.Any(c => c.Name == Context || c.FullName == Context))
        {
            Context = Contexts.FirstOrDefault(c => defaults?.Context is { } named && (c.Name == named || c.FullName == named))?.Name ?? Contexts.FirstOrDefault()?.Name;
        }
        return null;
    }

    /// <summary>Whether a dotnet ef run has ended well since the command began and since its last build, so its runs can go
    /// side by side: each copies what it lacks of Roslyn's MSBuild build host into the startup project's output as it
    /// starts (all of it into a new output, what a package's new version brings into one), and two copying at once fail
    /// ("The file … already exists"); one that has ended leaves none to copy.</summary>
    private bool settled;

    /// <summary>Whether the setting's context's model has changes no migration covers (<c>dotnet ef migrations
    /// has-pending-model-changes</c>, EF Core 8 and later; an earlier one's refusal says nothing): unless
    /// <paramref name="fresh"/>, its last answer where what it read (<paramref name="read"/>) stands, else asked, the
    /// answer kept with what it read once it gave one.</summary>
    private async Task<bool> PendingAsync(EfSetting setting, string? read, Working work, bool fresh = false)
    {
        string context = setting.Context!;
        if (!fresh && read is not null && checks.TryGetValue(context, out (string Read, bool Changed) was) && was.Read == read)
        {
            return was.Changed;
        }
        EfOutcome checkedOut = await RunAsync(setting, ["migrations", "has-pending-model-changes"], work);
        bool changed = checkedOut.ExitCode != 0 && checkedOut.Errors.Any(e => e.Contains("Changes have been made to the model", StringComparison.Ordinal));
        if (read is not null && (checkedOut.Succeeded || changed))
        {
            checks[context] = (read, changed);
        }
        else
        {
            checks.Remove(context);
        }
        return changed;
    }

    /// <summary>A context's model check in Problems, its class found in the project to place it.</summary>
    private async Task ShowPendingAsync(EfSetting setting, string context, bool changed)
    {
        List<EfPending> pending = [.. Pending.Where(p => p.Context != context)];
        if (changed)
        {
            (string? file, Passenger.Text.TextPosition at) = await BackgroundWork.Run(() => Declaration(setting.Project, context));
            pending.Add(new EfPending(context, file, at));
        }
        Pending = pending;
        parts.ShowPending([.. Pending.Select(p => new Problem(ProblemSeverity.Warning, $"{p.Context}'s model has changes no migration covers: Add Migration… makes one",
            PendingCode, p.File, p.Position, setting.Project.Name))]);
    }

    /// <summary>Where a context's class is declared in its project, looked for in the project's own C# files; none when no
    /// file declares it.</summary>
    private static (string? File, Passenger.Text.TextPosition At) Declaration(ProjectModel project, string context)
    {
        string name = context.Split('.').Last();
        System.Text.RegularExpressions.Regex declared = new($@"\bclass\s+{System.Text.RegularExpressions.Regex.Escape(name)}\b");
        foreach (string file in FolderWalk.ProjectFiles(project, ".cs"))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            for (int line = 0; line < lines.Length; line++)
            {
                if (declared.Match(lines[line]) is { Success: true } found)
                {
                    return (file, new Passenger.Text.TextPosition(line, found.Index + found.Length - name.Length));
                }
            }
        }
        return (null, default);
    }

    /// <summary>A build the window's own commands did not start ended: once the window has listed, the migrations are listed
    /// again where the build changed what they were listed from, and the model checked, with no build of its own, so Problems
    /// says what the build left pending.</summary>
    public void AfterBuild(BuildOutcome built)
    {
        if (built.Succeeded && running is null && Listed && Gaps.All(g => !g.Blocks) && parts.Session().Folder is not null)
        {
            IdeErrors.Observe(ListAsync(AppActions.MigrationsRefresh, build: false, rereads: false));
        }
    }

    /// <summary>The command's restore of the folder's local tools, which every run that found them not restored waits for.</summary>
    private Task<Words?>? toolRestore;

    /// <summary>The stage of the command under way, which its run of dotnet ef is said under.</summary>
    private Words? commandStage;

    /// <summary>Whether dotnet said the manifest's tools are not restored, before the tool ran.</summary>
    private static bool NotRestored(EfOutcome outcome) => outcome.Lines.Any(l => EfRun.Split(l).Kind.Length == 0 && l.Contains("dotnet tool restore", StringComparison.Ordinal));

    private static IReadOnlyList<EfContext> ReadContexts(string data)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(data.Length == 0 ? "[]" : data);
            return [.. read.RootElement.EnumerateArray().Select(c => new EfContext(
                JsonMembers.Text(c, "name") ?? string.Empty,
                JsonMembers.Text(c, "fullName") ?? string.Empty))
                .Where(c => c.Name.Length > 0)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<MigrationRow> ReadMigrations(string data)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(data.Length == 0 ? "[]" : data);
            return [.. read.RootElement.EnumerateArray().Select(m => new MigrationRow(
                JsonMembers.Text(m, "id") ?? string.Empty,
                JsonMembers.Text(m, "name") ?? string.Empty,
                JsonMembers.Bool(m, "applied")))];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
