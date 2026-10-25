using System.Text.Json;
using Passenger.App.Run;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Containers;

/// <summary>What the person's container engine says of the folder's open Dockerfiles and compose files, through its own
/// CLI: BuildKit's build checks on a Dockerfile (<see cref="DockerfileCheck"/>), and on a compose file Compose's own model
/// of it (<see cref="ComposeCheck"/>): its errors, what each <c>${VAR}</c> comes to and the values an override file
/// changes, as inlay hints. Each file's problems are markers and rows of Problems' Containers tab (<see cref="Problems"/>);
/// with no engine running, or none that can check, nothing is shown or said for a file. Kept by
/// <see cref="WebLanguages"/> with the files' servers. A UI-thread object.</summary>
public sealed class EngineChecks : IDisposable
{
    private readonly Func<ContainerEngine> engine;
    private readonly Dictionary<OpenDocument, EngineCheck> checks = [];
    private ContainerEngine? followed;
    private bool disposed;

    /// <param name="buildKit">Whether BuildKit checks Dockerfiles (Settings' Check Dockerfiles with BuildKit).</param>
    public EngineChecks(Func<ContainerEngine> engine, bool buildKit)
    {
        this.engine = engine;
        BuildKit = buildKit;
    }

    /// <summary>Whether BuildKit checks the Dockerfiles, which reads their base images' metadata from their registries.</summary>
    public bool BuildKit { get; private set; }

    /// <summary>Settings' Check Dockerfiles with BuildKit changed: each open Dockerfile is checked now, or its checks taken away.</summary>
    public void UseBuildKit(bool on)
    {
        UiThread.Check();
        if (disposed || on == BuildKit)
        {
            return;
        }
        BuildKit = on;
        foreach (DockerfileCheck check in checks.Values.OfType<DockerfileCheck>())
        {
            check.Run();
        }
    }

    /// <summary>A file's problems changed (<see cref="Problems"/>), on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Every open file's problems as the engine last found them, each once: a compose file's may be its override's.</summary>
    public IReadOnlyList<Problem> Problems => [.. checks.Values.SelectMany(c => c.Problems).Distinct()];

    internal ContainerEngine Engine => engine();

    /// <summary>The folder's code as a compose file's check reads it (its projects, its C# language server once loaded),
    /// asked on the UI thread as a check starts; none without it.</summary>
    public Func<ComposeCode?>? Code { get; set; }

    /// <summary>The folder's code changed (its language server loaded, a project evaluated again, a C# file written): each
    /// open compose file is checked again once things settle, for the connection strings its .NET services read.</summary>
    public void CodeChanged()
    {
        UiThread.Check();
        foreach (ComposeCheck compose in checks.Values.OfType<ComposeCheck>())
        {
            compose.Later();
        }
    }

    /// <summary>Checks an open file the engine has something to say of (a Dockerfile, a compose file), now and as it changes.</summary>
    public void Attach(OpenDocument document, WebDocument web)
    {
        UiThread.Check();
        if (disposed || checks.ContainsKey(document))
        {
            return;
        }
        EngineCheck? check = Languages.Language.ForPath(document.Path) == Languages.Language.Dockerfile ? new DockerfileCheck(this, document, web)
            : ComposeDeclarations.IsCompose(document.Path) ? new ComposeCheck(this, document, web) : null;
        if (check is null)
        {
            return;
        }
        checks[document] = check;
        Follow();
        check.Run();
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (checks.Remove(document, out EngineCheck? check))
        {
            bool had = check.Problems.Count > 0;
            bool gapped = check.Gaps.Count > 0;
            check.Dispose();
            if (had)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            if (gapped)
            {
                GapsTold();
            }
        }
    }

