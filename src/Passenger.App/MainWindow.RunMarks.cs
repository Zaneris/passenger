using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.App.Aspire;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Something a run configuration runs from a line of an open file: a Compose configuration's service on its line
/// of a compose file the configuration reads, or a Dockerfile configuration's image on its Dockerfile's final FROM; or,
/// <paramref name="Whole"/>, the configuration itself, as Run launches it (a program's from its entry point, a launch
/// profile's from its profile, a Compose configuration's from its <c>services:</c>).</summary>
public sealed record RunTarget(int Line, RunConfiguration Configuration, string? Service, AspireRunResource? Declaration = null, bool Whole = false);

/// <summary>The gutter's run icons beside what the folder's run configurations run: a program's entry point (its Main or
/// top-level statements; a file-based program's first line), each profile of a launch profiles' file, each service of a
/// compose file a Compose configuration reads and its <c>services:</c>, the final FROM of a Dockerfile a Dockerfile
/// configuration builds, only in files a configuration uses, and each project resource an open AppHost file declares. A
/// click opens the menu that runs or debugs it. A C# file's entry point is read from each parse of it, off the UI thread;
/// the other files' are read from the text as typed, once typing pauses, and again as the configurations change; an
/// AppHost's are asked of the language server for that AppHost alone, as one of its open files opens or its text pauses,
/// and stand while they were read from the text the file has (by its hash), so no other file's key costs them anything.</summary>
public partial class MainWindow
{
    private Debounce? runMarksLater;
    private CancellationTokenSource? readingAspireMarks;

    /// <summary>The project resources each AppHost declares, as last read, by the AppHost a configuration runs.</summary>
    private readonly Dictionary<string, IReadOnlyList<AspireRunResource>> aspireRunMarks = new(Core.PathNames.Comparer);

    /// <summary>The AppHosts whose resources are read once typing pauses: one whose open file's text changed or opened.</summary>
    private readonly HashSet<string> aspireMarksToRead = new(Core.PathNames.Comparer);

    /// <summary>Each open AppHost file's text hash, worked out once a version, which its resources are matched by.</summary>
    private readonly Dictionary<OpenDocument, (int Version, string Hash)> aspireHashes = [];

    /// <summary>The language server the AppHosts' resources were read from: one started again reads them again.</summary>
    private CSharp.CSharpService? aspireMarksFrom;

    /// <summary>The line each open C# file declares its program's entry point on, as its last parse read it.</summary>
    private readonly Dictionary<OpenDocument, int> entryLines = [];

    private void AddRunMarks()
    {
        runMarksLater = new Debounce(TimeSpan.FromMilliseconds(300), () =>
        {
            IdeErrors.Observe(ReadRunMarksAsync());
            return Task.CompletedTask;
        });
        Closed += (_, _) =>
        {
            runMarksLater.Stop();
            readingAspireMarks?.Cancel();
        };
        Editors.Opened += (_, document) =>
        {
            document.Text.Changed += (_, _) =>
            {
                if (AspireAppHostOf(document) is { } appHost)
                {
                    aspireMarksToRead.Add(appHost);
                    runMarksLater.Restart();
                }
                else if (document.RunMarks.Count > 0 || IsRunFile(document.Path))
                {
                    runMarksLater.Restart();
                }
            };
            ShowRunMarks(document);
            if (document.Syntax is { } syntax)
            {
                document.Parsed += (_, parsed) => IdeErrors.Observe(ReadEntryAsync(document, parsed));
                if (syntax.Newest is { } now)
                {
                    IdeErrors.Observe(ReadEntryAsync(document, now));
                }
            }
            if (AspireAppHostOf(document) is { } opened)
            {
                aspireMarksToRead.Add(opened);
                runMarksLater.Restart();
            }
            else if (IsRunFile(document.Path))
            {
                runMarksLater.Restart();
            }
        };
        Editors.Closed += (_, document) =>
        {
            aspireHashes.Remove(document);
            entryLines.Remove(document);
        };
    }

