using ModelContextProtocol.Protocol;
using Passenger.App.Debugging;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Languages.CSharp;

namespace Passenger.App.Mcp;

/// <summary>A Godot game Run, Debug or Profile started, as the app_ tools reach it: through Passenger as its remote
/// debugger (<see cref="GodotGame"/>), its scene tree and a node's properties read and set over Godot's protocol and its
/// picture asked of Godot, where its nodes are drawn and input as a person gives it through Passenger's helper in the game
/// (<see cref="GodotAgent"/>). Places are the viewport's units, which a node's box and a click share.</summary>
public sealed partial class PassengerTools
{
    /// <summary>How long an app_ tool's call may wait on a game in all, once it has the game: each of its questions has a
    /// limit of its own, and the call's sum of them, its messages' turns and writes among them, is held to this too.</summary>
    private static TimeSpan GameLimit => Waits.Of(TimeSpan.FromSeconds(40));

    /// <summary>The Godot game an app_ tool's app means, once it has connected and opened its first scene: with none named, the
    /// chosen game when no Avalonia or WPF app runs; run, debug or profile as the chosen one was started; or the name of any
    /// game running, a copy's among them. Null where the app meant is another; a game paused in the debugger, not yet
    /// connected, with no scene open in time or ended is refused saying what to do.</summary>
    private async Task<GodotGame?> GameAsync(string? app, CancellationToken cancel)
    {
        if (Session.Game is not { } chosen)
        {
            return null;
        }
        GodotGame? meant = app switch
        {
            null => !Session.Inspecting.Any(a => a.App.State is LiveAppState.Waiting or LiveAppState.Connected) ? chosen : null,
            "ide" => null,
            "run" => RunApp() is null && chosen.Debugged is null && Session.Profiled is null ? chosen : null,
            "debug" => DebugApp() is null && chosen.Debugged is not null ? chosen : null,
            "profile" => Session.ProfiledApp is null && Session.Profiled is not null && chosen.Debugged is null ? chosen : null,
            _ => chosen.Name.Equals(app, StringComparison.OrdinalIgnoreCase) ? chosen
                : Session.Games.FirstOrDefault(g => g.Name.Equals(app, StringComparison.OrdinalIgnoreCase)),
        };
        if (meant is not { } game)
        {
            return null;
        }
        if (game.Debugged is { State: DebugState.Paused })
        {
            throw new InvalidOperationException($"{game.Name} is paused in the debugger, and its scene tree answers nothing until it runs: debug_continue");
        }
        await When(() => game.State != LiveAppState.Waiting, AppUiLimit, cancel, On(h => game.StateChanged += h, h => game.StateChanged -= h));
        switch (game.State)
        {
            case LiveAppState.Connected:
                // A copy just started, connected before its first scene, has nothing yet for the helper to come in beside.
                await game.FirstSceneAsync(AppUiLimit, cancel);
                return game;
            case LiveAppState.Waiting:
                throw new TimeoutException($"{game.Name} has not connected to Passenger, its remote debugger, in {AppUiLimit.TotalSeconds:0} s");
            default:
                throw new InvalidOperationException($"{game.Name} has ended: run or debug_start starts it again");
        }
    }

    /// <summary>The game's scene tree as the app_ tools read one: each node's id, type (a script's by its file's name), name
    /// and whether it shows, with where the helper says it is drawn, the text it shows and whether it is disabled, focused or
    /// out of view (none of those where the helper could not say, <c>Unplaced</c> saying why); with
    /// <paramref name="properties"/>, the values of those of each node's properties it has; and the tree as Godot sent it.</summary>
    private static async Task<(AppTree Tree, string? Unplaced, GodotNode Root)> GameTreeAsync(GodotGame game, IReadOnlyList<string> properties)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        GodotNode root = await game.SceneTreeAsync();
        Dictionary<long, List<object?>> drawn = [];
        string? unplaced = null;
        try
        {
            if (await game.AskAsync("layout", properties.Select(p => (object?)p).ToList()) is [List<object?> nodes])
            {
                foreach (List<object?> node in nodes.OfType<List<object?>>().Where(n => n is [long, ..] && n.Count >= 9))
                {
                    drawn[(long)node[0]!] = node;
                }
            }
        }
        catch (LiveAppException failed)
        {
            unplaced = failed.Message;
        }
        return (new AppTree([Of(root)]), unplaced, root);