    /// <summary>Files changed on disk: an open compose file is checked again once they settle when a compose file or the
    /// .env beside it changed, since Compose reads them with it, or a C# file, which may read a connection string.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        bool code = paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        foreach (ComposeCheck compose in checks.Values.OfType<ComposeCheck>())
        {
            if (code || paths.Any(p => Path.GetDirectoryName(p) is { } folder && PathNames.Same(folder, compose.Folder)
                && (ComposeDeclarations.IsCompose(p) || Path.GetFileName(p) == ".env")))
            {
                compose.Later();
            }
        }
    }

    /// <summary>A compose file saved: the other compose files open beside it, which Compose reads with it, are checked again.</summary>
    internal void Saved(EngineCheck saved)
    {
        if (saved is not ComposeCheck compose)
        {
            return;
        }
        foreach (ComposeCheck beside in checks.Values.OfType<ComposeCheck>().Where(c => c != compose && PathNames.Same(c.Folder, compose.Folder)))
        {
            beside.Run();
        }
    }

    internal void Told() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The connection strings the open compose files' .NET services read that their environments do not set, as
    /// their checks last found them; the services' code is marked where it reads them (<see cref="CSharp.CSharpDocument"/>).</summary>
    public IReadOnlyList<ComposeGap> Gaps => [.. checks.Values.SelectMany(c => c.Gaps)];

    /// <summary><see cref="Gaps"/> changed, on the UI thread.</summary>
    public event EventHandler? GapsChanged;

    internal void GapsTold() => GapsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised once, on the UI thread, when a Dockerfile is left unchecked for what the engine cannot do
    /// (<see cref="EngineState.Limit"/>), for the window to say.</summary>
    public event EventHandler<EngineState>? Limited;

    private bool limitSaid;

    /// <summary>A Dockerfile's check found the engine unable to run it; from any thread.</summary>
    internal void Limit(EngineState state) => UiThread.Post(() =>
    {
        if (!disposed && !limitSaid)
        {
            limitSaid = true;
            Limited?.Invoke(this, state);
        }
    });

    /// <summary>Passenger's own fixes for what the engine found on the line of an offset of an open file: a compose file's
    /// name a service uses that it does not declare, a connection string a .NET service's environment never sets
    /// (<see cref="ComposeFixes"/>), each worked out on its text as it stands.</summary>
    public IReadOnlyList<CSharp.EditorFix> Fixes(OpenDocument document, int offset)
    {
        UiThread.Check();
        if (!checks.TryGetValue(document, out EngineCheck? check) || check.Faults.Count == 0)
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        int line = text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length));
        return [.. check.Faults
            .Where(f => text.GetLineFromOffset(Math.Min(f.Range.Start, text.Length)) <= line && line <= text.GetLineFromOffset(Math.Min(f.Range.End, text.Length)))
            .SelectMany(f => f.Fixes(text))];
    }

    /// <summary>Follows the engine's state, so a start or stop of it checks every file again (or takes their problems away).</summary>
    private void Follow()
    {
        ContainerEngine now = engine();
        if (followed == now)
        {
            return;
        }
        if (followed is not null)
        {
            followed.StateChanged -= OnState;
        }
        followed = now;
        now.StateChanged += OnState;
    }

    private void OnState(object? sender, EngineState state) => UiThread.Post(() =>
    {
        if (disposed)
        {
            return;
        }
        foreach (EngineCheck check in checks.Values)
        {
            check.Run();
        }
    });

    public void Dispose()
    {
        UiThread.Check();
        disposed = true;
        if (followed is not null)
        {
            followed.StateChanged -= OnState;
            followed = null;
        }
        foreach (EngineCheck check in checks.Values)
        {
            check.Dispose();
        }
        checks.Clear();
    }
}

/// <summary>A problem the engine found: in the file checked, where <see cref="Range"/> says in its text as checked, or in a
/// file read with it (an override), at <see cref="Position"/> only.</summary>
internal sealed record Finding(ProblemSeverity Severity, string Message, string Path, TextPosition Position, TextRange? Range)
{
    /// <summary>Passenger's own fixes for it, worked out on the file's text as it stands when they are asked for: a name
    /// Compose says a service uses that the file does not declare (<see cref="ComposeFixes.For"/>), a connection string a
    /// .NET service reads that its environment never sets (<see cref="ComposeFixes.SetVariable"/>).</summary>
    public Func<TextSnapshot, IReadOnlyList<CSharp.EditorFix>>? Fixes { get; init; }