    /// <summary>Whether a file is a compose file, a Dockerfile or a launch profiles' file, where a run icon may stand.</summary>
    private static bool IsRunFile(string path) =>
        Web.ComposeDeclarations.IsCompose(path) || Languages.Language.ForPath(path) == Languages.Language.Dockerfile || IsProfilesFile(path);

    /// <summary>Whether a file may hold launch profiles: a project's launchSettings.json, a file-based program's .run.json.</summary>
    private static bool IsProfilesFile(string path) =>
        Path.GetFileName(path).Equals(LaunchProfile.SettingsFile, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".run.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A C# file's entry point read from a parse of it off the UI thread, its icon shown again where its line moved;
    /// passed over when the text has moved on since, a later parse landing for it.</summary>
    private async Task ReadEntryAsync(OpenDocument document, ParsedText parsed)
    {
        int? line = await BackgroundWork.Run(() => EntryLine(parsed));
        if (!ReferenceEquals(document.Text.Snapshot, parsed.Text) || !Editors.Documents.Contains(document))
        {
            return;
        }
        int? was = entryLines.TryGetValue(document, out int known) ? known : null;
        if (line == was)
        {
            return;
        }
        if (line is int at)
        {
            entryLines[document] = at;
        }
        else
        {
            entryLines.Remove(document);
        }
        ShowRunMarks(document);
    }

    /// <summary>The line of a C# file's entry point: its first top-level statement, else a static Main's name; null for
    /// neither.</summary>
    private static int? EntryLine(ParsedText parsed)
    {
        if (parsed.Root is not CompilationUnitSyntax unit)
        {
            return null;
        }
        if (unit.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } first)
        {
            return parsed.Text.GetLineFromOffset(first.SpanStart);
        }
        return Main(unit.Members) is { } main ? parsed.Text.GetLineFromOffset(main.Identifier.SpanStart) : null;

        static MethodDeclarationSyntax? Main(SyntaxList<MemberDeclarationSyntax> members)
        {
            foreach (MemberDeclarationSyntax member in members)
            {
                MethodDeclarationSyntax? found = member switch
                {
                    MethodDeclarationSyntax { Identifier.ValueText: "Main" } method when method.Modifiers.Any(SyntaxKind.StaticKeyword) => method,
                    BaseNamespaceDeclarationSyntax space => Main(space.Members),
                    TypeDeclarationSyntax type => Main(type.Members),
                    _ => null,
                };
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }
    }

    /// <summary>The AppHost an open C# file is a source of, as a configuration names it; null for any other file, whose keys
    /// read no AppHost's resources.</summary>
    private string? AspireAppHostOf(OpenDocument document) =>
        document.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? session.AspireFiles.AppHostOf(document.Path) : null;

    /// <summary>The run configurations or the language server changed: the AppHosts of the open files whose resources were
    /// never read (or, with <paramref name="again"/>, every one, the server being another) are read once typing pauses, and
    /// the marks of the open files that can have one shown again; with none open, no pause is waited out for nothing.</summary>
    private void ReadOpenAspireMarks(bool again)
    {
        if (again)
        {
            aspireRunMarks.Clear();
        }
        bool marked = false;
        foreach (OpenDocument document in Editors.Documents)
        {
            string? appHost = AspireAppHostOf(document);
            if (appHost is not null && !aspireRunMarks.ContainsKey(appHost))
            {
                aspireMarksToRead.Add(appHost);
            }
            marked |= appHost is not null || document.RunMarks.Count > 0 || IsRunFile(document.Path) || document.Syntax is not null;
        }
        if (marked || aspireMarksToRead.Count > 0)
        {
            runMarksLater?.Restart();
        }
    }

    /// <summary>The marks as the open files' texts stand, then each AppHost waiting to be read, one at a time, by one read
    /// that takes up those added meanwhile.</summary>
    private async Task ReadRunMarksAsync()
    {
        ShowRunMarks();
        ProjectSession current = session;
        if (readingAspireMarks is not null || aspireMarksToRead.Count == 0 || current.IsEnded || current.CSharp is not { IsReady: true })
        {
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        using CancellationTokenSource cancel = new();
        readingAspireMarks = cancel;
        try
        {
            while (aspireMarksToRead.FirstOrDefault() is { } appHost && session == current && !current.IsEnded && !cancel.IsCancellationRequested)
            {
                aspireMarksToRead.Remove(appHost);
                RunConfiguration? configuration = Configurations.FirstOrDefault(c => Core.PathNames.Same(c.Project, appHost)
                    && current.Aspire.IsAspire(c));
                IReadOnlyList<AspireRunResource>? resources = null;
                if (configuration is not null)
                {
                    try
                    {
                        resources = await ReadAspireConfigurationResourcesAsync(configuration, cancel.Token);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or Lsp.LanguageServerFailed)
                    {
                    }
                }
                if (session != current || cancel.IsCancellationRequested)
                {
                    return;
                }
                if (resources is null)
                {
                    aspireRunMarks.Remove(appHost);
                }
                else
                {
                    aspireRunMarks[appHost] = resources;
                }
                ShowRunMarks();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            readingAspireMarks = null;
            // What another folder's files are waiting for, the read that held them out having stopped with its folder.
            if (session != current && aspireMarksToRead.Count > 0)
            {
                runMarksLater?.Restart();
            }
        }
    }

    /// <summary>A new session's marks: none read yet, nor any waiting.</summary>
    private void ForgetRunMarks()
    {
        readingAspireMarks?.Cancel();
        aspireRunMarks.Clear();
        aspireMarksToRead.Clear();
        aspireHashes.Clear();
        aspireMarksFrom = null;
    }

    private void ShowRunMarks()
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            ShowRunMarks(document);
        }
    }

    private void ShowRunMarks(OpenDocument document)
    {
        RunMark[] marks = [.. RunTargets(document).Select(t => t.Line).Distinct().Select(l => new RunMark(l))];
        if (!marks.SequenceEqual(document.RunMarks))
        {
            document.RunMarks = marks;
        }
    }

    /// <summary>What the folder's configurations run from an open file's lines.</summary>
    public IReadOnlyList<RunTarget> RunTargets(OpenDocument document)
    {
        string path = document.Path;
        List<RunTarget>? entry = EntryTargets(document);
        string? appHost = AspireAppHostOf(document);
        if (appHost is null && !IsRunFile(path))
        {
            return entry ?? (IReadOnlyList<RunTarget>)[];
        }
        TextSnapshot text = document.Text.Snapshot;
        List<RunTarget> targets = entry ?? [];
        if (IsProfilesFile(path))
        {
            targets.AddRange(ProfileTargets(path, text));
        }
        foreach (RunConfiguration configuration in Configurations)
        {
            if (appHost is not null)
            {
                if (Core.PathNames.Same(configuration.Project, appHost) && session.Aspire.IsAspire(configuration)
                    && aspireRunMarks.TryGetValue(appHost, out IReadOnlyList<AspireRunResource>? resources)
                    && resources.Any(resource => Core.PathNames.Same(resource.Source, path)))
                {
                    string hash = HashOf(document);
                    targets.AddRange(resources.Where(resource => Core.PathNames.Same(resource.Source, path) && resource.SourceHash == hash)
                        .Select(resource => new RunTarget(resource.At.Line, configuration, resource.Name, resource)));
                }
            }
            else if (configuration.Kind == RunKind.Compose && configuration.ComposeFiles.Any(f => Core.PathNames.Same(f, path)))
            {
                if (ServicesLine(text) is int services)
                {
                    targets.Add(new RunTarget(services, configuration, null, Whole: true));
                }
                targets.AddRange(ServiceLines(text).Select(s => new RunTarget(s.Line, configuration, s.Service)));
            }
            else if (configuration.Kind == RunKind.Dockerfile && DockerfileOf(configuration) is { } dockerfile && Core.PathNames.Same(dockerfile, path)
                && FinalFrom(text) is int from)
            {
                targets.Add(new RunTarget(from, configuration, null));
            }
        }
        return targets;
    }

    /// <summary>The configurations of the program whose entry point an open file declares, on its line: a file-based
    /// program's own file on its first, a project's (not an AppHost's, whose icons are its resources') where its last parse
    /// found Main or the top-level statements; null for none.</summary>
    private List<RunTarget>? EntryTargets(OpenDocument document)
    {
        if (document.Syntax is null || session.ProjectOf(document.Path) is not { IsRunnable: true, IsAspireHost: false } program)
        {
            return null;
        }
        int line;
        if (program.IsProgram)
        {
            if (!Core.PathNames.Same(program.Path, document.Path))
            {
                return null;
            }
            line = 0;
        }
        else if (!entryLines.TryGetValue(document, out line))
        {
            return null;
        }
        List<RunTarget> targets = [.. Configurations.Where(c => Core.PathNames.Same(c.Project, program.Path)).Select(c => new RunTarget(line, c, null, Whole: true))];
        return targets.Count > 0 ? targets : null;
    }

    /// <summary>Each launch profile of a profiles' file a runnable project reads, on its name's line, with the configurations
    /// made from it.</summary>
    private IEnumerable<RunTarget> ProfileTargets(string path, TextSnapshot text)
    {
        ProjectModel[] owners = [.. Projects.Where(p => p.Exists && p.IsRunnable && Core.PathNames.Same(LaunchProfile.SettingsPath(p), path))];
        if (owners.Length == 0)
        {
            yield break;
        }
        foreach ((int line, string name) in ProfileLines(text))
        {
            foreach (ProjectModel owner in owners)
            {
                foreach (RunConfiguration configuration in Configurations.Where(c => Core.PathNames.Same(c.Project, owner.Path)
                    && (c.LaunchProfile == name || c.Generated && c.Name == RunConfiguration.NameOf(owner, name))))
                {
                    yield return new RunTarget(line, configuration, null, Whole: true);
                }
            }
        }
    }

    /// <summary>Each profile's name and its line, as the <c>profiles</c> object of a launch profiles' file names them, up to
    /// where its text stops being JSON.</summary>
    private static List<(int Line, string Name)> ProfileLines(TextSnapshot text)
    {
        List<(int, string)> found = [];
        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        bool inProfiles = false;
        int counted = 0;
        int characters = 0;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }
                if (reader.CurrentDepth == 1)
                {
                    inProfiles = reader.ValueTextEquals("profiles");
                }
                else if (reader.CurrentDepth == 2 && inProfiles)
                {
                    int at = (int)reader.TokenStartIndex;
                    characters += Encoding.UTF8.GetCharCount(bytes, counted, at - counted);
                    counted = at;
                    found.Add((text.GetLineFromOffset(Math.Min(characters, text.Length)), reader.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            // Half typed: the profiles before where it stops being JSON stand.
        }
        return found;
    }

    /// <summary>An open AppHost file's text hash, as the language server's reading of its resources names it.</summary>
    private string HashOf(OpenDocument document)
    {
        int version = document.Text.Version;
        if (aspireHashes.TryGetValue(document, out (int Version, string Hash) known) && known.Version == version)
        {
            return known.Hash;
        }
        string hash = TextHash.Of(document.Text.Snapshot.ToString());
        aspireHashes[document] = (version, hash);
        return hash;
    }

    /// <summary>The Dockerfile a Dockerfile configuration builds: its own, else its project's beside it.</summary>
    private string? DockerfileOf(RunConfiguration configuration) => configuration.Dockerfile.Length > 0 ? configuration.Dockerfile
        : Projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, configuration.Project)) is { } project
            ? Containers.ContainerConfigurations.Dockerfile(project)
            : null;

