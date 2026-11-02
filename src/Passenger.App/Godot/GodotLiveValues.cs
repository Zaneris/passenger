using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Godot;

/// <summary>A running Godot game's values after its scripts' members in the code, with no breakpoint: for each C# or
/// GDScript file of the game's project an editor shows, the members on the lines it shows are read from the node running
/// the script (the one chosen in the Live Visual Tree where several do, else their count after the class), through
/// Passenger's helper in the game, on its monitors' beat once a second and soon after what is shown changes; never for a
/// file no editor shows, nor while the debugger holds the game. A value's hint, hovered, shows its last minute. Gone once
/// the game ends, and while turned off in Settings. A UI-thread object.</summary>
public sealed class GodotLiveValues
{
    private readonly EditorArea editors;
    private readonly LiveTreeController tree;
    private readonly Debounce soon;
    private readonly GodotLiveHistory history = new();

    /// <summary>What each file shows, by the editors showing it.</summary>
    private readonly Dictionary<OpenDocument, Showing> showing = [];

    /// <summary>Each file's members, by the text (its parse, for C#) and the game's folder they were found in.</summary>
    private readonly Dictionary<OpenDocument, (object Read, string Folder, GodotScriptMembers? Members)> members = [];

    private ProjectSession? session;
    private GodotGame? game;
    private bool on = true;
    private bool reading;
    private bool again;

    /// <summary>Bumped by whatever makes a read under way out of date: another game, its end, a pause, the setting.</summary>
    private int generation;

    /// <summary>What a file shows: the lines its editors showed when it was read, the node read and its name, and each hint
    /// with the member it is of.</summary>
    private sealed record Showing(int First, int Last, ulong Node, string NodeName, List<(LineAnnotation Hint, string Member, GodotMemberValue Read)> Hints);

    public GodotLiveValues(EditorArea editors, LiveTreeController tree)
    {
        this.editors = editors;
        this.tree = tree;
        soon = new Debounce(TimeSpan.FromMilliseconds(150), ReadAsync);
        tree.ChosenChanged += (_, _) => Soon();
        editors.ActiveChanged += (_, _) => Soon();
        editors.Closed += (_, document) =>
        {
            showing.Remove(document);
            members.Remove(document);
        };
        editors.ForEachEditor(editor => editor.ScrollChanged += (_, _) => Scrolled(editor));
    }

    /// <summary>Whether values are shown (Settings › Godot); off, those shown go at once.</summary>
    public bool On
    {
        get => on;
        set
        {
            UiThread.Check();
            if (on == value)
            {
                return;
            }
            on = value;
            if (value)
            {
                Soon();
            }
            else
            {
                Clear();
            }
        }
    }

    /// <summary>Why the last read of the game came to nothing, until one does: what a wait for values that never came says.</summary>
    public string? Unread { get; private set; }