    /// <summary>A warning of less weight, marked as information is (a connection string the settings give at localhost).</summary>
    public bool Weak { get; init; }

    public Uri? Link { get; init; }

    public string? LinkText { get; init; }

    public Problem Problem => new(Severity, Message, null, Path, Position, null) { Link = Link, LinkText = LinkText };
}

/// <summary>What a check found: problems, and hints for the file checked, in its text as checked.</summary>
internal sealed record Outcome(IReadOnlyList<Finding> Findings, IReadOnlyList<InlayHint> Hints)
{
    public static readonly Outcome None = new([], []);

    /// <summary>A compose file's connection strings its .NET services' environments do not set, for their code's marks.</summary>
    public IReadOnlyList<ComposeGap> Gaps { get; init; } = [];
}

/// <summary>One open file's check by the engine: run as it opens, as it is saved and (<see cref="Later"/>) once typing
/// pauses, one at a time, off the UI thread as background work (a screenshot waits for it) holding the file's
/// <see cref="OpenDocument.Checking"/> (an agent's diagnostics wait for it); what it found is kept for the text it read,
/// its markers and hints carried with the text typed after, and dropped (to be checked again) when the text moved on
/// before it landed.</summary>
internal abstract class EngineCheck : IDisposable
{
    /// <summary>How long typing must pause before a file is checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly WebDocument web;
    private readonly Debounce later;
    private readonly AnchoredList<(TextRange Range, Func<TextSnapshot, IReadOnlyList<CSharp.EditorFix>> Fixes)> faults =
        new(f => f.Range, (f, range) => (range, f.Fixes));
    private CancellationTokenSource? running;
    private bool disposed;

    protected EngineCheck(EngineChecks owner, OpenDocument document, WebDocument web)
    {
        Owner = owner;
        this.web = web;
        Document = document;
        later = new Debounce(Settle, () =>
        {
            Run();
            return Task.CompletedTask;
        }, document.Checking);
        document.Text.Changed += OnChanged;
        document.Saved += OnSaved;
    }

    protected OpenDocument Document { get; }

    protected EngineChecks Owner { get; }

    /// <summary>Whether the check runs at all, as Settings say: when it does not, what it found before is taken away.</summary>
    protected virtual bool Wanted => true;

    /// <summary>The file's folder, where the engine's command runs.</summary>
    public string Folder => Path.GetDirectoryName(Document.Path)!;

    public IReadOnlyList<Problem> Problems { get; private set; } = [];

    /// <summary>The connection strings a compose file's .NET services read that their environments do not set, as last found.</summary>
    public IReadOnlyList<ComposeGap> Gaps { get; private set; } = [];

    /// <summary>What the check found that Passenger's own fixes take, each where it is marked with its fixes, carried with the
    /// text typed after, as the markers are.</summary>
    public IReadOnlyList<(TextRange Range, Func<TextSnapshot, IReadOnlyList<CSharp.EditorFix>> Fixes)> Faults => faults.Items;

    /// <summary>Whether the file is checked again once typing pauses, rather than only as it opens and is saved.</summary>
    protected abstract bool AsTyped { get; }

    /// <summary>Whether the check gives the file's hints, which it then owns.</summary>
    protected abstract bool Hints { get; }

    /// <summary>A check is starting, on the UI thread: what it reads of the window's own it takes now.</summary>
    protected virtual void Starting()
    {
    }

    /// <summary>The check itself, off the UI thread, of the file's text as it stood.</summary>
    protected abstract Task<Outcome> CheckAsync(ContainerEngine engine, string path, TextSnapshot text, string written, CancellationToken cancel);

    /// <summary>Checks the text as it stands now, in place of a check under way.</summary>
    public void Run()
    {
        if (disposed)
        {
            return;
        }
        later.Stop();
        running?.Cancel();
        if (!Wanted)
        {
            running = null;
            Show(Outcome.None);
            return;
        }
        CancellationTokenSource cancel = new();
        running = cancel;
        Starting();
        IdeErrors.Observe(RunAsync(Document.Text.Snapshot, Document.Text.Version, cancel, Document.Checking()));
    }