    /// <summary>Each service's line of a compose file: the keys one level under the top-level <c>services:</c>.</summary>
    private static IEnumerable<(int Line, string Service)> ServiceLines(TextSnapshot text)
    {
        int indent = -1;
        bool inServices = false;
        for (int line = 0; line < text.LineCount; line++)
        {
            string row = text.GetLineText(line);
            if (row.Trim().Length == 0 || row.TrimStart().StartsWith('#'))
            {
                continue;
            }
            int at = row.Length - row.TrimStart().Length;
            if (at == 0)
            {
                inServices = TopKey().Match(row) is { Success: true } top && top.Groups["key"].Value == "services";
                indent = -1;
                continue;
            }
            if (!inServices)
            {
                continue;
            }
            indent = indent < 0 ? at : indent;
            if (at == indent && ServiceKey().Match(row) is { Success: true } key)
            {
                yield return (line, key.Groups["key"].Value);
            }
        }
    }

    /// <summary>A compose file's top-level <c>services:</c> line.</summary>
    private static int? ServicesLine(TextSnapshot text)
    {
        for (int line = 0; line < text.LineCount; line++)
        {
            if (TopKey().Match(text.GetLineText(line)) is { Success: true } top && top.Groups["key"].Value == "services")
            {
                return line;
            }
        }
        return null;
    }