        Element Of(GodotNode node)
        {
            Element element = new((long)node.Id, TypeOf(node.Type), node.Name, [], false, node.SceneFile.Length > 0, node.Visible,
                [.. node.Children.Where(c => !GodotAgent.Is(c)).Select(Of)]) { Deeper = node.Deeper ? true : null };
            if (!drawn.TryGetValue(element.Id, out List<object?>? at))
            {
                return element;
            }
            long flags = at[7] is long given ? given : 0;
            return element with
            {
                Bounds = at[1] is true ? new Box(Number(at[2]), Number(at[3]), Number(at[4]), Number(at[5])) : null,
                Text = at[6] as string,
                Disabled = (flags & 1) != 0 ? true : null,
                Focused = (flags & 2) != 0 ? true : null,
                OutOfView = (flags & 4) != 0 ? true : null,
                Values = at[8] is List<object?> { Count: > 0 } named
                    ? named.OfType<List<object?>>().Where(v => v is [string, _]).ToDictionary(v => (string)v[0]!, v => GodotVariant.Shown(v[1]))
                    : null,
            };
        }

        // A script's type by its file's name, which names its class: Main for res://Main.cs.
        static string TypeOf(string type) => type.StartsWith(Passenger.Workspace.GodotResPaths.Scheme, StringComparison.Ordinal) || Path.IsPathRooted(type)
            ? Path.GetFileNameWithoutExtension(type)
            : type;
    }

    private static double Number(object? value) => value switch
    {
        double real => real,
        long whole => whole,
        _ => 0,
    };

    private static long Whole(object? value) => value is long whole ? whole : 0;

    /// <summary>The game's tree, flattened, and its window named (else its root).</summary>
    private async Task<(List<AppNode> Nodes, AppNode Window)> GameTreeOf(GodotGame game, string? window)
    {
        List<AppNode> nodes = AppQuery.Flatten((await GameTreeAsync(game, [])).Tree.Roots);
        return (nodes, await GameWindowAsync(game, nodes, window));
    }

    /// <summary>A window of a game's tree by what names it, a window below its root among them, as app_windows lists them;
    /// its root for none.</summary>
    private async Task<AppNode> GameWindowAsync(GodotGame game, List<AppNode> nodes, string? named)
    {
        if (named is not { Length: > 0 })
        {
            return nodes[0];
        }
        HashSet<string> windows = [.. (await GameWindowsAsync(game)).Windows.Select(w => w.Id)];
        return WindowNamed([.. nodes.Where(n => windows.Contains(Id(n.Element.Id)))], named, game.Name);
    }

    private async Task<AppWindows> GameWindowsAsync(GodotGame game)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        List<object?> said = await game.AskAsync("windows");
        IEnumerable<List<object?>> found = said is [List<object?> windows] ? windows.OfType<List<object?>>().Where(w => w.Count >= 10) : [];
        return new AppWindows(game.Name, game.Framework, null, game.ProcessId, [.. found.Select(w => new AppWindowInfo(Id(Whole(w[0])), w[1] as string ?? "Window",
            w[2] as string, w[3] as string, Math.Round(Number(w[4]), 2), Math.Round(Number(w[5]), 2), Math.Round(Number(w[6]), 2), w[7] is true, w[8] is true,
            Whole(w[9]) is not 0 and long owner ? Id(owner) : null))]);
    }

    private async Task<CallToolResult> GamePictureAsync(GodotGame game, string? control, bool area)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        if (control is { Length: > 0 } || area)
        {
            throw new ArgumentException($"{game.Name} is a Godot game, whose picture Godot takes of its whole window: name no control or area");
        }
        (byte[] png, int width, int height) = await game.PictureAsync();
        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = $"{game.Name}'s window as Godot draws it: {width}x{height} pixels" }],
        };
    }

    /// <summary>The members the code of the script each node <paramref name="control"/> finds runs declares, each with its
    /// value in that node as the code's hints say it, read for every node in one message through Passenger's helper.</summary>
    private async Task<AppScriptMembers> GameMembersAsync(GodotGame game, string control, string? window)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        (AppTree tree, _, GodotNode root) = await GameTreeAsync(game, []);
        List<AppNode> nodes = AppQuery.Flatten(tree.Roots);
        AppNode chosen = await GameWindowAsync(game, nodes, window);
        List<AppNode> found = AppQuery.Find(Within(nodes, window is null ? null : chosen), control);
        if (found.Count == 0)
        {
            throw new ArgumentException($"Nothing in {game.Name} matches {control}; app_tree shows what there is");
        }
        Dictionary<long, string> scripts = root.All().Where(n => n.Type.StartsWith(Passenger.Workspace.GodotResPaths.Scheme, StringComparison.Ordinal)).ToDictionary(n => (long)n.Id, n => n.Type);
        List<(AppNode Node, string Script, GodotScriptMembers Members)> read = [];
        Dictionary<string, GodotScriptMembers?> declared = new(StringComparer.Ordinal);
        foreach (AppNode node in found)
        {
            if (scripts.TryGetValue(node.Element.Id, out string? script)
                && (declared.TryGetValue(script, out GodotScriptMembers? known) ? known : declared[script] = await ScriptMembersAsync(game, script)) is { Members.Count: > 0 } members)
            {
                read.Add((node, script, members));
            }
        }
        if (read.Count == 0)
        {
            throw new ArgumentException(found.Count == 1
                ? $"{AppQuery.Short(found[0].Element)} runs no script of {game.Name}'s whose code declares a member"
                : $"None of the {found.Count} nodes {control} finds runs a script of {game.Name}'s whose code declares a member");
        }
        IReadOnlyList<GodotScriptRead> answers = await game.ScriptValuesAsync(
            [.. read.Select(r => new GodotScriptAsk(r.Script, (ulong)r.Node.Element.Id, [.. r.Members.Members.Select(m => m.Name)]))]);
        return new AppScriptMembers(game.Name, [.. read.Select((r, i) => new AppNodeMembers(AppQuery.Short(r.Node.Element), Relative(ScriptFile(game, r.Script) ?? r.Script), r.Members.Class,
            [.. r.Members.Members.Where(m => answers[i].Values.ContainsKey(m.Name)).Select(m => new AppMemberValue(m.Name, m.Line + 1, GodotLiveWords.Of(answers[i].Values[m.Name])))]))]);
    }

    /// <summary>The members a script's code declares, from its file as the editor has it (open, its parse; else as on disk,
    /// read and parsed off the window's thread); null for a file not there or of no script's class.</summary>
    private async Task<GodotScriptMembers?> ScriptMembersAsync(GodotGame game, string script)
    {
        if (ScriptFile(game, script) is not { } file)
        {
            return null;
        }
        bool csharp = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if (Editors.Find(file) is { HoldsText: true } open)
        {
            return csharp
                ? open.Syntax?.Current is { } parsed ? GodotMembers.OfCSharp(parsed.Root, parsed.Text, script) : null
                : GodotMembers.OfGdScript(open.Text.Snapshot, script);
        }
        Microsoft.CodeAnalysis.CSharp.CSharpParseOptions options = Editors.ParseOptions(file);
        return await BackgroundWork.Run(() =>
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            Text.TextSnapshot text = Text.TextSnapshot.From(source);
            return csharp
                ? GodotMembers.OfCSharp(CSharpParsing.Parse(source, options).GetRoot(), text, script)
                : GodotMembers.OfGdScript(text, script);
        });
    }

    /// <summary>The file of a game's script by its <c>res://</c> path; null for one outside its project or built into a scene.</summary>
    private static string? ScriptFile(GodotGame game, string script) => Passenger.Workspace.GodotResPaths.ScriptFileOf(game.Folder, script) is { } file ? PathNames.Canonical(file) : null;

    private async Task<AppElementDetails> GameDetailsAsync(GodotGame game, string control, string? window, string? properties)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        (List<AppNode> nodes, AppNode chosen) = await GameTreeOf(game, window);
        AppNode element = AppQuery.One(Within(nodes, window is null ? null : chosen), control);
        ElementDetails details = await game.DetailsAsync(element.Element.Id);
        string typed = properties?.Trim() ?? string.Empty;
        return new AppElementDetails(AppQuery.Short(element.Element), details.Type, details.FullType, details.Name, [], [],
            details.Source is { } source ? Placed(source) : null, details.Source is null ? details.NoSource : null, null, null,
            [.. details.Properties.Where(p => typed.Length == 0 || p.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .Select(p => new AppProperty(p.Name, p.Value, p.Type, p.Source, null, null, null))], []) { Ran = game.RanByReading(element.Element.Id) };
    }

    /// <summary>Sets a node's property to the value a text gives, as the Live Visual Tree's are typed, and says what the game
    /// holds once it has taken it.</summary>
    private async Task<string> GameSetAsync(GodotGame game, string? control, string? window, string property, string text)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        if (control is not { Length: > 0 })
        {
            throw new ArgumentException("Name the node (control) whose property to set");
        }
        (List<AppNode> nodes, AppNode chosen) = await GameTreeOf(game, window);
        AppNode element = AppQuery.One(Within(nodes, window is null ? null : chosen), control);
        string named = AppQuery.Short(element.Element);
        IReadOnlyList<PropertyValue> shown = (await game.DetailsAsync(element.Element.Id)).Properties;
        PropertyValue value = shown.FirstOrDefault(p => p.Name == property) ?? shown.FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{named} has no property {property}: app_details lists its properties");
        await game.SetAsync(element.Element.Id, value, text);
        string now = (await game.DetailsAsync(element.Element.Id)).Properties.FirstOrDefault(p => p.Name == value.Name)?.Value ?? "nothing";
        return $"Set {named}'s {value.Name} to {text}: it holds {now} now";
    }

    /// <summary>What a game's input acts on: the tree it was aimed in, the node named (null for a point, or whatever has the
    /// focus) and the node whose window it is in.</summary>
    private async Task<(List<AppNode> Nodes, AppNode? Element, AppNode Window)> GameTarget(GodotGame game, string? control, string? window, bool needsPoint, double? x, double? y)
    {
        (List<AppNode> nodes, AppNode chosen) = await GameTreeOf(game, window);
        if (control is { Length: > 0 } query)
        {
            return (nodes, AppQuery.One(Within(nodes, window is null ? null : chosen), query), chosen);
        }
        if (needsPoint && (x is null || y is null))
        {
            throw new ArgumentException("Name a control, or give x and y in the window");
        }
        return (nodes, null, chosen);
    }

    /// <summary>What the helper said its input came to (the point, what was under it, what has the focus, what that shows and
    /// the window it went to), as the app_ tools' answers read it, nodes named as the tree read before names them.</summary>
    private static InputDone GameDone(List<AppNode> nodes, AppNode window, List<object?> said)
    {
        long under = said.Count > 2 ? Whole(said[2]) : 0;
        long focus = said.Count > 3 ? Whole(said[3]) : 0;
        return new InputDone(said.Count > 5 ? Whole(said[5]) : window.Element.Id, said.Count > 0 ? Number(said[0]) : null, said.Count > 1 ? Number(said[1]) : null, under is 0 ? null : under,
            under is 0 ? null : NameOf(under), said.Count > 4 ? said[4] as string : null, focus is 0 ? null : NameOf(focus));

        string NameOf(long id) => nodes.FirstOrDefault(n => n.Element.Id == id) is { } node ? AppQuery.Short(node.Element) : Id(id);
    }

    private static long GameModifiers(IReadOnlyList<string>? names) => GameModifiers(Modifiers(names) ?? []);

    /// <summary>Modifiers as the helper takes them, Primary the command key of the system the game runs on.</summary>
    private static long GameModifiers(IEnumerable<Modifier> held) => held.Aggregate(0L, (flags, one) => flags | (long)(one switch
    {
        Modifier.Shift => GodotAgent.Shift,
        Modifier.Control => GodotAgent.Control,
        Modifier.Alt => GodotAgent.Alt,
        Modifier.Meta => GodotAgent.Meta,
        _ => OperatingSystem.IsMacOS() ? GodotAgent.Meta : GodotAgent.Control,
    }));

    private async Task<string> GameClickAsync(GodotGame game, string? control, double? x, double? y, string? window, string button, int count, IReadOnlyList<string>? modifiers)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        long pressed = button switch
        {
            "left" => 1,
            "right" => 2,
            "middle" => 3,
            _ => throw new ArgumentException($"No button '{button}': there are left, right and middle"),
        };
        (List<AppNode> nodes, AppNode? element, AppNode target) = await GameTarget(game, control, window, needsPoint: true, x, y);
        GodotPicking picking = game.Picking;
        int picks = game.Picks;
        List<object?> said = await game.AskAsync("click", element?.Element.Id ?? 0L, target.Element.Id, x is not null && y is not null, x ?? 0, y ?? 0, pressed, (long)Math.Max(1, count),
            GameModifiers(modifiers));
        InputDone done = GameDone(nodes, target, said);
        string clicked = count switch
        {
            2 => "Double-clicked",
            >= 3 => "Triple-clicked",
            _ => button == "left" ? "Clicked" : $"{char.ToUpperInvariant(button[0])}{button[1..]}-clicked",
        };
        return $"{clicked} {Aimed(element, done)}{Took(element, done, "click")}{Said(done)}{await PickedAsync(game, picking, picks, nodes)}";
    }

    /// <summary>What a click given while Select Element picks in the game came to: the node Godot picked, which the Live
    /// Visual Tree chose (its pick comes before the helper's answer on the one connection), or that it picked none there.</summary>
    private static async Task<string> PickedAsync(GodotGame game, GodotPicking picking, int picks, List<AppNode> nodes)
    {
        if (picking == GodotPicking.Off)
        {
            return string.Empty;
        }
        if (game.Picks == picks || game.LastPick is not { } picked)
        {
            return picking == GodotPicking.In3D && await game.MissedAsync() is { } missed ? $"; Select Element picked no node there: {missed}" : "; Select Element picked no node there";
        }
        string named = nodes.FirstOrDefault(n => n.Element.Id == (long)picked.Id) is { } node ? AppQuery.Short(node.Element) : $"{picked.Class} {Id((long)picked.Id)}";
        return $"; Select Element picked {named}, which the Live Visual Tree chose";
    }

    private async Task<string> GameTypeAsync(GodotGame game, string text, string? control, bool replace, string? window)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        (List<AppNode> nodes, AppNode? element, AppNode target) = await GameTarget(game, control, window, needsPoint: false, null, null);
        List<object?> said = await game.AskAsync("type", element?.Element.Id ?? 0L, target.Element.Id, text, replace);
        InputDone done = GameDone(nodes, target, said);
        return $"Typed {Plural.Of(text.Length, "character")} into {done.Focused ?? "the window, nothing having the focus"}{KeysHeld(element, done, said)}{Said(done)}";
    }

    private async Task<string> GameKeyAsync(GodotGame game, string keys, string? control, string? window)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        IReadOnlyList<Chord> chords = Chords(keys);
        (List<AppNode> nodes, AppNode? element, AppNode target) = await GameTarget(game, control, window, needsPoint: false, null, null);
        List<object?> pressed = [.. chords.Select(c => (object?)new List<object?> { GodotAgent.KeyName(c.Key), GameModifiers(c.Modifiers) })];
        List<object?> said = await game.AskAsync("key", element?.Element.Id ?? 0L, target.Element.Id, pressed);
        InputDone done = GameDone(nodes, target, said);
        return $"Pressed {keys.Trim()} in {done.Focused ?? "the window, nothing having the focus"}{KeysHeld(element, done, said)}{Said(done)}";
    }

    /// <summary>Why keys aimed at a control went elsewhere, where the helper says an exclusive window kept them.</summary>
    private static string KeysHeld(AppNode? element, InputDone done, List<object?> said) =>
        element is not null && said.Count > 6 && Whole(said[6]) != 0
            ? $", not {AppQuery.Short(element.Element)}: window {Id(done.Window)} is exclusive, so it takes every key until it closes" : string.Empty;

    private async Task<string> GameScrollAsync(GodotGame game, string? control, double down, double across, double? x, double? y, string? window, IReadOnlyList<string>? modifiers)
    {
        using IDisposable asking = GodotGame.Within(GameLimit);
        (List<AppNode> nodes, AppNode? element, AppNode target) = await GameTarget(game, control, window, needsPoint: true, x, y);
        InputDone done = GameDone(nodes, target, await game.AskAsync("scroll", element?.Element.Id ?? 0L, target.Element.Id, x is not null && y is not null, x ?? 0, y ?? 0, across, down,
            GameModifiers(modifiers)));
        string turned = Plural.Listed([.. ((string[])[Notches(down, "down", "up"), Notches(across, "right", "left")]).Where(n => n.Length > 0)]);
        string scrolled = $"Scrolled {turned} over {Aimed(element, done)}{Took(element, done, "wheel")}";
        return done.Shows is { } where ? $"{scrolled}: {where}" : $"{scrolled}, which no ScrollContainer holds";
    }
}