    /// <summary>Checks the text again once typing (or another program's writes) pauses.</summary>
    public void Later()
    {
        if (!disposed)
        {
            later.Restart();
        }
    }

    private async Task RunAsync(TextSnapshot text, int version, CancellationTokenSource cancel, IDisposable checking)
    {
        try
        {
            ContainerEngine engine = Owner.Engine;
            string path = Document.Path;
            string written = text.ToString();
            Outcome found = await BackgroundWork.Run(() => CheckAsync(engine, path, text, written, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || disposed)
            {
                return;
            }
            if (Document.Text.Version != version)
            {
                // Typed over while it ran: a compose file is checked again as typed, a Dockerfile at its next save.
                return;
            }
            Show(found);
        }
        catch (OperationCanceledException)
        {
            // Another check took its place, or the file was let go.
        }
        catch (Exception failed) when (failed is TimeoutException or IOException or JsonException or FileNotFoundException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            IdeErrors.Write($"The container engine could not check {Document.Path}: {failed.Message}");
            if (!disposed && running == cancel)
            {
                Show(Outcome.None);
            }
        }
        finally
        {
            checking.Dispose();
            if (running == cancel)
            {
                running = null;
            }
            cancel.Dispose();
        }
    }

    private void Show(Outcome found)
    {
        web.ShowEngine([.. found.Findings.Where(f => f.Range is not null).Select(f =>
            new Marker(f.Range!.Value, f.Severity == ProblemSeverity.Error ? MarkerKind.Error : f.Weak ? MarkerKind.Info : MarkerKind.Warning, f.Message)
            {
                Link = f.Link,
                LinkText = f.LinkText,
            })]);
        if (Hints)
        {
            Document.InlayHints = found.Hints;
        }
        faults.Set(Document.Text, [.. found.Findings.Where(f => f is { Range: not null, Fixes: not null }).Select(f => (f.Range!.Value, f.Fixes!))]);
        if (found.Gaps.Count != Gaps.Count || found.Gaps.Zip(Gaps).Any(g => !g.First.Same(g.Second)))
        {
            Gaps = found.Gaps;
            Owner.GapsTold();
        }
        IReadOnlyList<Problem> problems = [.. found.Findings.Select(f => f.Problem)];
        if (!problems.SequenceEqual(Problems))
        {
            Problems = problems;
            Owner.Told();
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        // The hints move with the text itself (OpenDocument.InlayHints), a value's before what is typed at it.
        if (AsTyped)
        {
            Later();
        }
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        Run();
        Owner.Saved(this);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        later.Stop();
        running?.Cancel();
        Document.Text.Changed -= OnChanged;
        Document.Saved -= OnSaved;
        web.ShowEngine([]);
        if (Hints)
        {
            Document.InlayHints = [];
        }
        Problems = [];
        faults.Clear();
        Gaps = [];
    }

    /// <summary>A stretch of a line as a marker spans it: from its first character that is no space to its end.</summary>
    protected static TextRange Lines(TextSnapshot text, int first, int last)
    {
        first = Math.Clamp(first, 0, text.LineCount - 1);
        last = Math.Clamp(last, first, text.LineCount - 1);
        LineSpan start = text.GetLine(first);
        LineSpan end = text.GetLine(last);
        string opening = text.GetLineText(first);
        int indent = opening.Length - opening.TrimStart().Length;
        string closing = text.GetLineText(last);
        int to = end.Start + closing.TrimEnd().Length;
        int from = Math.Min(start.Start + indent, to);
        return new TextRange(from, to - from);
    }
}

/// <summary>BuildKit's build checks on a Dockerfile, through the person's engine: <c>docker buildx build --call=check -</c>
/// on its text (its syntax directive blanked, <see cref="BuiltIn"/>) as it opens and is saved, no build, no context, on the
/// context's own builder and frontend (never a builder container or a frontend image started for it). Each rule a line
/// breaks is a warning, linked to its page; a build error is said too, but not a parse error (the server marks those) nor
/// a registry out of reach (BuildKit reads each base image's metadata, so offline it checks nothing).</summary>
internal sealed class DockerfileCheck(EngineChecks owner, OpenDocument document, WebDocument web) : EngineCheck(owner, document, web)
{
    /// <summary>How long a check may take: reading a base image's metadata from its registry the first time included.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    protected override bool AsTyped => false;

    protected override bool Hints => false;

    protected override bool Wanted => Owner.BuildKit;

    protected override async Task<Outcome> CheckAsync(ContainerEngine engine, string path, TextSnapshot snapshot, string text, CancellationToken cancel)
    {
        EngineState state = await engine.StateAsync(cancel: cancel);
        // Podman has no BuildKit of its own: Docker's buildx on it would start one in a container. Said once by the window.
        if (state.IsPodman && state.Status is EngineStatus.Ready or EngineStatus.NoCompose)
        {
            Owner.Limit(state);
            return Outcome.None;
        }
        if (state.Status is not (EngineStatus.Ready or EngineStatus.NoCompose) || state.BuildxVersion is null)
        {
            return Outcome.None;
        }
        if (BuiltIn(text) is not { } checkedText)
        {
            return Outcome.None;
        }
        List<string> words = ["buildx", "build", "--progress=quiet", "--call=check,format=json"];
        if (state.Context is { Length: > 0 } context)
        {
            words.AddRange(["--builder", context]);
        }
        words.Add("-");
        System.Diagnostics.ProcessStartInfo start = engine.Command(words, Path.GetDirectoryName(path));
        start.Environment.Remove("BUILDKIT_SYNTAX");
        ChildResult result = await ChildProcesses.RunAsync(start, checkedText, Limit, cancel, answers: [1]);
        if (!result.Output.TrimStart().StartsWith('{'))
        {
            // An older buildx (before --call), or a builder that would not start: nothing to show, said in the log.
            IdeErrors.Write($"BuildKit's checks could not run on {path}: {ContainerEngine.Said(result)}");
            return Outcome.None;
        }
        using JsonDocument read = JsonDocument.Parse(result.Output);
        JsonElement root = read.RootElement;
        List<Finding> found = [];
        if (root.TryGetProperty("warnings", out JsonElement warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement warning in warnings.EnumerateArray())
            {
                string? rule = ContainerEngine.Text(warning, "ruleName");
                string said = ContainerEngine.Text(warning, "detail") ?? ContainerEngine.Text(warning, "description") ?? rule ?? "BuildKit's check failed";
                Uri? page = ContainerEngine.Text(warning, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? link) && link.Scheme == Uri.UriSchemeHttps ? link : null;
                found.Add(At(snapshot, path, warning, ProblemSeverity.Warning, rule is null ? $"BuildKit: {said}" : $"BuildKit: {said} ({rule})")
                    with { Link = page, LinkText = page is null ? null : rule });
            }
        }
        if (root.TryGetProperty("buildError", out JsonElement error) && error.ValueKind == JsonValueKind.Object
            && ContainerEngine.Text(error, "message") is { } message && Shown(message))
        {
            found.Add(At(snapshot, path, error, ProblemSeverity.Error, $"BuildKit: {message}"));
        }
        return new Outcome(found, []);
    }

    /// <summary>The text BuildKit is given: its <c>syntax</c> parser directive (<c># syntax=</c>, or the <c>// syntax=</c>
    /// BuildKit reads too) blanked, each of its characters a space so lines and columns stay, so the engine's own Dockerfile
    /// frontend checks it and no frontend image the file names is pulled and run; its <c>check</c> and <c>escape</c>
    /// directives kept. Null for a text BuildKit would read whole as a JSON directive, which no Dockerfile is.</summary>
    internal static string? BuiltIn(string text)
    {
        // BuildKit passes over a byte-order mark and a first line that is a shebang before it looks for directives.
        int start = text.StartsWith('﻿') ? 1 : 0;
        if (text.AsSpan(start).StartsWith("#!", StringComparison.Ordinal))
        {
            int shebang = text.IndexOf('\n', start);
            start = shebang < 0 ? text.Length : shebang + 1;
        }
        if (text.AsSpan(start).TrimStart(" \t\r\n").StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }
        char[]? blanked = null;
        // Directives are the lines from the top that are each one; what either of BuildKit's two forms finds is among them.
        for (int line = start; line < text.Length;)
        {
            int end = text.IndexOf('\n', line);
            int stop = end < 0 ? text.Length : end;
            ReadOnlySpan<char> content = text.AsSpan(line, stop - line).TrimEnd('\r');
            if (Directive(content) is not { } name)
            {
                break;
            }
            if (name.Equals("syntax", StringComparison.OrdinalIgnoreCase))
            {
                blanked ??= text.ToCharArray();
                Array.Fill(blanked, ' ', line, content.Length);
            }
            line = stop + 1;
        }
        return blanked is null ? text : new string(blanked);
    }

    /// <summary>The name of the parser directive a line is (<c># name=value</c>, or <c>// name=value</c>), else null; read
    /// more loosely than BuildKit's own pattern, so whatever it takes for one is one here.</summary>
    private static string? Directive(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> spaces = " \t\f\v";
        line = line.TrimStart(spaces);
        if (line.StartsWith("//", StringComparison.Ordinal))
        {
            line = line[2..];
        }
        else if (line.StartsWith("#", StringComparison.Ordinal))
        {
            line = line[1..];
        }
        else
        {
            return null;
        }
        line = line.TrimStart(spaces);
        int name = 0;
        while (name < line.Length && (char.IsAsciiLetter(line[name]) || (name > 0 && char.IsAsciiDigit(line[name]))))
        {
            name++;
        }
        ReadOnlySpan<char> rest = line[name..].TrimStart(spaces);
        return name > 0 && rest.StartsWith("=", StringComparison.Ordinal) && rest.Length > 1 ? line[..name].ToString() : null;
    }

    /// <summary>A finding at the lines its location's first range names (from 1), else the file's first line.</summary>
    private static Finding At(TextSnapshot text, string path, JsonElement found, ProblemSeverity severity, string message)
    {
        int first = 1;
        int last = 1;
        if (Property(found, "location") is { } location && Property(location, "ranges") is { ValueKind: JsonValueKind.Array } ranges && ranges.GetArrayLength() > 0)
        {
            JsonElement range = ranges[0];
            first = LineOf(range, "start") ?? 1;
            last = LineOf(range, "end") ?? first;
        }
        TextRange marked = Lines(text, first - 1, last - 1);
        return new Finding(severity, message, path, text.GetPosition(marked.Start), marked);

        static int? LineOf(JsonElement range, string end) =>
            Property(range, end) is { } place && Property(place, "line") is { ValueKind: JsonValueKind.Number } line && line.TryGetInt32(out int n) && n > 0 ? n : null;

        static JsonElement? Property(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : null;
    }

    /// <summary>Whether a build error is the check's to say: not the Dockerfile's syntax, which its server marks as typed,
    /// nor a registry the engine could not reach.</summary>
    private static bool Shown(string message) =>
        !message.Contains("dockerfile parse error", StringComparison.OrdinalIgnoreCase)
        && !((string[])["dial tcp", "no such host", "i/o timeout", "connection refused", "network is unreachable", "TLS handshake timeout",
            "context deadline exceeded", "server misbehaving", "connection reset"]).Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Whether Settings' Check Dockerfiles with BuildKit is on in a Passenger with no settings.json saying otherwise:
/// on, unless the IDE's <c>PASSENGER_BUILDKIT_CHECKS</c> is <c>off</c>, which keeps a run from reaching any registry
/// through the Dockerfiles it opens.</summary>
public static class BuildKitChecks
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_BUILDKIT_CHECKS";

    /// <summary>The setting's default; a value of the variable other than <c>off</c> or <c>on</c> is fatal as the IDE starts.</summary>
    public static bool ByDefault => Environment.GetEnvironmentVariable(Variable) switch
    {
        null or "" or "on" => true,
        "off" => false,
        string other => throw new FormatException($"{Variable} takes off or on, not '{other}'"),
    };
}