    /// <summary>A Dockerfile's final FROM, the image it makes.</summary>
    private static int? FinalFrom(TextSnapshot text)
    {
        int? last = null;
        for (int line = 0; line < text.LineCount; line++)
        {
            if (From().IsMatch(text.GetLineText(line)))
            {
                last = line;
            }
        }
        return last;
    }

    /// <summary>The run icon's menu: Run of each configuration that runs the line (one service of a Compose
    /// configuration, a Dockerfile's, one project an AppHost declares, or the configuration whole as the program at hand),
    /// Debug and Profile of it; for a .NET service of a
    /// run under way that reads the line (the container run's, an AppHost's that runs), the service actions on that run
    /// instead, as the Containers and Aspire windows' rows give them (greyed with why), which leave the run up.</summary>
    private void ShowRunMarkMenu(CodeEditor editor, OpenDocument document, int line)
    {
        RunTarget[] here = [.. RunTargets(document).Where(t => t.Line == line)];
        if (here.Length == 0)
        {
            return;
        }
        MenuFlyout menu = new();
        HashSet<string> joined = new(StringComparer.Ordinal);
        foreach (RunTarget target in here)
        {
            if (target.Whole)
            {
                ActionContext launching = new(Editors.Active, Editors.CodeEditor, FocusManager?.GetFocusedElement(), null,
                    session.Debug.IsDebugging ? session.Debug.Session?.State : null) { Configuration = target.Configuration };
                foreach (string id in (ReadOnlySpan<string>)[AppActions.RunHere, AppActions.DebugHere, AppActions.ProfileHere])
                {
                    menu.Items.Add(FlyoutEntry(new MenuEntry(id), launching));
                }
                continue;
            }
            string what = target.Service ?? target.Configuration.Name;
            if (target.Service is { } service && RunOf(document.Path, target) is { } running)
            {
                if (joined.Add($"{running.Name}\n{service}"))
                {
                    ActionContext context = new(Editors.Active, Editors.CodeEditor, FocusManager?.GetFocusedElement(), null,
                        session.Debug.IsDebugging ? session.Debug.Session?.State : null) { Service = new ChosenService(running, service) };
                    foreach ((string id, _) in ToolWindows.RunWindows.ServiceActions)
                    {
                        menu.Items.Add(FlyoutEntry(new MenuEntry(id), context));
                    }
                }
                continue;
            }
            string from = here.Length > 1 && target.Service is not null ? $" ({target.Configuration.Name})" : string.Empty;
            MenuItem run = new() { Header = $"Run '{what}'{from}" };
            run.Click += (_, _) => IdeErrors.Observe(RunTarget(target));
            MenuItem debug = new() { Header = $"Debug '{what}'{from}" };
            debug.Click += (_, _) => IdeErrors.Observe(RunTarget(target, RunStart.Debug));
            MenuItem profile = new() { Header = $"Profile '{what}'{from}" };
            profile.Click += (_, _) => IdeErrors.Observe(RunTarget(target, RunStart.Profile));
            menu.Items.Add(run);
            menu.Items.Add(debug);
            menu.Items.Add(profile);
        }
        RunMarkMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
    }