    /// <summary>Follows a session from now: the game it has chosen, and whether its debugger holds it.</summary>
    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        if (session is { } previous)
        {
            previous.LiveAppChanged -= OnGameChosen;
            previous.Debug.StateChanged -= OnDebugState;
        }
        session = next;
        next.LiveAppChanged += OnGameChosen;
        next.Debug.StateChanged += OnDebugState;
        Follow(next.Game);
    }

    private void OnGameChosen(object? sender, EventArgs e) => Follow(session?.Game);

    private void OnDebugState(object? sender, EventArgs e)
    {
        if (Held(game))
        {
            Clear();
        }
        else
        {
            Soon();
        }
    }

    private void Follow(GodotGame? next)
    {
        UiThread.Check();
        if (game == next)
        {
            return;
        }
        if (game is not null)
        {
            game.MonitorsChanged -= OnBeat;
            game.StateChanged -= OnGameState;
        }
        Clear();
        history.Clear();
        game = next;
        if (next is not null)
        {
            next.MonitorsChanged += OnBeat;
            next.StateChanged += OnGameState;
            Soon();
        }
    }

    private void OnGameState(object? sender, EventArgs e)
    {
        if (sender != game)
        {
            return;
        }
        if (game is { State: LiveAppState.Ended })
        {
            Clear();
            history.Clear();
        }
        else
        {
            Soon();
        }
    }

    /// <summary>The game's monitors came, once a second: the values shown are read again.</summary>
    private void OnBeat(object? sender, EventArgs e)
    {
        if (sender == game && !reading && Readable() is not null)
        {
            IdeErrors.Observe(ReadAsync());
        }
    }

    /// <summary>An editor scrolled: its file is read again soon only where it now shows lines it was not read for, so a
    /// scroll among lines read costs nothing.</summary>
    private void Scrolled(CodeEditor editor)
    {
        if (game is not { State: LiveAppState.Connected } || !on || soon.IsWaiting)
        {
            return;
        }
        foreach ((OpenDocument document, Showing shown) in showing)
        {
            if (document.Text == editor.Document)
            {
                (int first, int last) = editor.LinesShown;
                if (first < shown.First || last > shown.Last)
                {
                    soon.Restart();
                }
                return;
            }
        }
    }

    private void Soon()
    {
        if (on && game is { State: LiveAppState.Connected })
        {
            soon.Restart();
        }
    }

    /// <summary>Whether the debugger holds the game, which then answers nothing.</summary>
    private static bool Held(GodotGame? game) => game?.Debugged is { State: DebugState.Paused };

    /// <summary>The game whose values are read now, or null: none is followed, it is not up, the debugger holds it, or values
    /// are turned off.</summary>
    private GodotGame? Readable() => on && game is { State: LiveAppState.Connected } live && !Held(live) ? live : null;

    /// <summary>Every value shown goes, and a read under way lands nothing.</summary>
    private void Clear()
    {
        generation++;
        soon.Stop();
        foreach (OpenDocument document in showing.Keys)
        {
            document.LiveValues = [];
        }
        showing.Clear();
    }

    /// <summary>Reads what the files shown want, one read at a time, the last word winning.</summary>
    private async Task ReadAsync()
    {
        UiThread.Check();
        if (reading)
        {
            again = true;
            return;
        }
        reading = true;
        try
        {
            do
            {
                again = false;
                await ReadOnceAsync();
            }
            while (again);
        }
        finally
        {
            reading = false;
        }
    }

    private async Task ReadOnceAsync()
    {
        int now = generation;
        GodotGame? read = Readable();
        Dictionary<OpenDocument, (GodotScriptMembers Members, int First, int Last)> wanted = read is null ? [] : Wanted(read);
        foreach (OpenDocument hidden in showing.Keys.Where(d => !wanted.ContainsKey(d)).ToList())
        {
            hidden.LiveValues = [];
            showing.Remove(hidden);
        }
        if (read is null || wanted.Count == 0)
        {
            return;
        }
        ulong chosen = tree.App == read && tree.Chosen is long id ? (ulong)id : 0;
        // Only the members on the lines shown are asked of, and a file showing none asks nothing.
        List<GodotScriptAsk> asks = [.. wanted.Values.GroupBy(w => w.Members.Script, StringComparer.Ordinal)
            .Select(script => new GodotScriptAsk(script.Key, chosen, [.. script.SelectMany(w => Seen(w.Members, w.First, w.Last).Select(m => m.Name)).Distinct(StringComparer.Ordinal)]))
            .Where(a => a.Names.Count > 0)];
        if (asks.Count == 0)
        {
            Show(wanted, [], [], 0);
            return;
        }
        IReadOnlyList<GodotScriptRead> answers;
        try
        {
            answers = await read.ScriptValuesAsync(asks);
        }
        catch (LiveAppException refused)
        {
            // The beat asks again in a second; the game's own end or pause clears what is shown.
            Unread = refused.Message;
            return;
        }
        Unread = null;
        if (now != generation || Readable() != read)
        {
            return;
        }
        long at = Environment.TickCount64;
        Show(wanted, asks, answers, at);
        history.Forget(at);
    }

    /// <summary>Each file shown its script's answer; one whose script was not asked of (no member on its lines shown) shows
    /// nothing, the lines it was read for kept so a scroll to its members reads them.</summary>
    private void Show(Dictionary<OpenDocument, (GodotScriptMembers Members, int First, int Last)> wanted, List<GodotScriptAsk> asks, IReadOnlyList<GodotScriptRead> answers, long at)
    {
        foreach ((OpenDocument document, (GodotScriptMembers script, int first, int last)) in wanted)
        {
            int asked = asks.FindIndex(a => a.Script == script.Script);
            Showing shown = asked >= 0 ? Shown(script, first, last, answers[asked], at) : new Showing(first, last, 0, string.Empty, []);
            showing[document] = shown;
            IReadOnlyList<LineAnnotation> hints = [.. shown.Hints.Select(h => h.Hint)];
            if (!hints.SequenceEqual(document.LiveValues))
            {
                document.LiveValues = hints;
            }
        }
    }

    /// <summary>A script's members on the lines (from zero) from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private static IEnumerable<GodotScriptMember> Seen(GodotScriptMembers script, int first, int last) => script.Members.Where(m => m.Line >= first && m.Line <= last);

    /// <summary>The files of the game's project the editors show, each with its script's members and the lines shown (of
    /// every editor showing it).</summary>
    private Dictionary<OpenDocument, (GodotScriptMembers Members, int First, int Last)> Wanted(GodotGame read)
    {
        Dictionary<OpenDocument, (GodotScriptMembers Members, int First, int Last)> wanted = [];
        foreach (EditorGroup group in editors.Groups)
        {
            if (group.Active is not { HoldsText: true } document || !group.Editor.IsEffectivelyVisible || MembersOf(document, read) is not { Members.Count: > 0 } script)
            {
                continue;
            }
            (int first, int last) = group.Editor.LinesShown;
            wanted[document] = wanted.TryGetValue(document, out (GodotScriptMembers, int First, int Last) known)
                ? (script, Math.Min(known.First, first), Math.Max(known.Last, last))
                : (script, first, last);
        }
        return wanted;
    }

    /// <summary>A file's script and members, found again only once its text has changed; null for a file of another project,
    /// or of no script's class.</summary>
    private GodotScriptMembers? MembersOf(OpenDocument document, GodotGame read)
    {
        bool csharp = document.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if ((!csharp && !document.Path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)) || !PathNames.IsUnder(document.Path, read.Folder))
        {
            return null;
        }
        object text = csharp ? document.Syntax?.Current is { } parsed ? parsed : document.Text.Snapshot : document.Text.Snapshot;
        if (members.TryGetValue(document, out (object Read, string Folder, GodotScriptMembers? Members) known) && ReferenceEquals(known.Read, text) && known.Folder == read.Folder)
        {
            return known.Members;
        }
        string script = "res://" + Path.GetRelativePath(read.Folder, document.Path).Replace('\\', '/');
        GodotScriptMembers? found = text switch
        {
            Languages.CSharp.ParsedText syntax => GodotMembers.OfCSharp(syntax.Root, syntax.Text, script),
            Text.TextSnapshot snapshot when !csharp => GodotMembers.OfGdScript(snapshot, script),
            _ => null,
        };
        members[document] = (text, read.Folder, found);
        return found;
    }

    /// <summary>What a file shows of its script's answer: each member on the lines shown its value, recorded for its last
    /// minute; where several nodes run the script and none is chosen, their count, after the class's line when it is shown,
    /// else after the first member's; where one of several is chosen, its name after the class's line.</summary>
    private Showing Shown(GodotScriptMembers script, int first, int last, GodotScriptRead answer, long at)
    {
        List<(LineAnnotation, string, GodotMemberValue)> hints = [];
        List<GodotScriptMember> seen = [.. Seen(script, first, last)];
        if (answer.Node == 0)
        {
            if (answer.Count > 1 && seen.Count > 0)
            {
                int line = script.ClassLine >= first && script.ClassLine <= last ? script.ClassLine : seen[0].Line;
                hints.Add((new LineAnnotation(line, $"{Plural.Of(answer.Count, "node")} run {script.Class}: choose one in the Live Visual Tree", LineAnnotationLook.Muted), string.Empty, default));
            }
            return new Showing(first, last, 0, string.Empty, hints);
        }
        if (answer.Count > 1)
        {
            hints.Add((new LineAnnotation(script.ClassLine, $"{answer.NodeName}, chosen of {answer.Count} nodes", LineAnnotationLook.Muted), string.Empty, default));
        }
        foreach (GodotScriptMember member in seen)
        {
            if (answer.Values.TryGetValue(member.Name, out GodotMemberValue read))
            {
                hints.Add((new LineAnnotation(member.Line, GodotLiveWords.Hint(member.Name, read), LineAnnotationLook.Muted), member.Name, read));
                history.Add(answer.Node, member.Name, at, read.Words is null && !read.IsPath ? read.Value : null);
            }
        }
        return new Showing(first, last, answer.Node, answer.NodeName, hints);
    }

    /// <summary>The card for a value's hint the pointer rests on: the node's member and its value, then its last minute as a
    /// line of each of its numbers with the lowest and highest, or that it holds none to chart; false off a value's hint.</summary>
    internal bool Card(OpenDocument document, LineAnnotation hint, IHoverCard card)
    {
        UiThread.Check();
        if (!showing.TryGetValue(document, out Showing? shown) || shown.Hints.FindIndex(h => h.Member.Length > 0 && h.Hint.Text == hint.Text) is not (>= 0 and int at))
        {
            return false;
        }
        (_, string member, GodotMemberValue read) = shown.Hints[at];
        card.AddCode($"{shown.NodeName}.{member} = {GodotLiveWords.Of(read)}");
        IReadOnlyList<(long At, double[] Parts)> readings = history.Of(shown.Node, member, Environment.TickCount64);
        if (readings.Count < 2)
        {
            card.AddText(GodotLiveWords.Parts(read.Words is null && !read.IsPath ? read.Value : null) is null
                ? $"Read from {game?.Name} once a second while it runs"
                : $"Read from {game?.Name} once a second: its last minute shows once it has been read twice");
            return true;
        }
        IReadOnlyList<string> names = GodotLiveWords.PartNames(read.Value);
        int parts = readings[^1].Parts.Length;
        string[] brushes = [.. Enumerable.Range(0, parts).Select(i => Graphs[i % Graphs.Length])];
        Sparkline line = new(Kept, fromZero: false, fromRight: true, brushes) { Height = 44, Width = 240 };
        foreach ((long _, double[] values) in readings)
        {
            line.Add(values);
        }
        card.AddChart(new Border { Classes = { StyleClasses.Framed }, Child = line, HorizontalAlignment = HorizontalAlignment.Left });
        string Range(int part) => $"{GodotLiveWords.Number(readings.Min(r => r.Parts[part]))} to {GodotLiveWords.Number(readings.Max(r => r.Parts[part]))}";
        string over = $"over the last {Plural.Of(Math.Max(1, (readings[^1].At - readings[0].At + 500) / 1000), "second")}, read once a second";
        if (parts == 1)
        {
            card.AddText($"From {Range(0)} {over}");
            return true;
        }
        // Each part's line named beside its colour, with its lowest and highest.
        StackPanel legend = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        for (int i = 0; i < parts; i++)
        {
            Border swatch = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable(brushes[i]));
            TextBlock words = MutedText.Of($"{(i < names.Count ? names[i] : $"{i}")} {Range(i)}");
            words.FontSize = 12;
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { swatch, words } });
        }
        card.AddChart(legend);
        card.AddText($"Each part's lowest and highest {over}");
        return true;
    }

    /// <summary>How many readings a card's line has room for: a minute's, one a second.</summary>
    private const int Kept = (int)(GodotLiveHistory.Span / 1000);

    /// <summary>The colours of a value's parts, in the order of the theme's charts.</summary>
    private static readonly string[] Graphs = [TokenKeys.Graph_0, TokenKeys.Graph_1, TokenKeys.Graph_2, TokenKeys.Graph_3];
}
