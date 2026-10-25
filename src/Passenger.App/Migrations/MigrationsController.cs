using System.Text;
using System.Text.Json;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Packages;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;

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
    Func<ProjectModel, string, string, Task<PackageOutcome>> Install,
    Func<ProjectModel, IReadOnlyList<(string Id, string Version)>, Task<PackageOutcome>> InstallSet,
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

/// <summary>A context whose model has changes no migration covers, at its class's declaration where it was found.</summary>
public sealed record EfPending(string Context, string? File, Passenger.Text.TextPosition Position);

/// <summary>A context the project holds, as <c>dotnet ef dbcontext list</c> names it.</summary>
public sealed record EfContext(string Name, string FullName);

/// <summary>What one of the window's commands came to: whether it was done, and what the status bar said of it.</summary>
public sealed record EfDone(bool Done, string Said);

/// <summary>What a command runs against: the project holding the context and its migrations, the startup project dotnet ef
/// makes it from (with the framework, folder and environment of the run configuration that runs it: the database the run
/// uses), the context, and the tool.</summary>
internal sealed record EfSetting(ProjectModel Project, ProjectModel Startup, string? Framework, string Folder, IReadOnlyDictionary<string, string> Environment,
    string? Context, EfTool Tool, string Runtime)
{
    /// <summary>The database of a compose run or an AppHost that runs the startup project, as this machine reaches it; null
    /// for the one its configuration gives.</summary>
    public EfRunDatabase? Database { get; init; }

    /// <summary>The output folders dotnet ef loads the startup project's and the project's assemblies from, as the build
    /// before the command makes them; null when MSBuild could not say, so every read runs.</summary>
    public IReadOnlyList<string>? Outputs { get; init; }
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

    /// <summary>The folder's projects that reference EF Core, in the solution's order.</summary>
    public IReadOnlyList<ProjectModel> Projects { get; private set; } = [];

    /// <summary>The project chosen, whose contexts and migrations are listed; null with none.</summary>
    public ProjectModel? Project { get; private set; }

    /// <summary>The projects dotnet ef can make the chosen project's contexts from: those that run it (that reference it, or
    /// it itself), the ones a run configuration runs first.</summary>
    public IReadOnlyList<ProjectModel> Startups { get; private set; } = [];

    public ProjectModel? Startup { get; private set; }

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

    /// <summary>Where the chosen project's contexts are declared, read from its C# files as the projects are read: where a
    /// gap's fixes are offered, beside the startup project's file.</summary>
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
    public string? Busy => running?.Stage;

    /// <summary>Follows the session the window binds: its projects read again, its listing forgotten.</summary>
    public void Bind(ProjectSession session)
    {
        UiThread.Check();
        bound = session;
        Pending = [];
        Project = null;
        Startup = null;
        Context = null;
        Forget();
        Follow();
    }

    /// <summary>The folder's projects read again (an evaluation landed, a package changed), the choices kept where they still
    /// stand, and the gaps found again.</summary>
    public void Follow()
    {
        UiThread.Check();
        IReadOnlyList<ProjectModel> all = bound?.Projects ?? [];
        Projects = [.. all.Where(UsesEf)];
        Project = Projects.FirstOrDefault(p => Project is { } was && PathNames.Same(p.Path, was.Path)) ?? Projects.FirstOrDefault();
        Startups = Project is null ? [] : StartupsOf(Project, all);
        Startup = Startups.FirstOrDefault(p => Startup is { } was && PathNames.Same(p.Path, was.Path)) ?? Startups.FirstOrDefault();
        FindGaps();
        if (Project is { } project)
        {
            BackgroundWork.Track(FindDeclaredAsync(project));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The project's contexts' declarations read off the UI thread, kept while it is still the one chosen.</summary>
    private async Task FindDeclaredAsync(ProjectModel project)
    {
        IReadOnlyList<ProjectModel> all = bound?.Projects ?? [];
        IReadOnlyList<EfPending> found = await Task.Run(() => DeclaredIn(project, all));
        if (Project == project)
        {
            Declared = found;
        }
    }

    /// <summary>The classes a project's own C# files declare deriving from a DbContext, each at its name.</summary>
    private static IReadOnlyList<EfPending> DeclaredIn(ProjectModel project, IReadOnlyList<ProjectModel> all)
    {
        List<EfPending> found = [];
        foreach (string file in FolderWalk.ProjectFiles(project, all, ".cs"))
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
                if (ContextClass().Match(lines[line]) is { Success: true } declared)
                {
                    found.Add(new EfPending(declared.Groups[1].Value, file, new Passenger.Text.TextPosition(line, declared.Groups[1].Index)));
                }
            }
        }
        return found;
    }

    /// <summary>A class declared on one line deriving from a DbContext (IdentityDbContext&lt;User&gt; too), its name captured.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\bclass\s+(\w+)\b[^{;]*:\s*[^{;]*?\b\w*DbContext\b(?!\w)")]
    private static partial System.Text.RegularExpressions.Regex ContextClass();

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for the startup project or context to be chosen in the window, a fix that is the person's choice.</summary>
    public event EventHandler<EfFixKind>? ChoiceAsked;

    /// <summary>Whether the window, shown, lists the migrations by itself now: once for each choice, while nothing it shows
    /// stops dotnet ef and nothing else runs.</summary>
    public bool WantsListing => !Listed && !tried && running is null && Startup is not null && gapsLanded == gapsAsked && Gaps.All(g => !g.Blocks);

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
        checks.Clear();
    }

    /// <summary>What the contexts were last listed from (<see cref="ReadOf"/>): listed again only once it changed.</summary>
    private string? contextsRead;

    /// <summary>What the migrations were listed from; null when a command's own output made the list, which no build has yet.</summary>
    private string? migrationsRead;

    /// <summary>Each context's model check, with what it read: its answer stands while that does.</summary>
    private readonly Dictionary<string, (string Read, bool Changed)> checks = [];

    /// <summary>Whether a project references EF Core, or a provider of it, itself.</summary>
    public static bool UsesEf(ProjectModel project) =>
        project.Packages.Any(p => p.Name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));

    /// <summary>The projects that can start a project's contexts: those that reference it, through others too, the runnable
    /// ones first; then the project itself.</summary>
    private static IReadOnlyList<ProjectModel> StartupsOf(ProjectModel project, IReadOnlyList<ProjectModel> all)
    {
        List<ProjectModel> referencing = [.. all.Where(p => p != project && References(p, project, all, []))];
        return [.. referencing.Where(p => p.IsRunnable), project, .. referencing.Where(p => !p.IsRunnable)];
    }

    private static bool References(ProjectModel from, ProjectModel to, IReadOnlyList<ProjectModel> all, HashSet<string> seen)
    {
        foreach (string reference in from.ProjectReferences)
        {
            if (PathNames.Same(reference, to.Path))
            {
                return true;
            }
            if (seen.Add(reference) && all.FirstOrDefault(p => PathNames.Same(p.Path, reference)) is { } next && References(next, to, all, seen))
            {
                return true;
            }
        }
        return false;
    }

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
    /// <paramref name="rereads"/>, the contexts and the model's check are taken from their last reads where what those read
    /// stands.</summary>
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
        CommandAsync(action, "Listing migrations", asks: false, cancel, async (setting, work) =>
        {
            string? read = await BackgroundWork.Run(() => ReadOf(setting));
            if (await ReadAsync(setting, read, work, contexts: true, fresh: rereads) is { } failed)
            {
                return failed;
            }
            if (Context is null)
            {
                return Done($"{setting.Project.Name} has no DbContext");
            }
            int pending = Migrations.Count(m => m.Applied == false);
            return Done(Migrations.Count == 0 ? $"{Context} has no migrations"
                : Migrations.All(m => m.Applied is null) ? $"{Context}: {Plural.Of(Migrations.Count, "migration")}; the database could not be read"
                : pending == 0 ? $"{Context}: {Plural.Of(Migrations.Count, "migration")}, all applied"
                : $"{Context}: {Plural.Of(Migrations.Count, "migration")}, {pending} pending");
        }, build: build);

    /// <summary>Adds a migration of the given name to the chosen context, then lists again.</summary>
    public Task<EfDone> AddAsync(string action, string name, CancellationToken cancel = default) =>
        CommandAsync(action, $"Adding migration {name}", asks: true, cancel, async (setting, work) =>
        {
            EfOutcome added = await RunAsync(setting, ["migrations", "add", name, "--json"], work);
            if (!added.Succeeded)
            {
                return Failed(setting, added);
            }
            IReadOnlyList<string> written = Written(added);
            parts.FollowWritten(written);
            string? made = MigrationId(added);
            string id = made ?? Path.GetFileNameWithoutExtension(written.FirstOrDefault() ?? name);
            // Add's answer says the listing but whether the database can be read, which the first migration's leaves to one.
            if (Listed && setting.Context is { } context && context == Context && made is not null && Migrations.Count > 0
                && string.CompareOrdinal(Migrations[^1].Id, id) < 0)
            {
                Migrations = [.. Migrations, new MigrationRow(id, name, Migrations.All(m => m.Applied is null) ? null : false)];
                migrationsRead = null;
                checks.Remove(context);
                await ShowPendingAsync(setting, context, changed: false);
                then = Relist.No;
            }
            else
            {
                Migrations = [.. Migrations, new MigrationRow(id, name, false)];
            }
            return Done($"Added migration {name} to {setting.Context ?? setting.Project.Name}");
        }, Relist.Built | Relist.Listed);

    /// <summary>Removes the chosen context's last migration (once the person agrees), then lists again.</summary>
    public async Task<EfDone> RemoveLastAsync(string action, CancellationToken cancel = default)
    {
        if (Listed && Migrations.LastOrDefault() is { } last && !parts.AsksNoOne() && Mcp.AgentCall.Current is null
            && await parts.Choose("Remove Last Migration", $"Remove {last.Name}? Its files are deleted and the model snapshot goes back to the migration before it.",
                ["Remove"], null) is null)
        {
            return new EfDone(false, "Remove cancelled");
        }
        return await CommandAsync(action, "Removing the last migration", asks: true, cancel, async (setting, work) =>
        {
            EfOutcome removed = await RunAsync(setting, ["migrations", "remove", "--json"], work);
            if (!removed.Succeeded)
            {
                return Failed(setting, removed);
            }
            // What remove deleted says the listing: the last one gone; the model's check waits for the build without it.
            if (Listed && setting.Context == Context && Migrations.Count > 0 && MigrationId(removed) == Migrations[^1].Id)
            {
                Migrations = [.. Migrations.SkipLast(1)];
                migrationsRead = null;
                then = Relist.Built | Relist.Checked;
            }
            return Done($"Removed the last migration of {setting.Context ?? setting.Project.Name}");
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
                return new EfDone(false, "Update cancelled");
            }
        }
        string to = target is null ? "the latest migration" : target == "0" ? "before the first migration" : target;
        return await CommandAsync(action, $"Updating the database to {to}", asks: true, cancel, async (setting, work) =>
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
                then = Relist.Checked;
            }
            return Done($"Updated the database of {setting.Context ?? setting.Project.Name} to {to}");
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
        CommandAsync(action, "Scripting migrations", asks: true, cancel, async (setting, work) =>
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
            return Done($"Scripted the migrations of {setting.Context ?? setting.Project.Name}{(idempotent ? ", idempotent," : string.Empty)} into {PathNames.Canonical(file)}");
        });

    /// <summary>Bundles the chosen context's migrations into a program that applies them, in the startup project's output.</summary>
    public Task<EfDone> BundleAsync(string action, CancellationToken cancel = default) =>
        CommandAsync(action, "Bundling migrations", asks: true, cancel, async (setting, work) =>
        {
            string bundle = Path.Combine(setting.Startup.Folder, "bin", "Migrations", OperatingSystem.IsWindows() ? "efbundle.exe" : "efbundle");
            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            EfOutcome bundled = await RunAsync(setting, ["migrations", "bundle", "--force", "--output", bundle], work);
            return bundled.Succeeded ? Done($"Bundled the migrations of {setting.Context ?? setting.Project.Name} into {PathNames.Shown(bundle, parts.Session().Folder)}")
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
    private async Task<EfDone> CommandAsync(string action, string stage, bool asks, CancellationToken cancel, Func<EfSetting, Working, Task<EfDone>> run,
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
        if (parts.Work.Begin(action, $"{stage}…") is not { } work)
        {
            return Said(new EfDone(false, ActionWork.Busy(parts.Work.Of(action)!)));
        }
        running = work;
        then = relist;
        settled = false;
        using CancellationTokenRegistration stopping = cancel.Register(parts.Work.Stop);
        try
        {
            (EfSetting? setting, string? why) = await PrepareAsync(session, work, asks, build);
            if (setting is null)
            {
                return Said(new EfDone(false, why!));
            }
            EfDone done = await run(setting, work);
            if (done.Done && then != Relist.No && !work.IsStopped && session == parts.Session()
                && (!then.HasFlag(Relist.Built) || await BuildAsync(setting.Project, setting.Startup, setting.Framework, work) is null))
            {
                string? read = await BackgroundWork.Run(() => ReadOf(setting));
                await ReadAsync(setting with { Context = Context }, read, work, contexts: false, list: then.HasFlag(Relist.Listed));
            }
            return Said(done);
        }
        finally
        {
            running = null;
            toolRestore = null;
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

    private EfDone Done(string said)
    {
        Failure = null;
        return new EfDone(true, said);
    }

    private EfDone Failed(EfSetting setting, EfOutcome outcome, bool idempotent = false)
    {
        Failure = EfGaps.After(outcome, setting.Startup.Name, setting.Runtime, idempotent);
        string fix = Failure.Fixes.Count > 0 ? $" ({Failure.Fixes[0].Title} in the Migrations window)" : string.Empty;
        return new EfDone(false, $"{Failure.Said}{fix}");
    }

    /// <summary>What a command runs against, made ready: the choices, the restore (built first when there is none), each gap
    /// asked about and fixed (or refused, saying it), then Passenger's own build of the startup project and the project.</summary>
    private async Task<(EfSetting? Setting, string? Why)> PrepareAsync(ProjectSession session, Working work, bool asks, bool build = true)
    {
        if (session.Folder is null)
        {
            return (null, Refusals.NoFolder);
        }
        if (Project is not { } project || Startup is not { } startup)
        {
            return (null, "No project here uses EF Core");
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
            return (null, $"{startup.Name} does not use EF Core: choose the startup project that runs the context");
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
                return (null, gap.Fixes.Count > 0 ? $"{gap.Said}: {gap.Fixes[0].Title} first" : gap.Said);
            }
            List<string> answers = [.. gap.Fixes.Where(f => f.Kind is not (EfFixKind.ChooseStartup or EfFixKind.ChooseContext)).Select(f => f.Title)];
            if (!gap.Blocks)
            {
                answers.Add("Go On");
            }
            string? answer = await parts.Choose(Title(work.Stage), $"{gap.Said}.{NetworkNote(gap)}", answers, answers.FirstOrDefault());
            if (work.IsStopped || session != parts.Session())
            {
                return (null, "Stopped");
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
            return (null, "Stopped");
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
            return (null, $"dotnet ef is not installed here: {EfGaps.PinFix(runtime).Title} first");
        }
        // A compose run's or an AppHost's database, as this machine reaches it, in place of what the configuration gives.
        EfRunDatabase? database = session.ContainerRun is { IsStopped: false } containers ? await EfDatabases.OfComposeAsync(containers, startup, work.Stopping) : null;
        database ??= EfDatabases.OfAspire(session.Aspire, startup);
        if (database is not null)
        {
            environment = new Dictionary<string, string>(environment.Concat(database.Environment).GroupBy(e => e.Key).Select(g => g.Last()));
        }
        Database = database?.Where;
        IReadOnlyList<string>? outputs = await OutputsAsync(project, startup, framework, parts.Session().Projects, work.Stopping);
        return (new EfSetting(project, startup, framework, folder, environment, Context, tool, restore.Runtime ?? runtime) { Database = database, Outputs = outputs }, null);
    }

    /// <summary>The folders a build puts the startup project's assembly in, and the project's where the startup project does
    /// not reference it (else its copy is in the startup project's), as MSBuild evaluates them with what a build of each
    /// is given; null when either cannot be said.</summary>
    private static async Task<IReadOnlyList<string>?> OutputsAsync(ProjectModel project, ProjectModel startup, string? framework, IReadOnlyList<ProjectModel> all,
        CancellationToken cancel)
    {
        List<string> folders = [];
        foreach (ProjectModel each in project == startup || References(startup, project, all, []) ? [startup] : new[] { startup, project })
        {
            Dictionary<string, string> globals = global::Passenger.App.Workspace.ProjectModels.Properties(null, each);
            if (framework is not null)
            {
                globals["TargetFramework"] = framework;
            }
            try
            {
                ProjectHost host = await ProjectHosts.ForAsync(each.Folder, cancel);
                Host.PropertiesAnswer answer = await host.PropertiesAsync(each.Path, globals, [Host.CarriedProperties.TargetPath], cancel);
                if (answer.Values.GetValueOrDefault(Host.CarriedProperties.TargetPath) is not { Length: > 0 } target)
                {
                    return null;
                }
                folders.Add(Path.GetDirectoryName(PathNames.Canonical(target))!);
            }
            catch (ProjectHostFailed)
            {
                return null;
            }
        }
        return folders;
    }

    /// <summary>What dotnet ef reads of a setting's build, which none of its reads answers otherwise while it stands: the
    /// projects, framework, working folder and environment, and the assemblies, symbols and settings in the output folders
    /// and the working folder (<see cref="BuiltInputs.Stamp"/>); null when the outputs are not known. On any thread.</summary>
    private static string? ReadOf(EfSetting setting) => setting.Outputs is not { } outputs ? null
        : string.Join('\n', (string[])[setting.Project.Path, setting.Startup.Path, setting.Framework ?? string.Empty, setting.Folder,
            .. setting.Environment.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"),
            .. outputs.Select(BuiltInputs.Stamp), BuiltInputs.Stamp(setting.Folder)]);

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

    /// <summary>The NuGet window's Install or Update of one of EF Core's packages, weighed before it goes: one that would
    /// leave a provider of the project behind (its range, the feed's metadata, holding not the new version) says so and
    /// offers the set's update together (EF Core's packages to it, the provider to the newest version that supports it),
    /// or the update as asked. Whether the window's own change goes on.</summary>
    public async Task<bool> WeighUpdateAsync(ProjectModel project, string id, string version)
    {
        UiThread.Check();
        if (!EfProviders.InSet(id) || EfRestore.Of(project.Path) is not { Runtime: not null } restore
            || restore.Providers.FirstOrDefault(p => !EfProviders.InSet(p.Id) && !EfRestore.InRange(p.Range, version)) is not { } left)
        {
            return true;
        }
        string? up = null;
        try
        {
            IPackageFeed feed = await parts.Feed();
            foreach (string candidate in await feed.VersionsAsync(left.Id, VersionOrder.IsPrerelease(version), CancellationToken.None))
            {
                if (await EfProviders.RangeAsync(feed, left.Id, candidate, restore.Framework, CancellationToken.None) is { } range && EfRestore.InRange(range, version))
                {
                    up = candidate;
                    break;
                }
            }
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            // The feeds unread: the update is asked about as one no provider supports yet.
        }
        string together = $"Update the Set to {version}";
        string said = $"{left.Id} {left.Version} supports {EfRestore.Supports(left.Range)}; {id} {version} leaves it behind. "
            + (up is null ? $"No version of {left.Id} in the feeds supports EF Core {version} yet."
                : $"{left.Id} {up} supports it: {together} moves EF Core's packages to {version} and {left.Id} to {up} together.");
        string? answer = await parts.Choose($"Update {id}", said, up is null ? ["Update Anyway"] : [together, "Update Anyway"], up is null ? null : together);
        if (answer != together)
        {
            return answer is not null;
        }
        using CommandHistory.Recording recording = parts.Editors.Commands.Record(together);
        WatchPackageFiles(recording, project);
        List<(string, string)> moved = [.. project.Packages.Where(p => EfProviders.InSet(p.Name)).Select(p => (p.Name, version))];
        if (!moved.Any(m => m.Item1.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            moved.Add((id, version));
        }
        moved.Add((left.Id, up!));
        PackageOutcome outcome = await parts.InstallSet(project, moved);
        parts.Say(outcome.Done ? $"{together}: {left.Id} {up} with it" : outcome.Refused ?? $"{together} failed: the NuGet window's log says why");
        return false;
    }

    /// <summary>Puts a gap right as its fix says, from the window's button or a command's question: a file change is one
    /// step of global undo. Null once fixed, else why not.</summary>
    public async Task<string?> FixAsync(EfFix fix)
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
            return Said(new EfDone(false, "Build the startup project first: its restore says which EF Core it has")).Said;
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
                return "Fix cancelled";
            }
            string? why = await FixAsync(fix, session, startup, runtime, work);
            parts.Say(why ?? $"{fix.Title}: done");
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

    private async Task<string?> FixAsync(EfFix fix, ProjectSession session, ProjectModel startup, string runtime, Working work)
    {
        work.Say($"{fix.Title}…");
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
                return AddFactory(session);
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
    /// makes at the folder's root: one step of global undo, which takes the manifest back (or away).</summary>
    private async Task<string?> PinToolAsync(ProjectSession session, ProjectModel startup, string runtime, Working work)
    {
        string root = session.Folder!;
        string? manifest = EfTools.NearestManifest(startup.Folder) is { } near && PathNames.IsUnder(near, root) ? near : null;
        EfTool? local = manifest is null ? null : EfTools.Local(startup.Folder);
        using CommandHistory.Recording recording = parts.Editors.Commands.Record($"Pin {EfTools.Id} to {runtime}");
        if (manifest is null)
        {
            manifest = Path.Combine(root, "dotnet-tools.json");
            recording.Watch(manifest);
            FileText.Save(manifest, EfTools.NewManifest, new UTF8Encoding(false), false);
        }
        else
        {
            recording.Watch(manifest);
        }
        IReadOnlyList<string> arguments = local is null
            ? ["tool", "install", EfTools.Id, "--version", runtime, "--tool-manifest", manifest]
            : ["tool", "update", EfTools.Id, "--version", runtime, "--allow-downgrade", "--tool-manifest", manifest];
        string? failed = await ToolCommandAsync(arguments, Path.GetDirectoryName(manifest)!, work);
        parts.FollowWritten([manifest]);
        Touched = [manifest];
        return failed;
    }

    /// <summary>A <c>dotnet tool</c> command, its output in the Build window: null once it succeeded, else its last words.</summary>
    private async Task<string?> ToolCommandAsync(IReadOnlyList<string> arguments, string folder, Working work)
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
            : outcome.Lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"dotnet {arguments[0]} {arguments[1]} exited with code {outcome.ExitCode}";
    }

    /// <summary>A package added to a project through the NuGet window's one way, as one step of global undo.</summary>
    private async Task<string?> AddPackageAsync(ProjectModel project, string id, string version)
    {
        using CommandHistory.Recording recording = parts.Editors.Commands.Record($"Add {id} {version} to {project.Name}");
        WatchPackageFiles(recording, project);
        PackageOutcome outcome = await parts.Install(project, id, version);
        Touched = outcome.Changed;
        return outcome.Done ? null : outcome.Refused ?? outcome.Said.LastOrDefault(l => l.Trim().Length > 0) ?? $"{id} could not be added to {project.Name}";
    }

    /// <summary>A provider behind the startup project's EF Core brought into step, as the feeds' metadata says fits
    /// (<see cref="EfProviders.AlignAsync"/>): every project's packages moved as one step of global undo.</summary>
    private async Task<string?> AlignAsync(ProjectModel startup, string runtime, Working work)
    {
        if (EfRestore.Of(startup.Path) is not { Behind: { } behind } restore)
        {
            return null;
        }
        work.Say($"Reading which versions of {behind.Id} and EF Core fit…");
        IReadOnlyList<ProjectModel> projects = [.. new[] { startup, Project ?? startup }.Distinct()];
        EfAlignment? alignment;
        try
        {
            alignment = await EfProviders.AlignAsync(await parts.Feed(), projects, behind, runtime, restore.Framework, work.Stopping);
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed) && !work.IsStopped)
        {
            return $"The feeds could not say which versions fit: {failed.Message}";
        }
        if (alignment is null)
        {
            return $"No version of {behind.Id} in the feeds supports EF Core {runtime}, and none of EF Core it supports is there";
        }
        work.Say($"{alignment.Title}…");
        using CommandHistory.Recording recording = parts.Editors.Commands.Record(alignment.Title);
        foreach (EfMove move in alignment.Moves)
        {
            WatchPackageFiles(recording, move.Project);
            PackageOutcome outcome = await parts.InstallSet(move.Project, move.Packages);
            Touched = [.. Touched, .. outcome.Changed];
            if (!outcome.Done)
            {
                return outcome.Refused ?? outcome.Said.LastOrDefault(l => l.Trim().Length > 0) ?? $"{alignment.Title} failed in {move.Project.Name}";
            }
        }
        return null;
    }

    /// <summary>The files a package change writes, watched by a command of global undo: the project, its lock file and the
    /// nearest Directory.Packages.props.</summary>
    private static void WatchPackageFiles(CommandHistory.Recording recording, ProjectModel project)
    {
        recording.Watch(project.Path);
        recording.Watch(Path.Combine(project.Folder, "packages.lock.json"));
        for (string? at = project.Folder; at is not null; at = Path.GetDirectoryName(at))
        {
            string props = Path.Combine(at, "Directory.Packages.props");
            if (File.Exists(props))
            {
                recording.Watch(PathNames.Canonical(props));
                break;
            }
        }
    }

    /// <summary>A design-time factory for the context, beside the project's file, from a template: the provider's own
    /// <c>Use…</c> with the connection string the startup project's appsettings.json names first.</summary>
    private string? AddFactory(ProjectSession session)
    {
        if (Project is not { } project || Startup is not { } startup)
        {
            return "No project here uses EF Core";
        }
        string? named = (Failure?.Fixes.FirstOrDefault(f => f.Kind == EfFixKind.AddFactory)?.Title)?.Split(' ').Last() ?? Context;
        EfContext? context = Contexts.FirstOrDefault(c => c.Name == named || c.FullName == named) ?? (named is null ? null : new EfContext(named, named));
        if (context is null)
        {
            return "List the project's contexts first";
        }
        EfRestore? restore = EfRestore.Of(project.Path);
        string file = Path.Combine(project.Folder, $"{context.Name}Factory.cs");
        if (File.Exists(file))
        {
            return $"{Path.GetFileName(file)} is already there";
        }
        using CommandHistory.Recording recording = parts.Editors.Commands.Record($"Add a Design-Time Factory for {context.Name}");
        recording.Watch(file);
        FileText.Save(file, EfTemplates.Factory(context, restore, EfTemplates.ConnectionOf(startup.Folder)), new UTF8Encoding(false), false);
        Touched = [file];
        parts.FollowWritten([file]);
        parts.OpenFile(PathNames.Canonical(file));
        return null;
    }

    /// <summary>Builds the project and its startup project with Passenger's own build, the Build window and Problems
    /// showing it as any build's: null once built, else why not.</summary>
    private async Task<string?> BuildAsync(ProjectModel project, ProjectModel startup, string? framework, Working work)
    {
        settled = false;
        work.Say($"Building {startup.Name}…");
        BuildRunController builds = parts.Builds();
        if (builds.IsBuilding)
        {
            return "Another build is running";
        }
        bool covers = project == startup || startup.ProjectReferences.Count > 0 && References(startup, project, parts.Session().Projects, []);
        if (!covers)
        {
            BuildOutcome first = await builds.BuildForAsync(new BuildStep(project.Path, null), work.Stopping);
            if (!first.Succeeded)
            {
                return first.Message;
            }
        }
        BuildOutcome built = await builds.BuildForAsync(new BuildStep(startup.Path, framework), work.Stopping);
        return built.Succeeded ? null : built.Message;
    }

    /// <summary>What the run of the startup project would have: the framework, working folder and environment of the run
    /// configuration that runs it (the chosen one first), so dotnet ef reaches the database the run uses; the project's
    /// folder and the process's own environment without one.</summary>
    private async Task<(string? Framework, string Folder, IReadOnlyDictionary<string, string> Environment)> RunOfAsync(ProjectModel startup, Working work)
    {
        ProjectSession session = parts.Session();
        string? framework = startup.TargetFrameworks.Count > 1 ? startup.TargetFrameworks[0] : null;
        RunConfigurations configurations = session.Configurations;
        RunConfiguration? runs = configurations.Chosen is { Kind: RunKind.Project } chosen && PathNames.Same(chosen.Project, startup.Path) ? chosen
            : configurations.All.FirstOrDefault(c => c.Kind == RunKind.Project && c.Program.Length == 0 && PathNames.Same(c.Project, startup.Path));
        if (runs is null)
        {
            return (framework, startup.Folder, new Dictionary<string, string>());
        }
        work.Say($"Reading how {runs.Name} runs…");
        (LaunchPlan? plan, _) = await parts.Builds().ResolvePlanAsync(runs, null, work.Stopping);
        return plan is null ? (framework, startup.Folder, runs.Environment)
            : (plan.Framework ?? framework, Directory.Exists(plan.WorkingDirectory) ? plan.WorkingDirectory : startup.Folder, plan.Environment);
    }

    /// <summary>Runs dotnet ef with the setting's projects, framework and context, never building, its lines in the Build
    /// window as they come; EF's warning that the tool is older than the runtime kept as a gap.</summary>
    private async Task<EfOutcome> RunAsync(EfSetting setting, IReadOnlyList<string> verb, Working work, bool retried = false)
    {
        List<string> arguments = [.. verb, "--project", setting.Project.Path, "--startup-project", setting.Startup.Path, "--no-build", "--prefix-output"];
        if (setting.Framework is { } framework)
        {
            arguments.AddRange(["--framework", framework]);
        }
        if (setting.Context is { } context)
        {
            arguments.AddRange(["--context", context]);
        }
        // A run's database goes only in the environment: any process can read a command line.
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            start = setting.Tool.Command(arguments, setting.Folder);
        }
        catch (FileNotFoundException missing)
        {
            return EfOutcome.Unstarted(missing.Message);
        }
        foreach ((string name, string value) in setting.Environment)
        {
            start.Environment[name] = value;
        }
        work.Say($"{Title(work.Stage).TrimEnd('…')}: dotnet ef {string.Join(' ', verb.TakeWhile(v => !v.StartsWith('-')))}…");
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
    /// context's migrations (with <paramref name="list"/>; with no context chosen, the only one's) and its model's check,
    /// side by side once a run has left dotnet ef nothing to copy into the output (<see cref="settled"/>), else one by one. What
    /// the build stands as (<paramref name="read"/>, <see cref="ReadOf"/>) is kept with each answer; <paramref name="fresh"/>
    /// takes nothing from earlier reads. Null once read, else the failure.</summary>
    private async Task<EfDone?> ReadAsync(EfSetting setting, string? read, Working work, bool contexts, bool list = true, bool fresh = false)
    {
        string? remembered = fresh ? null : read;
        bool together = settled;
        string? context = Context;
        Task<EfOutcome>? naming = contexts && (remembered is null || remembered != contextsRead) ? RunAsync(setting with { Context = null }, ["dbcontext", "list", "--json"], work) : null;
        if (naming is not null && !together)
        {
            if (Named(setting, await naming, read) is { } unnamed)
            {
                return unnamed;
            }
            naming = null;
            context = Context;
            together = settled;
        }
        bool none = contexts && naming is null && Context is null;
        Task<EfOutcome>? listing = list && !none ? RunAsync(setting with { Context = context }, ["migrations", "list", "--json"], work) : null;
        // Beside the listing where the context had migrations; its answer is kept while the listing finds some.
        Task<bool>? checking = (together || listing is null) && context is not null && Migrations.Count > 0
            ? PendingAsync(setting with { Context = context }, read, work, fresh) : null;
        await Task.WhenAll(new Task?[] { naming, listing, checking }.OfType<Task>());
        if (naming is not null)
        {
            if (Named(setting, naming.Result, read) is { } unnamed)
            {
                return unnamed;
            }
            // Listed with no context named, dotnet ef took the only one; with another, it is read again for this one.
            if (Context != context && !(context is null && Contexts.Count == 1))
            {
                checking = null;
                listing = list && Context is not null ? RunAsync(setting with { Context = Context }, ["migrations", "list", "--json"], work) : null;
            }
            context = Context;
        }
        if (contexts && Context is null)
        {
            Migrations = [];
            Listed = true;
            migrationsRead = read;
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
        }
        if (Migrations.Count > 0 && context is not null)
        {
            await ShowPendingAsync(setting, context, await (checking ?? PendingAsync(setting with { Context = context }, read, work, fresh)));
        }
        return null;
    }

    /// <summary>The contexts dotnet ef listed taken as the project's, the chosen one kept while it is among them; null once
    /// taken, else the failure.</summary>
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
            Context = Contexts.FirstOrDefault()?.Name;
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
            IReadOnlyList<ProjectModel> all = bound?.Projects ?? [];
            (string? file, Passenger.Text.TextPosition at) = await BackgroundWork.Run(() => Declaration(setting.Project, all, context));
            pending.Add(new EfPending(context, file, at));
        }
        Pending = pending;
        parts.ShowPending([.. Pending.Select(p => new Problem(ProblemSeverity.Warning, $"{p.Context}'s model has changes no migration covers: Add Migration… makes one",
            PendingCode, p.File, p.Position, setting.Project.Name))]);
    }

    /// <summary>Where a context's class is declared in its project, looked for in the project's own C# files; none when no
    /// file declares it.</summary>
    private static (string? File, Passenger.Text.TextPosition At) Declaration(ProjectModel project, IReadOnlyList<ProjectModel> all, string context)
    {
        string name = context.Split('.').Last();
        System.Text.RegularExpressions.Regex declared = new($@"\bclass\s+{System.Text.RegularExpressions.Regex.Escape(name)}\b");
        foreach (string file in FolderWalk.ProjectFiles(project, all, ".cs"))
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
    /// again and the model checked, with no build of its own, so Problems says what the build left pending.</summary>
    public void AfterBuild(BuildOutcome built)
    {
        if (built.Succeeded && running is null && Listed && Gaps.All(g => !g.Blocks) && parts.Session().Folder is not null)
        {
            IdeErrors.Observe(ListAsync(AppActions.MigrationsRefresh, build: false, rereads: false));
        }
    }

    /// <summary>The command's restore of the folder's local tools, which every run that found them not restored waits for.</summary>
    private Task<string?>? toolRestore;

    /// <summary>Whether dotnet said the manifest's tools are not restored, before the tool ran.</summary>
    private static bool NotRestored(EfOutcome outcome) => outcome.Lines.Any(l => EfRun.Split(l).Kind.Length == 0 && l.Contains("dotnet tool restore", StringComparison.Ordinal));

    private static IReadOnlyList<EfContext> ReadContexts(string data)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(data.Length == 0 ? "[]" : data);
            return [.. read.RootElement.EnumerateArray().Select(c => new EfContext(
                c.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                c.TryGetProperty("fullName", out JsonElement full) ? full.GetString() ?? string.Empty : string.Empty))
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
                m.GetProperty("id").GetString() ?? string.Empty,
                m.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                m.TryGetProperty("applied", out JsonElement applied) && applied.ValueKind is JsonValueKind.True or JsonValueKind.False ? applied.GetBoolean() : null))];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }
}