    /// <summary>The run icon's menu last opened.</summary>
    public MenuFlyout? RunMarkMenu { get; private set; }

    /// <summary>Runs (or debugs, or profiles) what a run icon stands beside: a Compose configuration's one service (with what
    /// it depends on, the service alone debugged or profiled), or a Dockerfile configuration; what stopped it, if anything,
    /// is in the status bar.</summary>
    public Task<string?> RunTarget(RunTarget target, RunStart how = RunStart.Run)
    {
        if (target.Configuration.Aspire)
        {
            return RunAspireTargetAsync(target, how);
        }
        RunConfiguration configuration = target.Service is { } service
            ? target.Configuration.Copied() with
            {
                Name = $"{target.Configuration.Name}: {service}",
                Services = [service],
                LaunchService = service,
                ServiceActions = new Dictionary<string, ServiceStart>(target.Configuration.ServiceActions) { [service] = ServiceStart.Debug },
            }
            : target.Configuration;
        return how == RunStart.Profile ? ProfileAsync(configuration) : buildRun.RunContainersAsync(configuration, how);
    }

    private async Task<string?> RunAspireTargetAsync(RunTarget target, RunStart how)
    {
        ProjectSession current = session;
        if (target.Service is not { } name)
        {
            return "No Aspire project resource is selected";
        }
        IReadOnlyList<AspireRunResource> declared = await ReadAspireConfigurationResourcesAsync(target.Configuration, CancellationToken.None);
        if (session != current || current.IsEnded || target.Declaration is not { } original
            || !declared.Any(resource => resource == original)
            || !Editors.Documents.Any(document => Core.PathNames.Same(document.Path, original.Source)
                && TextHash.Of(document.Text.Snapshot.ToString()) == original.SourceHash))
        {
            return "The AppHost source changed; open its gutter action again";
        }
        if (current.Aspire.FindApp(target.Configuration.Project) is { IsTest: false, IsStopped: false } app)
        {
            // Up since the menu opened: the service actions on its run, as the menu offers them now.
            string id = how switch { RunStart.Debug => AppActions.ServiceDebug, RunStart.Profile => AppActions.ServiceProfile, _ => AppActions.ServiceRun };
            return await ServiceActionAsync(id, new ServiceAim(app, name, app.ServiceNamed(name) is not null, null, null));
        }
        if (session != current || current.IsEnded || declared.Count(resource => resource.Name == name) != 1)
        {
            return "The selected AppHost resource changed";
        }
        RunConfiguration configuration = Targeting(target.Configuration, declared.Select(resource => (resource.Name, resource.Project)), name,
            debug: how is RunStart.Debug or RunStart.Profile);
        // Under its own action's work and words: Debug's debugging, Profile's profiling, Run's running.
        return how == RunStart.Profile ? await buildRun.ProfileAspireAsync(configuration)
            : await buildRun.RunAspireAsync(configuration, null, how == RunStart.Debug ? RunStart.Debug : RunStart.Run);
    }

    /// <summary>The run under way whose .NET service a run icon stands beside, which the service actions act on: the container
    /// run that reads the compose file, for a .NET service of it; an AppHost's run of the configuration, for a project it
    /// declares, whether that runs now or not (Debug Service starts it); else null.</summary>
    private ServiceRun? RunOf(string path, RunTarget target)
    {
        if (target.Configuration.Aspire)
        {
            return session.Aspire.FindApp(target.Configuration.Project) is { IsTest: false, IsStopped: false } app ? app : null;
        }
        return buildRun.Containers is { } run && target.Service is { } service && run.Plan.Compose.Any(word => Core.PathNames.Same(word, path))
            && buildRun.ServiceOfRun(service).Service is not null ? run : null;
    }

    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_.-]+)\s*:")]
    private static partial Regex TopKey();

    [GeneratedRegex(@"^\s+[""']?(?<key>[A-Za-z0-9_.-]+)[""']?\s*:(?:\s|$)")]
    private static partial Regex ServiceKey();

    [GeneratedRegex(@"^\s*FROM\s", RegexOptions.IgnoreCase)]
    private static partial Regex From();
}
