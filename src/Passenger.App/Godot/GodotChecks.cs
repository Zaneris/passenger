using Passenger.Roslyn.Extension;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>A Godot project's C# file read against its scenes: the project's folder and scenes as they stood, what the
/// extension was asked of the code, and what it answered.</summary>
internal sealed record GodotRead(string Folder, GodotScenes Scenes, GodotCodeRequest Asked, GodotCodeFound Found);

/// <summary>What a Godot project's C# file and its scenes say of each other, its code read by Passenger's extension
/// (<see cref="GodotSource"/>) and its scenes by Passenger's own reader (<see cref="GodotScenes"/>): what to ask the
/// extension of the project's types for the scenes the file's scripts run in (<see cref="Asks"/>), and what is wrong
/// (<see cref="Findings"/>): a GetNode path no node answers in any scene a script runs in, one whose node is not of the
/// type asked for, a res:// path no file answers, a scene's connection to a method the script lacks, or that takes other
/// parameters than its signal gives, a property a scene sets that the script lacks, a string Godot's calls take naming a
/// member a script lacks (a C# script's, or the GDScript of the node the code tells, <see cref="Members"/>) or connecting
/// one of other arguments, and one naming what the project's files do not define
/// (<see cref="Named"/>); each with what it offers to mend it (<see cref="GodotOffer"/>). Pure: it reads only what it is
/// given (and whether a file is there).</summary>
internal static class GodotChecks
{
    /// <summary>One finding: where it is in the file (the server's places), whether it is an error, its words, and what it
    /// offers to mend it.</summary>
    public sealed record Finding(Place At, Place End, bool Error, string Message)
    {
        public GodotOffer? Offer { get; init; }
    }

    /// <summary>A connection a scene writes, with the nodes its tree finds for its ends; null for an end it finds none of.</summary>
    public sealed record Connected(string Scene, GodotConnection Connection, GodotTreeNode? From, GodotTreeNode? To);

    /// <summary>What to ask of the extension for a file whose code runs as some scripts: every engine class and C# script of
    /// the scenes they run in, and the signals and methods their connections to those scripts' nodes name.</summary>
    public static GodotCodeRequest Asks(string path, GodotScenes scenes, IEnumerable<string> scripts)
    {
        HashSet<string> engine = new(StringComparer.Ordinal);
        HashSet<string> written = new(StringComparer.Ordinal);
        List<GodotMemberAsked> signals = [];
        List<GodotMemberAsked> methods = [];
        List<GodotMemberAsked> properties = [];
        HashSet<string> runs = new(StringComparer.Ordinal);
        foreach (string script in scripts.Distinct(StringComparer.Ordinal))
        {
            foreach ((string scene, GodotTreeNode node) in scenes.RunsOf(script))
            {
                properties.AddRange(Set(node).Select(p => new GodotMemberAsked(node.Type, script, p.Property.Key)));
                if (!runs.Add(scene))
                {
                    continue;
                }
                foreach (GodotTreeNode below in node.Root.Below())
                {
                    if (below.Type is { } type)
                    {
                        engine.Add(type);
                    }
                    if (IsCSharp(below.Script))
                    {
                        written.Add(below.Script!);
                    }
                }
            }
            foreach (Connected connected in runs.SelectMany(scene => Connections(scenes, scene)).Where(c => c.To?.Script == script))
            {
                signals.Add(SignalAsked(connected));
                methods.Add(MethodAsked(connected));
            }
        }
        // An autoload's node, which a GetNode path through /root names, is its script's class or its scene's tree.
        foreach (GodotDefined autoload in scenes.Defined.Autoloads)
        {
            string? loaded = scenes.AutoloadFile(autoload.Name);
            if (IsCSharp(loaded))
            {
                written.Add(loaded!);
            }
            foreach (GodotTreeNode below in loaded is not null && GodotProject.IsScene(loaded) && scenes.Tree(loaded) is { } tree ? tree.Below() : [])
            {
                if (below.Type is { } type)
                {
                    engine.Add(type);
                }
                if (IsCSharp(below.Script))
                {
                    written.Add(below.Script!);
                }
            }
        }
        return new GodotCodeRequest(path, [.. engine], [.. written], [.. signals.Distinct()], [.. methods.Distinct()]) { Properties = [.. properties.Distinct()] };
    }

    /// <summary>The properties the scenes set on a node of a C# script that are a script's own to give: written by a name that
    /// begins with a capital (an engine class's properties are snake_case), no path in it; each with the scene and section
    /// that sets it.</summary>
    public static IEnumerable<(string Scene, Passenger.Workspace.GodotNode Section, GodotEntry Property)> Set(GodotTreeNode node)
    {
        if (!IsCSharp(node.Script))
        {
            yield break;
        }
        foreach ((string scene, Passenger.Workspace.GodotNode section) in node.Written)
        {
            foreach (GodotEntry property in section.Section.Properties)
            {
                if (property.Key.Length > 0 && char.IsUpper(property.Key[0]) && !property.Key.Contains('/'))
                {
                    yield return (scene, section, property);
                }
            }
        }
    }

    /// <summary>What to ask of the extension for a scene's own connections, through a C# file of its project
    /// (<paramref name="path"/>): every engine class and C# script of its tree, and each connection's signal and method.</summary>
    public static GodotCodeRequest SceneAsks(string path, GodotScenes scenes, string scene)
    {
        IReadOnlyList<GodotTreeNode> nodes = scenes.Tree(scene) is { } root ? [.. root.Below()] : [];
        List<Connected> connections = [.. Connections(scenes, scene)];
        return new GodotCodeRequest(path, [.. nodes.Select(n => n.Type).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. nodes.Select(n => n.Script).Where(IsCSharp).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. connections.Select(SignalAsked).Distinct()], [.. connections.Select(MethodAsked).Distinct()])
        {
            Properties = [.. nodes.SelectMany(n => Set(n).Where(s => s.Scene == scene).Select(s => new GodotMemberAsked(n.Type, n.Script, s.Property.Key))).Distinct()],
        };
    }

    /// <summary>One finding in a scene's own text: where (offsets of the file), whether it is an error, its words, and what
    /// it offers to mend it.</summary>
    public sealed record SceneFinding(int Start, int End, bool Error, string Message)
    {
        public GodotOffer? Offer { get; init; }
    }

    /// <summary>What is wrong in a scene or resource as Godot would load it, by what the extension answered of its connections
    /// and properties (<see cref="SceneAsks"/>): an external resource whose file is not there, an ExtResource or SubResource no
    /// resource of the file has, a node whose parent is not in it, a connection's end no node answers, or whose method or signal
    /// its node lacks (a GDScript's as its scripts declare them, <see cref="Lacks"/>), or of other arguments, a property a
    /// node's C# script lacks, or a node path it sets naming no node or one of another type.</summary>
    public static IReadOnlyList<SceneFinding> SceneFindings(GodotScenes scenes, string scene, GodotCodeRequest asked, GodotCodeFound found, Func<string, bool> exists)
    {
        List<SceneFinding> findings = [];
        if (!scenes.Files.TryGetValue(scene, out GodotFile? file))
        {
            return findings;
        }
        foreach (GodotExternal external in file.Externals)
        {
            string? byUid = external.Uid is { } uid && scenes.ResOfUid(uid) is { } res && scenes.FileOf(res) is { } loaded && exists(loaded) ? res : null;
            if (external.Path is { } path && scenes.FileOf(path) is { } named && !exists(named) && external.Section.Attribute("path")?.Value is { } value)
            {
                // Godot loads an external resource by its uid where one names a file, by its path only where none does.
                findings.Add(byUid is null ? new SceneFinding(value.TextStart, value.TextEnd, true, $"No file {path} is in the Godot project")
                    : new SceneFinding(value.TextStart, value.TextEnd, false, $"No file {path} is in the Godot project: Godot loads {external.Uid}, {byUid}, by its uid"));
            }
            else if (byUid is not null && external.Path is { } written && written != byUid && external.Section.Attribute("uid")?.Value is { } uidValue)
            {
                findings.Add(new SceneFinding(uidValue.TextStart, uidValue.TextEnd, false, $"{external.Uid} is {byUid}, which Godot loads in place of {written}"));
            }
        }
        HashSet<string> subResources = [.. file.Sections.Where(s => s.Tag == "sub_resource").Select(s => s.Text("id")).OfType<string>()];
        foreach (GodotSection section in file.Sections)
        {
            foreach (GodotEntry entry in section.Attributes.Concat(section.Properties))
            {
                if (entry.Value is { Call: "ExtResource", Text: { } id } value && file.External(id) is null && section.Tag != "ext_resource")
                {
                    findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No ext_resource of this file has the id \"{id}\""));
                }
                foreach ((string sub, int start, int end) in file.CallsIn(entry.Value, "SubResource").Where(c => !subResources.Contains(c.Argument)))
                {
                    findings.Add(new SceneFinding(start, end, true, $"No sub_resource of this file has the id \"{sub}\""));
                }
            }
        }
        if (scenes.Tree(scene) is not { } root)
        {
            return findings;
        }
        foreach (Passenger.Workspace.GodotNode node in file.Nodes)
        {
            if (node.Parent is { } parent && GodotScenes.Resolve(root, parent) is { Found: null, Unknown: false } && node.Section.Attribute("parent")?.Value is { } value)
            {
                findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No node '{parent}' is in the scene, so Godot leaves {node.Name} out"));
            }
        }
        foreach (Connected connected in Connections(scenes, scene))
        {
            GodotConnection connection = connected.Connection;
            foreach ((string key, string path, GodotTreeNode? end) in new[] { ("from", connection.From, connected.From), ("to", connection.To, connected.To) })
            {
                if (end is null && !GodotScenes.Resolve(root, path).Unknown && connection.Section.Attribute(key)?.Value is { } value)
                {
                    findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No node '{path}' is in the scene"));
                }
            }
            int methodAt = Known(connected.To) ? IndexOf(asked.Methods, MethodAsked(connected)) : -1;
            int signalAt = Known(connected.From) ? IndexOf(asked.Signals, SignalAsked(connected)) : -1;
            bool methodKnown = methodAt >= 0 && methodAt < found.Methods.Count;
            bool signalKnown = signalAt >= 0 && signalAt < found.Signals.Count;
            GodotSignature? method = methodKnown ? found.Methods[methodAt] : null;
            GodotSignature? signal = signalKnown ? found.Signals[signalAt] : null;
            if (methodKnown && method is null && connection.Section.Attribute("method")?.Value is { } named)
            {
                findings.Add(new SceneFinding(named.TextStart, named.TextEnd, true, $"{connected.To!.Name}, {Article(Shown(connected.To))}, has no method {connection.Method}")
                {
                    Offer = HandlerOffer(scenes, connected, signal, file),
                });
            }
            else if (!methodKnown && connected.To is { } receiver && Lacks(scenes, receiver, GodotScriptKind.Function, connection.Method, EngineHas(asked.Methods, found.Methods, MethodAsked(connected)))
                is { } methods && connection.Section.Attribute("method")?.Value is { } scripted)
            {
                findings.Add(new SceneFinding(scripted.TextStart, scripted.TextEnd, true, $"{receiver.Name}, {Article(Shown(receiver))}, has no method {connection.Method}")
                {
                    Offer = new GodotOffer
                    {
                        Near = Core.Spelling.Near(connection.Method, methods),
                        Function = ScriptHandler(scenes, connected, SignalOf(scenes, asked, found, connected), file),
                    },
                });
            }
            else if (!methodKnown && connected.To is { } handler && ChainOf(scenes, handler)?.Find(GodotScriptKind.Function, connection.Method) is (_, { Parameters: not null } function)
                && SignalOf(scenes, asked, found, connected) is { } carried && connection.Section.Attribute("method")?.Value is { } handled)
            {
                int passed = carried.Count + connection.Bound - connection.Unbinds;
                if (!function.Takes(passed))
                {
                    findings.Add(new SceneFinding(handled.TextStart, handled.TextEnd, true,
                        $"{connection.Signal} {Gives(passed, carried.Select(p => p.Type.Length == 0 ? "Variant" : p.Type).Concat(Enumerable.Repeat("bound", Math.Max(0, connection.Bound))))}, but {connection.Method} {Takes(function)}"));
                }
            }
            if (signalKnown && signal is null && connection.Section.Attribute("signal")?.Value is { } emitted)
            {
                findings.Add(new SceneFinding(emitted.TextStart, emitted.TextEnd, true, $"{connected.From!.Name}, {Article(Shown(connected.From))}, has no signal {connection.Signal}"));
            }
            else if (!signalKnown && connected.From is { } sender && Lacks(scenes, sender, GodotScriptKind.Signal, connection.Signal, EngineHas(asked.Signals, found.Signals, SignalAsked(connected)))
                is { } signals && connection.Section.Attribute("signal")?.Value is { } declaring)
            {
                findings.Add(new SceneFinding(declaring.TextStart, declaring.TextEnd, true, $"{sender.Name}, {Article(Shown(sender))}, has no signal {connection.Signal}")
                {
                    Offer = new GodotOffer { Near = Core.Spelling.Near(connection.Signal, signals) },
                });
            }
            int given = (signal?.Parameters.Count ?? 0) + connection.Bound - connection.Unbinds;
            if (method is not null && signal is not null && (given < method.Required || given > method.Parameters.Count) && connection.Section.Attribute("method")?.Value is { } called)
            {
                findings.Add(new SceneFinding(called.TextStart, called.TextEnd, true,
                    $"{connection.Signal} {Gives(given, signal.Parameters.Concat(Enumerable.Repeat("bound", Math.Max(0, connection.Bound))))}, but {connection.Method} {Takes(method.Parameters)}"));
            }
        }
        foreach (GodotTreeNode node in root.Below())
        {
            foreach ((string where, Passenger.Workspace.GodotNode section, GodotEntry property) in Set(node).Where(s => s.Scene == scene))
            {
                int at = IndexOf(asked.Properties, new GodotMemberAsked(node.Type, node.Script, property.Key));
                if (at >= 0 && at < found.Properties.Count && found.Properties[at] is null)
                {
                    findings.Add(new SceneFinding(property.KeyStart, property.KeyStart + property.Key.Length, true,
                        $"{node.Name}, {Article(Shown(node))}, has no property {property.Key}, so Godot drops its value")
                    {
                        Offer = ExportOffer(scenes, node, file, property),
                    });
                }
                else if (at >= 0 && at < found.Properties.Count && found.Properties[at] is { Node: { } wanted } && NodeSet(node, property, wanted, found) is { } finding)
                {
                    findings.Add(finding);
                }
            }
        }
        return findings;

        // A node whose members the code says: one with no script, of a known engine class, or one with a C# script.
        static bool Known(GodotTreeNode? node) => node is not null && (node.Script is null ? node.Type is not null : IsCSharp(node.Script));
    }

    /// <summary>What the engine's classes answer of a scene's connections and nodes where no C# is read with it (a game of
    /// GDScript alone, a scene of no C# script), from the GodotSharp the Godot found carries (<see cref="GodotEngineClasses"/>):
    /// each engine class's chain, and each signal and method asked of a node's engine class, its script none of C#'s.</summary>
    public static GodotCodeFound EngineAnswers(GodotCodeRequest asked, GodotEngineClasses engine)
    {
        if (!engine.Any)
        {
            return GodotCodeFound.None;
        }
        List<GodotChain> chains = [.. asked.Engine.Where(engine.Knows).Select(e => new GodotChain(e, engine.Chain(e), e, e))];
        GodotSignature? Signal(GodotMemberAsked one) => one is { Script: null, Engine: { } type } && engine.Signal(type, one.Name) is { } signal
            ? new GodotSignature(one.Name, [.. signal.Parameters.Select(p => p.Type.Length == 0 ? "Variant" : p.Type)], signal.Parameters.Count, null, null, null)
            {
                Names = [.. signal.Parameters.Select(p => p.Name)],
            }
            : null;
        GodotSignature? Method(GodotMemberAsked one) => one is { Script: null, Engine: { } type } && engine.Method(type, one.Name) is { } method
            ? new GodotSignature(one.Name, [.. Enumerable.Repeat("Variant", Math.Min(method.Parameters, 32))], method.Required, null, null, null)
            : null;
        return new GodotCodeFound([], [], [], chains, [], [.. asked.Signals.Select(Signal)], [.. asked.Methods.Select(Method)]);
    }

    /// <summary>Whether a node's engine class has a member, as the extension answered for it; null where it was not asked.</summary>
    private static bool? EngineHas(IReadOnlyList<GodotMemberAsked> asked, IReadOnlyList<GodotSignature?> found, GodotMemberAsked one) =>
        IndexOf(asked, one) is int at and >= 0 && at < found.Count ? found[at] is not null : null;

    /// <summary>The class of a node whose script is GDScript, or that has none (<see cref="GodotScriptChain"/>); null for a
    /// node of a C# script, or of another kind of script.</summary>
    public static GodotScriptChain? ChainOf(GodotScenes scenes, GodotTreeNode node) =>
        node.Script is null ? GodotScriptChain.Of(node.Type) : GodotScriptDeclarations.IsScript(node.Script) ? scenes.ChainOf(node.Script) : null;

    /// <summary>The functions the scenes' connections of a GDScript's signal (of <paramref name="script"/>, a res:// path) call,
    /// from nodes whose class takes in its script, by the script each runs and its name; and the connections a change of the
    /// signal's parameters can't follow: one binding values, one to a node no GDScript runs, one whose handler a connection of
    /// any other signal calls too, as another signal is another declaration, whatever it is called.</summary>
    public static (IReadOnlyList<GDScriptHandler> Handlers, IReadOnlyList<GDScriptPlace> Unfollowed) SignalHandlers(GodotScenes scenes, string script, string signal)
    {
        List<GDScriptHandler> handlers = [];
        List<GDScriptPlace> unfollowed = [];
        List<Connected> all = [.. scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").SelectMany(f => Connections(scenes, f.Key))];
        bool OfIt(Connected connected) => connected.Connection.Signal == signal && connected.From is { } from && ChainOf(scenes, from)?.Runs(script) == true;
        foreach (Connected connected in all.Where(OfIt))
        {
            int line = scenes.Files[connected.Scene].Text[..connected.Connection.Section.Start].Count(c => c == '\n') + 1;
            GDScriptPlace at = new($"{Path.GetFileName(scenes.FileOf(connected.Scene) ?? connected.Scene)}:{line}", 0, 0, "a scene's connection names it");
            string method = connected.Connection.Method;
            if (connected.Connection.Bound > 0 || connected.Connection.Unbinds > 0)
            {
                unfollowed.Add(at with { What = "a scene's connection binds values to its handler" });
            }
            else if (connected.To?.Script is not { } handlerScript || !GodotScriptDeclarations.IsScript(handlerScript))
            {
                unfollowed.Add(at with { What = $"a scene's connection calls {method} on a node no GDScript runs" });
            }
            else if (all.Any(other => other.Connection.Method == method && other.To?.Script == handlerScript && !OfIt(other)))
            {
                unfollowed.Add(at with { What = $"its handler {method} is connected to another signal too" });
            }
            else
            {
                handlers.Add(new GDScriptHandler(handlerScript, method, at));
            }
        }
        return (handlers, unfollowed);
    }

    /// <summary>The names of a kind a node of GDScript, or of no script, has, where it lacks the one asked for: none of its
    /// scripts declares it and they are all its class has beside its engine class, which lacks it too, as the extension
    /// answered (<paramref name="engine"/>), or, where none answered, as no engine method begins with '_' and a connection's
    /// handler that does is a script's; null where it has it, or what it has is not known.</summary>
    private static IReadOnlyList<string>? Lacks(GodotScenes scenes, GodotTreeNode node, GodotScriptKind kind, string name, bool? engine)
    {
        if (ChainOf(scenes, node) is not { Whole: true } chain || chain.Find(kind, name) is not null)
        {
            return null;
        }
        return engine == false || engine is null && kind == GodotScriptKind.Function && name.StartsWith('_') ? chain.Names(kind) : null;
    }

    /// <summary>Where a GDScript's class declares a member of a kind (it or a script it extends): the script's file and the
    /// place of its name there, as the project's texts have it; null for none.</summary>
    public static (string File, Passenger.Text.TextPosition At)? ScriptPlace(GodotScenes scenes, string script, GodotScriptKind kind, string name) =>
        scenes.ChainOf(script).Find(kind, name) is ({ } res, { } member) && scenes.FileOf(res) is { } file && scenes.Texts.TryGetValue(res, out string? text)
            ? (file, Passenger.Text.TextSnapshot.From(text).GetPosition(member.Start)) : null;

    /// <summary>A scene's value of a script's property that names a node by its path (an exported NodePath, a node's type)
    /// that no node answers from the node it is set on, or that names a node not of the property's type: at the path, as
    /// the script's file says it at the member; null where it holds, or the scene does not say.</summary>
    private static SceneFinding? NodeSet(GodotTreeNode node, GodotEntry property, GodotTypeRef wanted, GodotCodeFound found)
    {
        if (property.Value is not { Call: "NodePath", Text: { Length: > 0 } path } value || GodotScenes.Resolve(node, path) is not { Unknown: false } resolved)
        {
            return null;
        }
        if (resolved.Found is not { } target)
        {
            (int start, int end) = resolved.Missing!.Value;
            string name = path[start..end];
            string under = resolved.Reached?.Name ?? node.Name;
            return new SceneFinding(value.TextStart, value.TextEnd, true,
                $"{node.Name}'s {property.Key} names no node: {under} has no {(name.StartsWith('%') ? $"unique node {name[1..]}" : $"child {name}")}");
        }
        return Fits(wanted, target, found) == false
            ? new SceneFinding(value.TextStart, value.TextEnd, true, $"{node.Name}'s {property.Key} takes {Article(wanted.Shown)}: '{path}' is {Article(Shown(target))}")
            : null;
    }

    public static GodotMemberAsked SignalAsked(Connected connected) =>
        new(connected.From?.Type, IsCSharp(connected.From?.Script) ? connected.From!.Script : null, connected.Connection.Signal);

    public static GodotMemberAsked MethodAsked(Connected connected) =>
        new(connected.To?.Type, IsCSharp(connected.To?.Script) ? connected.To!.Script : null, connected.Connection.Method);

    /// <summary>A scene's own connections, each with the nodes its tree finds for its ends.</summary>
    public static IEnumerable<Connected> Connections(GodotScenes scenes, string scene)
    {
        if (!scenes.Files.TryGetValue(scene, out GodotFile? file) || scenes.Tree(scene) is not { } root)
        {
            yield break;
        }
        foreach (GodotConnection connection in file.Connections)
        {
            yield return new Connected(scene, connection, GodotScenes.Resolve(root, connection.From).Found, GodotScenes.Resolve(root, connection.To).Found);
        }
    }

    /// <summary>Whether a script is C#'s, which the extension reads.</summary>
    public static bool IsCSharp(string? script) => script?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>What is wrong between a file and the scenes its scripts run in, by what the extension answered of it.</summary>
    public static IReadOnlyList<Finding> Findings(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, Func<string, bool> exists)
    {
        List<Finding> findings = [];
        foreach (GodotNodeCall call in found.Calls)
        {
            if (Call(scenes, asked, found, call) is { } finding)
            {
                findings.Add(finding);
            }
        }
        foreach (GodotPathRead read in found.Resources)
        {
            string last = read.Path.TrimEnd('/').Split('/')[^1];
            string named = read.Path.Split("::")[0];
            if (named.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
            {
                // A uid none of the project's files has is known to name none only once Godot's editor has kept their cache.
                if (scenes.ResOfUid(named) is { } res ? scenes.FileOf(res) is { } loaded && !exists(loaded) : scenes.KeepsUids)
                {
                    findings.Add(new Finding(read.At, read.End, true, scenes.ResOfUid(named) is { } gone ? $"{named} is {gone}, which is not in the Godot project"
                        : $"No file of the Godot project has {named}"));
                }
                continue;
            }
            if ((read.Path.EndsWith('/') || last.Contains('.')) && scenes.FileOf(named) is { } file && !exists(file))
            {
                findings.Add(new Finding(read.At, read.End, true, $"No {(named.EndsWith('/') ? "folder" : "file")} {named} is in the Godot project"));
            }
        }
        foreach (GodotStringRead read in found.Strings)
        {
            if (StringFinding(found, read) is { } finding)
            {
                findings.Add(finding.Error && read.Found is null && read.Kind == GodotNameKind.Method ? finding with { Offer = CallableOffer(scenes, found, read) } : finding);
            }
            else if (ScriptStringFinding(scenes, asked, found, read) is { } scripted)
            {
                findings.Add(scripted);
            }
        }
        HashSet<(GodotStringKind, string)> added = [.. found.Added.Select(a => (a.Kind, a.Name)), .. found.Kinds.Where(k => k.Adds).Select(k => (k.Kind, k.Name))];
        foreach (GodotKindRead read in found.Kinds.Where(k => k.Exact && !k.Adds && !added.Contains((k.Kind, k.Name))))
        {
            Candidates named = Named(scenes, read.Kind, ScriptsOf(found, read.Class), read.Node, read.Export, read.Member);
            if (named.Whole && !named.Names.Any(n => n.Name == read.Name) && !named.Others.Contains(read.Name))
            {
                bool later = named.Later.TryGetValue(read.Name, out Version? from);
                findings.Add(new Finding(read.At, read.End, !later, $"No {What(read.Kind)} '{read.Name}' is {named.Where}{(later ? $": the project is of Godot {named.Godot}, and Godot {from} added it" : string.Empty)}")
                {
                    Offer = new GodotOffer { Near = Core.Spelling.Near(read.Name, named.Names.Select(n => n.Name)), Defines = Defines(scenes, read, ScriptsOf(found, read.Class)) },
                });
            }
        }
        foreach (GodotNodeCall call in found.Calls)
        {
            if (AutoloadFinding(scenes, found, call) is { } finding)
            {
                findings.Add(finding);
            }
        }
        foreach (GodotClassFound type in found.Classes)
        {
            findings.AddRange(Exports(scenes, asked, found, type));
        }
        foreach (GodotClassFound type in found.Classes)
        {
            findings.AddRange(Properties(scenes, asked, found, type));
        }
        for (int i = 0; i < found.Classes.Count; i++)
        {
            GodotClassFound type = found.Classes[i];
            HashSet<string> runs = new(StringComparer.Ordinal);
            foreach (string script in type.Scripts)
            {
                foreach (string scene in scenes.RunsOf(script).Select(r => r.Scene).Where(runs.Add))
                {
                    foreach (Connected connected in Connections(scenes, scene).Where(c => c.To?.Script == script))
                    {
                        if (Connection(scenes, asked, found, type, connected) is { } finding)
                        {
                            findings.Add(finding);
                        }
                    }
                }
            }
        }
        return findings;
    }

    /// <summary>A GetNode call's finding: no node answers its path in any scene it runs in that says (a GetNodeOrNull's never
    /// counts), or a node it names in one is not of the type asked for; null where every scene's node fits or none says.</summary>
    private static Finding? Call(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotNodeCall call)
    {
        if (call.Class >= found.Classes.Count)
        {
            return null;
        }
        GodotClassFound type = found.Classes[call.Class];
        List<(string Scene, GodotTreeNode Node, GodotResolved Resolved)> said = [.. type.Scripts.SelectMany(scenes.RunsOf)
            .Select(r => (r.Scene, r.Node, Resolved: GodotScenes.Resolve(r.Node, call.Path))).Where(r => !r.Resolved.Unknown)];
        if (said.Count == 0)
        {
            return null;
        }
        if (said.All(s => s.Resolved.Found is null))
        {
            if (call.OrNull)
            {
                return null;
            }
            (string scene, GodotTreeNode node, GodotResolved resolved) = said.OrderByDescending(s => Attached(scenes, s.Scene, s.Node)).First();
            (int start, int end) = resolved.Missing!.Value;
            string name = call.Path[start..end];
            string under = resolved.Reached?.Name ?? node.Name;
            string lacking = start == 0 && end == call.Path.Split(':')[0].Length ? string.Empty
                : $": {under} has no {(name.StartsWith('%') ? $"unique node {name[1..]}" : $"child {name}")}";
            return new Finding(call.At, call.End, true, $"{Place(scenes, scene, node)} has no node '{call.Path}' under {node.Name}{lacking}")
            {
                Offer = call.Exact ? new GodotOffer { Near = NearPaths(found, call, [.. said.Select(s => s.Node)]) } : null,
            };
        }
        if (call.Type is not { } wanted)
        {
            return null;
        }
        foreach ((string scene, GodotTreeNode node, GodotResolved resolved) in said.Where(s => s.Resolved.Found is not null))
        {
            GodotTreeNode target = resolved.Found!;
            if (Fits(wanted, target, found) == false)
            {
                string shown = $"'{call.Path}' is {Article(Shown(target))} in {Place(scenes, target.Declared.Scene, target)}, not {Article(wanted.Shown)}";
                return new Finding(call.At, call.End, !call.OrNull, call.OrNull ? $"GetNodeOrNull<{wanted.Shown}> gives null: {shown}" : shown)
                {
                    Offer = TypeOffer(asked, found, call, said.Where(s => s.Resolved.Found is not null).Select(s => s.Resolved.Found!)),
                };
            }
        }
        return null;
    }

    /// <summary>A connection's finding in its receiving script's file: a method the script lacks (at the class's name), one
    /// whose parameters are not as many as the signal gives with its binds (at the method's name, where this file declares
    /// it), or a signal its sending node does not have; null where it holds, or what it needs is not known.</summary>
    private static Finding? Connection(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotClassFound type, Connected connected)
    {
        GodotConnection connection = connected.Connection;
        string where = $"{System.IO.Path.GetFileName(connected.Scene)}:{connection.Section.Line + 1}";
        string from = connection.From == "." ? connected.From?.Name ?? connection.From : connection.From;
        int methodAt = IndexOf(asked.Methods, MethodAsked(connected));
        int signalAt = IndexOf(asked.Signals, SignalAsked(connected));
        GodotSignature? method = methodAt >= 0 && methodAt < found.Methods.Count ? found.Methods[methodAt] : null;
        GodotSignature? signal = signalAt >= 0 && signalAt < found.Signals.Count ? found.Signals[signalAt] : null;
        if (methodAt < 0 || methodAt >= found.Methods.Count)
        {
            return null;
        }
        if (method is null)
        {
            return new Finding(type.At, type.End, true, $"{where} connects {from}'s {connection.Signal} to {connection.Method}, which {type.Name} has no method of")
            {
                Offer = scenes.Files.TryGetValue(connected.Scene, out GodotFile? file) ? HandlerOffer(scenes, connected, signal, file) : null,
            };
        }
        if (signal is null)
        {
            bool known = connected.From is { } sender && (sender.Script is null || IsCSharp(sender.Script)) && sender.Type is not null;
            return known && signalAt >= 0 && signalAt < found.Signals.Count
                ? new Finding(type.At, type.End, true, $"{where} connects {from}'s {connection.Signal}, a signal {from} ({Shown(connected.From!)}) has none of, to {connection.Method}")
                : null;
        }
        int given = signal.Parameters.Count + connection.Bound - connection.Unbinds;
        if (given >= method.Required && given <= method.Parameters.Count)
        {
            return null;
        }
        bool here = method.At is not null && method.Path is not null && Core.PathNames.SameFile(method.Path, asked.Path);
        string takes = Takes(method.Parameters);
        string gives = Gives(given, signal.Parameters.Concat(Enumerable.Repeat("bound", Math.Max(0, connection.Bound))));
        return new Finding(here ? method.At! : type.At, here ? method.End! : type.End, true, $"{where} connects {from}'s {connection.Signal}, which {gives}, to {connection.Method}, which {takes}");
    }

    /// <summary>Each scene's value of an exported member that names a node by its path (a NodePath, a node's type) that no
    /// node answers from the node the script runs on, or that names a node not of the member's type: at the member's name
    /// where this file declares it (else at its class's), naming the scene and line; a value set in one scene, read from each
    /// tree that holds it, said once.</summary>
    private static IEnumerable<Finding> Exports(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotClassFound type)
    {
        HashSet<(string, int)> said = [];
        foreach (GodotMember export in type.Exports.Where(e => e.Node is not null))
        {
            bool here = Core.PathNames.SameFile(export.Path, asked.Path);
            foreach ((string scene, GodotTreeNode node) in type.Scripts.SelectMany(scenes.RunsOf))
            {
                foreach ((string written, Passenger.Workspace.GodotNode section) in node.Written)
                {
                    if (section.Section.Property(export.Name) is not { Value: { Call: "NodePath", Text: { Length: > 0 } path } } property
                        || GodotScenes.Resolve(node, path) is not { Unknown: false } resolved || !scenes.Files.TryGetValue(written, out GodotFile? file))
                    {
                        continue;
                    }
                    string where = $"{System.IO.Path.GetFileName(written)}:{file.LineOf(property.KeyStart) + 1}";
                    string? message = null;
                    if (resolved.Found is not { } target)
                    {
                        (int start, int end) = resolved.Missing!.Value;
                        string name = path[start..end];
                        string under = resolved.Reached?.Name ?? node.Name;
                        message = $"{where} sets {export.Name} to '{path}', which names no node: {under} has no {(name.StartsWith('%') ? $"unique node {name[1..]}" : $"child {name}")}";
                    }
                    else if (Fits(export.Node!, target, found) == false)
                    {
                        message = $"{where} sets {export.Name} to '{path}', {Article(Shown(target))}, not {Article(export.Node!.Shown)}";
                    }
                    if (message is not null && said.Add((written, property.KeyStart)))
                    {
                        yield return new Finding(here ? export.At : type.At, here ? export.End : type.End, true, message);
                    }
                }
            }
        }
    }

    /// <summary>A string Godot's calls take naming a member: one a script's class lacks, a signal an EmitSignal gives other
    /// arguments than it carries, or a method connected by a Connect to a signal named by a string that takes other
    /// arguments than the signal gives; null where it holds, or what it names is not known (an engine class's object, which
    /// a script may give more).</summary>
    private static Finding? StringFinding(GodotCodeFound found, GodotStringRead read)
    {
        if (read.Found is not { } member)
        {
            string what = read.Kind switch { GodotNameKind.Signal => "signal", GodotNameKind.Method => "method", _ => "property" };
            return read.Script ? new Finding(read.At, read.End, true, $"{read.On} has no {what} '{read.Name}'") : null;
        }
        if (read is { Kind: GodotNameKind.Signal, Given: { } given } && (given < member.Required || given > member.Parameters.Count))
        {
            return new Finding(read.At, read.End, true, $"'{read.Name}' {Takes(member.Parameters)}, but this EmitSignal gives it {(given == 0 ? "none" : given.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        if (read.Signal is { } index && index < found.Strings.Count && found.Strings[index].Found is { } signal
            && (signal.Parameters.Count < member.Required || signal.Parameters.Count > member.Parameters.Count))
        {
            return new Finding(read.At, read.End, true, $"'{found.Strings[index].Name}', which {Gives(signal.Parameters.Count, signal.Parameters)}, is connected to '{read.Name}', which {Takes(member.Parameters)}");
        }
        return null;
    }

    private static string Gives(int given, IEnumerable<string> types) => given == 0 ? "gives no arguments" : $"gives {given} ({string.Join(", ", types.Take(given))})";

    private static string Takes(IReadOnlyList<string> parameters) => parameters.Count == 0 ? "takes none" : $"takes {parameters.Count} ({string.Join(", ", parameters)})";

    /// <summary>What a GDScript function takes in words: its count, at least its required one past a rest parameter.</summary>
    private static string Takes(GodotScriptDeclared function) => function.Most is null
        ? $"takes at least {function.Required} ({string.Join(", ", function.Parameters ?? [])})"
        : Takes(function.Parameters ?? []);

    private static int IndexOf(IReadOnlyList<GodotMemberAsked> asked, GodotMemberAsked one)
    {
        for (int i = 0; i < asked.Count; i++)
        {
            if (asked[i] == one)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>What a string of a kind may name, as Godot looks for it: the names defined, the words saying where Godot looks
    /// (after "No input action 'jump' is"), and whether those are all it may name, so a name none of them is is known
    /// missing (not where the object the call is made on is not told, or what defines its names is not all read).</summary>
    public sealed record Candidates(IReadOnlyList<GodotDefined> Names, string Where, bool Whole)
    {
        /// <summary>The names it may name beside those offered, so none is marked missing: a theme item of a class deriving
        /// from the one the code knows the node as, or one the project's themes or scenes give another type.</summary>
        public IReadOnlySet<string> Others { get; init; } = new HashSet<string>();

        /// <summary>The names only a Godot after the project's has, each with the first that does: not offered, and a warning
        /// where written, as the Godot named is the one that last opened the project, not sure to be the one that runs it.</summary>
        public IReadOnlyDictionary<string, Version> Later { get; init; } = new Dictionary<string, Version>();

        /// <summary>The Godot the names are of, where the project names one.</summary>
        public Version? Godot { get; init; }
    }

    /// <summary>A kind as its words name it.</summary>
    public static string What(GodotStringKind kind) => kind switch
    {
        GodotStringKind.Action => "input action",
        GodotStringKind.Group => "group",
        GodotStringKind.Animation => "animation",
        GodotStringKind.Autoload => "autoload",
        GodotStringKind.ShaderParameter => "shader parameter",
        GodotStringKind.Layer => "layer",
        GodotStringKind.TreeParameter => "AnimationTree parameter",
        GodotStringKind.ShaderGlobal => "global shader uniform",
        GodotStringKind.ThemeColor => "theme color",
        GodotStringKind.ThemeConstant => "theme constant",
        GodotStringKind.ThemeFont => "theme font",
        GodotStringKind.ThemeFontSize => "theme font size",
        GodotStringKind.ThemeIcon => "theme icon",
        GodotStringKind.ThemeStyle => "theme stylebox",
        _ => "translation",
    };

    /// <summary>Whether a kind is a theme item's.</summary>
    public static bool IsTheme(GodotStringKind kind) => kind is >= GodotStringKind.ThemeColor and <= GodotStringKind.ThemeStyle;

    /// <summary>A theme item's kind as a theme resource names it (<see cref="GodotThemeTable.Kinds"/>).</summary>
    public static string ThemeKind(GodotStringKind kind) => kind switch
    {
        GodotStringKind.ThemeColor => "colors",
        GodotStringKind.ThemeConstant => "constants",
        GodotStringKind.ThemeFont => "fonts",
        GodotStringKind.ThemeFontSize => "font_sizes",
        GodotStringKind.ThemeIcon => "icons",
        _ => "styles",
    };

    /// <summary>A theme item's kind as Godot's class reference names its members (<c>theme_color</c>).</summary>
    public static string ThemeDocKind(GodotStringKind kind) => "theme_" + ThemeKind(kind) switch
    {
        "font_sizes" => "font_size",
        "styles" => "style",
        var plural => plural[..^1],
    };

    /// <summary>The scripts a class of the file runs as; none for a string written outside its classes.</summary>
    public static IReadOnlyList<string> ScriptsOf(GodotCodeFound found, int index) => index >= 0 && index < found.Classes.Count ? found.Classes[index].Scripts : [];

    /// <summary>What a string of a kind may name (<see cref="Candidates"/>), given in a class running as some scripts on an
    /// object the code tells by a node's path from the script's node or an exported member a scene sets to it (and for a
    /// shader parameter, the node's property holding its material; for a layer, the layers it is among): an animation
    /// player's or animated sprite's animations, an AnimationTree's parameters, a material's shader's uniforms, in each scene
    /// the script runs in; the project's for one not told.</summary>
    public static Candidates Named(GodotScenes scenes, GodotStringKind kind, IReadOnlyList<string> scripts, string? node, string? export, string? member)
        => Named(scenes, kind, scripts, node, export, member, []);

    /// <summary>What a string of a kind may name (<see cref="Named(GodotScenes, GodotStringKind, IReadOnlyList{string}, string?, string?, string?)"/>),
    /// with the names the open Godot editor answers for it joined, each the files do not give said to be the editor's.</summary>
    public static Candidates Named(GodotScenes scenes, GodotStringKind kind, IReadOnlyList<string> scripts, string? node, string? export, string? member, IReadOnlyList<string> editor)
    {
        Candidates read = FromFiles(scenes, kind, scripts, node, export, member);
        HashSet<string> named = [.. read.Names.Select(n => n.Name)];
        List<string> joined = [.. editor.Where(e => e.Length > 0 && named.Add(e))];
        // The editor answers Godot's own ui_ actions among the project's, a newer Godot's those Passenger does not list.
        return joined.Count == 0 ? read : read with
        {
            Names = [.. read.Names, .. joined.Select(e => new GodotDefined(e, kind == GodotStringKind.Action && e.StartsWith("ui_", StringComparison.Ordinal)
                ? "Godot's own input action" : $"{What(kind)} the open Godot editor holds", []))],
        };
    }

    private static Candidates FromFiles(GodotScenes scenes, GodotStringKind kind, IReadOnlyList<string> scripts, string? node, string? export, string? member)
    {
        GodotDefinitions defined = scenes.Defined;
        bool project = scenes.Texts.ContainsKey(GodotDefinitions.ProjectRes);
        if (IsTheme(kind))
        {
            return ThemeNamed(defined, kind, ThemeTypes(scenes, scripts, node, export, member));
        }
        switch (kind)
        {
            case GodotStringKind.Layer:
                return member is null ? new Candidates([], "among Godot's layers", false)
                    : new Candidates(defined.Layers(member), $"among Godot's {GodotDefinitions.LayersShown(member)} layers, numbered 1 to {GodotDefinitions.LayerCount(member)}", true);
            case GodotStringKind.ShaderGlobal:
                return new Candidates(defined.ShaderGlobals, "in project.godot's [shader_globals]", project);
            case GodotStringKind.Action:
                return new Candidates([.. defined.Actions, .. GodotDefinitions.BuiltInActions.Where(b => !defined.Actions.Any(a => a.Name == b)).Select(b => new GodotDefined(b, "Godot's own input action", []))],
                    "in project.godot's [input], nor among Godot's own ui_ actions", project);
            case GodotStringKind.Group:
                return new Candidates(defined.Groups, "given to a node by the project's scenes, nor added by its code", true);
            case GodotStringKind.Autoload:
                return new Candidates(defined.Autoloads, "in project.godot's [autoload]", project);
            case GodotStringKind.Translation:
                return new Candidates(defined.Translations, $"in the translations project.godot lists ({Core.Plural.Listed([.. defined.TranslationFiles.Select(f => f.Split('/')[^1])])})",
                    defined.TranslationFiles.Count > 0 && defined.TranslationsWhole);
        }
        (IReadOnlyList<GodotTreeNode> told, bool whole) = Told(scenes, scripts, node, export);
        string listed = Core.Plural.Listed([.. told.Select(p => $"{p.Name} ({Place(scenes, p.Declared.Scene, p)})").Distinct()]);
        if (kind == GodotStringKind.Animation)
        {
            if (!whole)
            {
                return new Candidates(defined.Animations, "in any animation library of the project's scenes", false);
            }
            List<GodotDefined> names = [];
            foreach (GodotTreeNode player in told)
            {
                (IReadOnlyList<GodotDefined> own, bool read) = defined.AnimationsOf(player);
                whole &= read && GodotDefinitions.IsPlayer(player);
                names.AddRange(own);
            }
            bool sprites = told.All(t => t.Written.Any(w => w.Node.Section.Property("sprite_frames") is not null));
            return new Candidates(Merged(names), $"in the {(sprites ? "SpriteFrames" : "animation libraries")} of {listed}", whole);
        }
        if (kind == GodotStringKind.TreeParameter)
        {
            List<GodotDefined> parameters = [];
            foreach (GodotTreeNode tree in whole ? told : scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").OrderBy(f => f.Key, StringComparer.Ordinal)
                .SelectMany(f => scenes.Tree(f.Key)?.Below() ?? []).Where(n => n.Type == "AnimationTree"))
            {
                (IReadOnlyList<GodotDefined> own, bool read) = defined.TreeParametersOf(tree);
                whole &= read && tree.Type == "AnimationTree";
                parameters.AddRange(own);
            }
            return new Candidates(Merged(parameters), whole ? $"made by the tree of {listed}" : "made by any AnimationTree of the project's scenes", whole);
        }
        List<string?> each = [.. whole && member is not null ? told.Select(t => defined.ShaderOf(t, member)) : []];
        if (!whole || member is null || each.Contains(null))
        {
            return new Candidates(Merged([.. defined.Shaders.SelectMany(s => defined.Uniforms(s).Names)]), "a uniform of any shader of the project", false);
        }
        List<string> shaders = [.. each.OfType<string>().Distinct(StringComparer.Ordinal)];
        List<GodotDefined> uniforms = [];
        foreach (string shader in shaders)
        {
            (IReadOnlyList<GodotDefined> own, bool read) = defined.Uniforms(shader);
            whole &= read;
            uniforms.AddRange(own);
        }
        return new Candidates(Merged(uniforms), $"a uniform of {Core.Plural.Listed([.. shaders.Select(GodotDefinitions.Shown)])}, the shader of {Core.Plural.Listed([.. told.Select(t => $"{t.Name}'s {member}")])}", whole);
    }

    /// <summary>The theme types a theme item's call looks its name up in, each with the types Godot looks in for it
    /// (<see cref="GodotDefinitions.ThemeChain"/>): the type it names (<paramref name="type"/>); for a call on a node the code
    /// tells (its class's own, <c>"."</c>, the node each scene runs its script on), each such node's class after the
    /// variation its scenes give it (<c>theme_type_variation</c>), the type named too where a scene tells none; none where
    /// neither is known.</summary>
    public static IReadOnlyList<(string Type, IReadOnlyList<string> Chain)> ThemeTypes(GodotScenes scenes, IReadOnlyList<string> scripts, string? node, string? export, string? type)
    {
        GodotDefinitions defined = scenes.Defined;
        (IReadOnlyList<GodotTreeNode> nodes, bool all) = node == "." ? ([.. scripts.SelectMany(scenes.RunsOf).Select(r => r.Node)], true)
            : node is not null || export is not null ? Told(scenes, scripts, node, export) : ([], true);
        List<(string, IReadOnlyList<string>)> types = [];
        foreach (GodotTreeNode told in nodes)
        {
            string of = told.Type ?? type ?? "Control";
            string? variation = told.Written.Select(w => w.Node.Section.Property("theme_type_variation")?.Value.Text).LastOrDefault(v => v is { Length: > 0 });
            IReadOnlyList<string> chain = defined.ThemeChain(of, variation);
            if (!types.Any(t => t.Item2.SequenceEqual(chain)))
            {
                types.Add((variation ?? of, chain));
            }
        }
        if (type is not null && (types.Count == 0 || !all) && defined.ThemeChain(type) is var named && !types.Any(t => t.Item2.SequenceEqual(named)))
        {
            types.Add((type, named));
        }
        return types;
    }

    /// <summary>The class whose page of Godot's class reference describes a theme item of a kind and name, the first of the
    /// types a call looks in (<see cref="ThemeTypes"/>) that does, with its words there; null where none does.</summary>
    public static (string Class, string Words)? ThemeDocumented(GodotScenes scenes, GodotStringKind kind, IReadOnlyList<string> scripts, string? node, string? export,
        string? member, string name)
    {
        GodotThemeTable table = scenes.Defined.Theme;
        foreach (string at in ThemeTypes(scenes, scripts, node, export, member).SelectMany(t => t.Chain).Distinct(StringComparer.Ordinal))
        {
            if (table.Described(at, ThemeKind(kind), name) is { } words)
            {
                return (at, words);
            }
        }
        return null;
    }

    /// <summary>Whether Godot's default theme gives a theme item of a kind and name to a type a call looks in.</summary>
    public static bool InDefaultTheme(GodotScenes scenes, GodotStringKind kind, IReadOnlyList<string> scripts, string? node, string? export, string? member, string name) =>
        ThemeTypes(scenes, scripts, node, export, member).SelectMany(t => t.Chain).Any(at => scenes.Defined.Theme.Items(at, ThemeKind(kind)).Contains(name));

    /// <summary>The scenes' overrides of a theme item (<c>theme_override_colors/accent</c>) on a node of a type that looks
    /// in a type the project's themes define it for at <paramref name="defined"/>: where each writes its name.</summary>
    public static IReadOnlyList<GodotPlace> ThemeOverridesOf(GodotScenes scenes, GodotStringKind kind, string name, IReadOnlyList<GodotPlace> defined)
    {
        GodotDefinitions definitions = scenes.Defined;
        string items = ThemeKind(kind);
        HashSet<string> types = [.. definitions.ThemeItems.Where(i => i.Kind == items && i.Name == name && defined.Any(p => p.Res == i.Place.Res && p.Start == i.Place.Start)).Select(i => i.Type)];
        return [.. definitions.ThemeOverrides.Where(o => o.Kind == items && o.Name == name && o.Type.Length > 0 && definitions.ThemeChain(o.Type).Any(types.Contains)).Select(o => o.Place)];
    }

    /// <summary>What a theme item's name may be in the theme types the call looks it up in (<see cref="ThemeTypes"/>): the items
    /// of each type Godot looks in that the project's themes and the default theme of the Godot it names give, its custom theme's
    /// first; as <see cref="Candidates.Others"/>, the items of every class deriving from one, which the node may be, and those the
    /// project's themes and scenes give any type; as <see cref="Candidates.Later"/>, those only a later Godot gives. Known whole
    /// only for types the carried table or a theme knows, of a Godot the carried table tells.</summary>
    private static Candidates ThemeNamed(GodotDefinitions defined, GodotStringKind kind, IReadOnlyList<(string Type, IReadOnlyList<string> Chain)> types)
    {
        string what = What(kind);
        if (types.Count == 0)
        {
            return new Candidates([], $"among a control's {what}s", false);
        }
        GodotThemeTable table = defined.Theme;
        string items = ThemeKind(kind);
        List<string> chain = [.. types.SelectMany(t => t.Chain).Distinct(StringComparer.Ordinal)];
        List<GodotDefined> names = [];
        foreach (GodotThemeItem item in defined.ThemeItems.Where(i => i.Kind == items && chain.Contains(i.Type)))
        {
            names.Add(new GodotDefined(item.Name, $"{item.Type}'s {what}", [item.Place]));
        }
        foreach (string at in chain)
        {
            names.AddRange(table.Items(at, items).Select(n => new GodotDefined(n, $"{at}'s {what} in Godot's default theme", [])));
        }
        List<string> below = [.. types.SelectMany(t => table.Below(t.Type))];
        HashSet<string> others = [.. below.SelectMany(b => table.Items(b, items)), .. chain.Concat(below).SelectMany(t => table.Removed(t, items)),
            .. defined.ThemeItems.Concat(defined.ThemeOverrides).Where(i => i.Kind == items).Select(i => i.Name)];
        Dictionary<string, Version> later = new(StringComparer.Ordinal);
        foreach ((string name, Version from) in chain.Concat(below).SelectMany(t => table.Later(t, items)).OrderBy(l => l.Value))
        {
            later.TryAdd(name, from);
        }
        string themes = defined.ThemeItems.Count > 0 ? "Godot's default theme or the project's themes" : "Godot's default theme";
        string named = Core.Plural.Listed([.. types.Select(t => $"{t.Type}'s")]).Replace(" and ", " or ", StringComparison.Ordinal);
        bool whole = table.IsWhole && types.All(t => table.Knows(t.Type) || defined.ThemeVariations.ContainsKey(t.Type));
        return new Candidates(Merged(names), $"among {named} {what}{(what.EndsWith('x') ? "es" : "s")} in {themes}", whole) { Others = others, Later = later, Godot = table.Godot };
    }

    /// <summary>The nodes an object a string's call is made on is, as the code tells it (by a node's path from the node its
    /// class runs on, or by an exported member a scene sets to one), in each scene the class's scripts run in, each once;
    /// and whether every such scene tells one.</summary>
    public static (IReadOnlyList<GodotTreeNode> Told, bool Whole) Told(GodotScenes scenes, IReadOnlyList<string> scripts, string? node, string? export)
    {
        List<GodotTreeNode> told = [];
        bool whole = node is not null || export is not null;
        foreach ((string scene, GodotTreeNode runner) in scripts.SelectMany(scenes.RunsOf))
        {
            string? path = node ?? (export is null ? null : runner.Written.Select(w => w.Node.Section.Property(export)?.Value).LastOrDefault(v => v is not null) is { Call: "NodePath", Text: { } set } ? set : null);
            if (path is not null && GodotScenes.Resolve(runner, path).Found is { } target)
            {
                if (!told.Any(t => t.Same(target)))
                {
                    told.Add(target);
                }
            }
            else
            {
                whole = false;
            }
        }
        return (told, whole && told.Count > 0);
    }

    /// <summary>What a string naming a member of a kind may name on an engine class's object the code tells a node of
    /// (<see cref="Told"/>), as the GDScript each such node runs declares (<see cref="GodotScriptChain"/>): its names of the
    /// kind (a property's its variables and constants), each where its script declares it, the nodes in words, and whether
    /// those are all it may name: each node told in every scene, each of GDScript or of no script, its scripts all read, and
    /// its engine class known to lack it (<paramref name="engine"/>: true where it has it, null where not known).</summary>
    public static Candidates Members(GodotScenes scenes, GodotNameKind kind, IReadOnlyList<string> scripts, string? node, string? export, Func<string?, bool?> engine)
    {
        (IReadOnlyList<GodotTreeNode> told, bool whole) = Told(scenes, scripts, node, export);
        GodotScriptKind[] kinds = kind switch
        {
            GodotNameKind.Method => [GodotScriptKind.Function],
            GodotNameKind.Signal => [GodotScriptKind.Signal],
            _ => [GodotScriptKind.Variable, GodotScriptKind.Constant],
        };
        List<GodotDefined> names = [];
        foreach (GodotTreeNode target in told)
        {
            GodotScriptChain? chain = ChainOf(scenes, target);
            whole &= chain is { Whole: true } && engine(target.Type) == false;
            if (chain is null)
            {
                continue;
            }
            foreach ((string script, GodotScriptDeclarations declared) in chain.Scripts)
            {
                string shown = script.Split('/')[^1];
                Passenger.Text.TextSnapshot? lines = scenes.Texts.TryGetValue(script, out string? text) ? Passenger.Text.TextSnapshot.From(text) : null;
                foreach (GodotScriptDeclared member in declared.Members.Where(m => kinds.Contains(m.Kind)))
                {
                    int line = lines is null ? 1 : lines.GetLineFromOffset(member.Start) + 1;
                    names.Add(new GodotDefined(member.Name, $"{Word(member.Kind)} of {shown}", [new GodotPlace(script, member.Start, member.Start + member.Name.Length, true, $"{shown}:{line}")]));
                }
            }
        }
        string where = string.Join(" or ", told.Select(t => $"{t.Name}, {Article(Shown(t))}"));
        return new Candidates(Merged(names), where, whole && told.Count > 0);

        static string Word(GodotScriptKind kind) => kind switch
        {
            GodotScriptKind.Function => "function",
            GodotScriptKind.Signal => "signal",
            GodotScriptKind.Variable => "variable",
            _ => "constant",
        };
    }

    /// <summary>A string naming a member an engine class's object lacks, on a node of GDScript the code tells it is: one no
    /// script of any node it is declares (<see cref="Members"/>), with the names near it; null where one may have it.</summary>
    private static Finding? ScriptStringFinding(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotStringRead read)
    {
        if (read.Found is not null || read.Script || read.Node is null && read.Export is null)
        {
            return null;
        }
        // The engine class of each node, where the extension was asked of it, has the member as it answered.
        Candidates members = Members(scenes, read.Kind, ScriptsOf(found, read.Class), read.Node, read.Export,
            type => type is not null && asked.Engine.Contains(type) ? read.Engines.Contains(type) : null);
        if (members.Names.Any(n => n.Name == read.Name))
        {
            return read switch
            {
                { Kind: GodotNameKind.Method, Given: { } given } => CountFinding(scenes, read, given),
                { Kind: GodotNameKind.Method } when (read.Connected ?? (read.Signal is { } at && at < found.Strings.Count ? found.Strings[at].Found : null)) is { } signal
                    => ConnectedFinding(scenes, read, signal),
                { Kind: GodotNameKind.Signal, Handler: { } handler } => HandlerFinding(scenes, members, read, handler),
                _ => null,
            };
        }
        if (!members.Whole)
        {
            return null;
        }
        string what = read.Kind switch { GodotNameKind.Signal => "signal", GodotNameKind.Method => "method", _ => "property" };
        return new Finding(read.At, read.End, true, $"{members.Where}, has no {what} '{read.Name}'")
        {
            Offer = read.Exact ? new GodotOffer { Near = Core.Spelling.Near(read.Name, members.Names.Select(n => n.Name)) } : null,
        };
    }

    /// <summary>A C# Call of a GDScript function by its string giving it a count no script declaring it takes; null where one
    /// of those it may call takes it, or none is known.</summary>
    private static Finding? CountFinding(GodotScenes scenes, GodotStringRead read, int given)
    {
        IReadOnlyList<GodotScriptDeclared> functions = Declarations(scenes, GodotScriptKind.Function, read.Name);
        if (functions.Count == 0 || functions.Any(f => f.Takes(given)))
        {
            return null;
        }
        return new Finding(read.At, read.End, true,
            $"'{read.Name}' {Takes(functions[0])}, but this call gives it {(given == 0 ? "none" : given.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
    }

    /// <summary>A C# Connect of a C# signal to a GDScript function a Callable names by its string, where no script declaring
    /// the function takes what the signal gives; null where one does, or none is known.</summary>
    private static Finding? ConnectedFinding(GodotScenes scenes, GodotStringRead read, GodotSignature signal)
    {
        IReadOnlyList<GodotScriptDeclared> functions = Declarations(scenes, GodotScriptKind.Function, read.Name);
        if (functions.Count == 0 || functions.Any(f => f.Takes(signal.Parameters.Count)))
        {
            return null;
        }
        return new Finding(read.At, read.End, true, $"'{signal.Name}', which {Gives(signal.Parameters.Count, signal.Parameters)}, is connected to '{read.Name}', which {Takes(functions[0])}");
    }

    /// <summary>A C# Connect of a GDScript signal, by its string on a node of GDScript, to a C# method that takes other
    /// arguments than every script declaring the signal says it gives; null where one fits, or none says.</summary>
    private static Finding? HandlerFinding(GodotScenes scenes, Candidates members, GodotStringRead read, GodotSignature handler)
    {
        HashSet<string> scripts = [.. members.Names.Where(n => n.Name == read.Name).SelectMany(n => n.Places).Select(p => p.Res)];
        List<GodotScriptDeclared> signals = [.. scripts.Select(s => scenes.ScriptOf(s)?.Member(GodotScriptKind.Signal, read.Name)).OfType<GodotScriptDeclared>()];
        if (signals.Count == 0 || signals.Any(s => s.Parameters is not { } given || given.Count >= handler.Required && given.Count <= handler.Parameters.Count))
        {
            return null;
        }
        IReadOnlyList<string> gives = signals[0].Parameters!;
        return new Finding(read.At, read.End, true, $"'{read.Name}', which {Gives(gives.Count, gives)}, is connected to '{handler.Name}', which {Takes(handler.Parameters)}");
    }

    /// <summary>Every declaration of a kind and name the project's GDScripts hold.</summary>
    private static IReadOnlyList<GodotScriptDeclared> Declarations(GodotScenes scenes, GodotScriptKind kind, string name) =>
        [.. scenes.Texts.Keys.Where(GodotScriptDeclarations.IsScript).Select(s => scenes.ScriptOf(s)?.Member(kind, name)).OfType<GodotScriptDeclared>().Where(d => d.Parameters is not null)];

    /// <summary>Names of several players or shaders, each name once with every place of it.</summary>
    private static IReadOnlyList<GodotDefined> Merged(IEnumerable<GodotDefined> names) =>
        [.. names.GroupBy(n => n.Name, StringComparer.Ordinal).Select(g => new GodotDefined(g.Key, g.First().Detail, [.. g.SelectMany(n => n.Places).DistinctBy(p => (p.Res, p.Start))]))];

    /// <summary>The autoload a node path names through <c>/root/Name</c>, and where the name lies in the path; null for any other path.</summary>
    public static (string Name, int Start, int End)? AutoloadIn(string path)
    {
        const string root = "/root/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return null;
        }
        int end = path.IndexOfAny(['/', ':'], root.Length);
        end = end < 0 ? path.Length : end;
        return end > root.Length ? (path[root.Length..end], root.Length, end) : null;
    }

    /// <summary>A GetNode path through <c>/root/Name</c>'s finding: a name no autoload has and no scene's root is named
    /// (the scene running is one of /root's children by its root's name), a node under an autoload's scene its tree lacks
    /// (a GetNodeOrNull's of either never counts), or an autoload's node not of the type asked for; null where it holds or
    /// is not known.</summary>
    private static Finding? AutoloadFinding(GodotScenes scenes, GodotCodeFound found, GodotNodeCall call)
    {
        if (!call.Exact || AutoloadIn(call.Path) is not ({ } name, _, int end) || !scenes.Texts.ContainsKey(GodotDefinitions.ProjectRes))
        {
            return null;
        }
        string? loaded = scenes.AutoloadFile(name);
        if (loaded is null)
        {
            bool aRoot = scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").Any(f => scenes.Tree(f.Key)?.Name == name);
            int start = AutoloadIn(call.Path)!.Value.Start;
            return aRoot || call.OrNull ? null : new Finding(call.At, call.End, true, $"No autoload '{name}' is in project.godot's [autoload], nor is a scene's root node so named")
            {
                Offer = call.Exact ? new GodotOffer
                {
                    Near = [.. Core.Spelling.Near(name, scenes.Defined.Autoloads.Select(a => a.Name)).Select(n => call.Path[..start] + n + call.Path[end..])],
                    Defines = AutoloadDefines(scenes, name),
                } : null,
            };
        }
        string rest = call.Path[end..].TrimStart('/');
        if (GodotProject.IsScene(loaded) && scenes.Tree(loaded) is { } tree)
        {
            GodotResolved resolved = GodotScenes.Resolve(tree, rest);
            if (resolved is { Found: null, Unknown: false, Missing: ({ } start, { } stop) })
            {
                return call.OrNull ? null : new Finding(call.At, call.End, true, $"{Place(scenes, loaded, tree)} has no node '{rest}' under {tree.Name}, the autoload {name}: {resolved.Reached?.Name ?? tree.Name} has no child {rest[start..stop]}");
            }
            if (resolved.Found is { } target && call.Type is { } wanted && Fits(wanted, target, found) == false)
            {
                return new Finding(call.At, call.End, !call.OrNull, $"'{call.Path}' is {Article(Shown(target))} in {Place(scenes, target.Declared.Scene, target)}, not {Article(wanted.Shown)}");
            }
            return null;
        }
        if (rest.Split(':')[0].Length == 0 && call.Type is { Engine: false } type && IsCSharp(loaded) && found.Scripts.FirstOrDefault(s => s?.Name == loaded) is { } script
            && !script.Chain.Contains(type.Name))
        {
            return new Finding(call.At, call.End, !call.OrNull, $"The autoload {name} is {Article(script.Shown)} ({loaded.Split('/')[^1]}), not {Article(type.Shown)}");
        }
        return null;
    }

    /// <summary>Each scene's property set on a node a class runs as that its script has none of, a value Godot drops: at the
    /// class's name, naming the scene and line, said once for each section that sets it.</summary>
    private static IEnumerable<Finding> Properties(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotClassFound type)
    {
        HashSet<(string, int)> said = [];
        foreach (string script in type.Scripts)
        {
            foreach ((_, GodotTreeNode node) in scenes.RunsOf(script))
            {
                foreach ((string written, _, GodotEntry property) in Set(node))
                {
                    int at = IndexOf(asked.Properties, new GodotMemberAsked(node.Type, script, property.Key));
                    if (at < 0 || at >= found.Properties.Count || found.Properties[at] is not null || !scenes.Files.TryGetValue(written, out GodotFile? file)
                        || !said.Add((written, property.KeyStart)))
                    {
                        continue;
                    }
                    yield return new Finding(type.At, type.End, true,
                        $"{System.IO.Path.GetFileName(written)}:{file.LineOf(property.KeyStart) + 1} sets {property.Key} on {node.Name}, which {type.Name} has no property of")
                    {
                        Offer = ExportOffer(scenes, node, file, property),
                    };
                }
            }
        }
    }

    /// <summary>The parameters a connection's signal carries, by name and GDScript's type (none untyped): its sending node's
    /// GDScript's declaration of it where one declares it, else its engine class's as answered; null where not known.</summary>
    private static IReadOnlyList<(string Name, string Type)>? SignalOf(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, Connected connected)
    {
        if (connected.From is { } sender && ChainOf(scenes, sender)?.Find(GodotScriptKind.Signal, connected.Connection.Signal) is (_, { Parameters: { } declared }))
        {
            return [.. declared.Select(p => p.Split(':', '=') is [{ } named, { } typed, ..] && p.Contains(':', StringComparison.Ordinal) ? (named.Trim(), typed.Trim()) : (p.Split('=')[0].Trim(), string.Empty))];
        }
        int at = IndexOf(asked.Signals, SignalAsked(connected));
        return at >= 0 && at < found.Signals.Count && found.Signals[at] is { } signal
            ? [.. signal.Parameters.Select((type, i) => (i < signal.Names.Count ? Snake(signal.Names[i]) : $"arg{i}", ScriptType(type)))]
            : null;
    }

    /// <summary>The function a connection's receiving node's GDScript lacks, made in its script: the signal's parameters less those
    /// the connection unbinds, then the sending node where its flags bind it and one for each value it binds, typed as GDScript
    /// writes them; null for a node of no GDScript.</summary>
    private static GodotScriptFunction? ScriptHandler(GodotScenes scenes, Connected connected, IReadOnlyList<(string Name, string Type)>? signal, GodotFile file)
    {
        if (connected.To?.Script is not { } script || !GodotScriptDeclarations.IsScript(script) || !scenes.Texts.ContainsKey(script))
        {
            return null;
        }
        GodotConnection connection = connected.Connection;
        IReadOnlyList<string> binds = connection.Section.Attribute("binds")?.Value is { } bound ? [.. Items(file.Written(bound)).Select(v => TypeOfValue(v) ?? string.Empty)] : [];
        IEnumerable<(string Name, string Type)> given = signal is null ? [] : signal.Take(Math.Max(0, signal.Count - connection.Unbinds));
        IEnumerable<(string Name, string Type)> source = connection.AppendsSource ? [("source", connected.From?.Type ?? string.Empty)] : [];
        IEnumerable<(string Name, string Type)> extra = source.Concat(binds.Select((type, i) => ($"extra_arg_{i}", type)));
        return new GodotScriptFunction(script, connection.Method, [.. given.Concat(extra).Select(p => p.Type.Length == 0 ? p.Name : $"{p.Name}: {p.Type}")]);
    }

    /// <summary>A C# type as GDScript writes it: C#'s numbers, strings and truths by GDScript's names, a variant untyped (empty).</summary>
    internal static string ScriptType(string type) => type switch
    {
        "long" or "int" or "uint" or "ulong" or "short" or "ushort" or "byte" or "sbyte" => "int",
        "double" or "float" => "float",
        "string" => "String",
        "bool" => "bool",
        "Variant" or "object" => string.Empty,
        "GodotObject" => "Object",
        "Rid" => "RID",
        "Aabb" => "AABB",
        _ => type.Contains('<', StringComparison.Ordinal) ? "Array" : type.Split('.')[^1],
    };

    /// <summary>A C# parameter's name as GDScript writes one: snake_case (<c>newText</c>'s <c>new_text</c>).</summary>
    internal static string Snake(string name)
    {
        System.Text.StringBuilder written = new();
        foreach (char c in name.TrimStart('@'))
        {
            if (char.IsUpper(c) && written.Length > 0)
            {
                written.Append('_');
            }
            written.Append(char.ToLowerInvariant(c));
        }
        return written.ToString();
    }

    /// <summary>The handler a connection's receiving C# script lacks, made in its class: the signal's parameters less those the
    /// connection unbinds, then the sending node where its flags bind it and a parameter for each value it binds, beside the
    /// other handlers the scene connects to the script.</summary>
    private static GodotOffer? HandlerOffer(GodotScenes scenes, Connected connected, GodotSignature? signal, GodotFile file)
    {
        if (connected.To?.Script is not { } script || !IsCSharp(script) || scenes.FileOf(script) is not { } code)
        {
            return null;
        }
        GodotConnection connection = connected.Connection;
        IEnumerable<string> source = connection.AppendsSource ? [connected.From is { Script: { } sender } && IsCSharp(sender) ? sender : connected.From?.Type ?? "Node"] : [];
        IReadOnlyList<string> binds = [.. source, .. connection.Section.Attribute("binds")?.Value is { } bound ? Items(file.Written(bound)).Select(v => TypeOfValue(v) ?? "Variant") : []];
        IReadOnlyList<string> given = signal is null ? [] : [.. signal.Parameters.Take(Math.Max(0, signal.Parameters.Count - connection.Unbinds))];
        return new GodotOffer
        {
            Make = new GodotFixRequest(code, script, GodotFixKind.Handler, connection.Method)
            {
                Signal = SignalAsked(connected),
                Binds = binds,
                Unbinds = connection.Unbinds,
                Beside = HandlersOf(scenes, script, connection.Method),
            },
            Shown = $"{connection.Method}({string.Join(", ", given.Concat(binds.Select(TypeShown)))})",
        };
    }

    /// <summary>The method a Callable names by a string that a script's class of the file lacks, made in it: taking what the
    /// signal a Connect joins it to gives, where its string names one the code knows, else nothing.</summary>
    private static GodotOffer? CallableOffer(GodotScenes scenes, GodotCodeFound found, GodotStringRead read)
    {
        if (!read.Exact || !read.Script || ClassNamed(found, read.On) is not { Scripts: [{ } script, ..] } || scenes.FileOf(script) is not { } code)
        {
            return null;
        }
        GodotStringRead? joined = read.Signal is { } index && index < found.Strings.Count && found.Strings[index].Found is not null ? found.Strings[index] : null;
        GodotMemberAsked? sender = joined is null ? null
            : joined.Script ? ClassNamed(found, joined.On) is { Scripts: [{ } own, ..] } ? new GodotMemberAsked(null, own, joined.Name) : null
            : new GodotMemberAsked(joined.On == "GodotObject" ? "Object" : joined.On, null, joined.Name);
        IReadOnlyList<string> given = sender is null ? [] : joined!.Found!.Parameters;
        return new GodotOffer
        {
            Make = new GodotFixRequest(code, script, GodotFixKind.Handler, read.Name) { Signal = sender, Beside = HandlersOf(scenes, script, read.Name) },
            Shown = $"{read.Name}({string.Join(", ", given)})",
        };

        static GodotClassFound? ClassNamed(GodotCodeFound found, string name) => found.Classes.FirstOrDefault(c => c.Name == name || c.FullName == name);
    }

    /// <summary>The methods the scenes connect to a script's nodes but one, which a new handler is put beside.</summary>
    private static IReadOnlyList<string> HandlersOf(GodotScenes scenes, string script, string but) =>
        [.. scenes.RunsOf(script).Select(r => r.Scene).Distinct(StringComparer.Ordinal).SelectMany(scene => Connections(scenes, scene))
            .Where(c => c.To?.Script == script && c.Connection.Method != but).Select(c => c.Connection.Method).Distinct(StringComparer.Ordinal)];

    /// <summary>The exported property a scene sets that a node's C# script lacks, made in its class of the type the scene's value is.</summary>
    private static GodotOffer? ExportOffer(GodotScenes scenes, GodotTreeNode node, GodotFile file, GodotEntry property)
    {
        if (node.Script is not { } script || !IsCSharp(script) || scenes.FileOf(script) is not { } code)
        {
            return null;
        }
        string type = TypeOfValue(node, file, property.Value) ?? "Variant";
        return new GodotOffer
        {
            Make = new GodotFixRequest(code, script, GodotFixKind.Export, property.Key) { Type = type },
            Shown = $"{property.Key} ({TypeShown(type)})",
        };
    }

    /// <summary>The GetNode type every scene's node is, which a call asking another changes to: a C# script's class (by its
    /// res://), else the node's engine class; none where the scenes' nodes differ.</summary>
    private static GodotOffer? TypeOffer(GodotCodeRequest asked, GodotCodeFound found, GodotNodeCall call, IEnumerable<GodotTreeNode> targets)
    {
        List<(string? Type, string? Shown)> types = [.. targets.Select(t => IsCSharp(t.Script) && found.Scripts.FirstOrDefault(s => s?.Name == t.Script) is { } script ? (t.Script, script.Shown)
            : t.Type is { } engine && found.Engine.FirstOrDefault(e => e.Name == engine) is { } chain ? (engine, chain.Shown) : (null, null)).Distinct()];
        return types is [{ Type: { } type, Shown: { } shown }]
            ? new GodotOffer { Make = new GodotFixRequest(asked.Path, string.Empty, GodotFixKind.NodeType, call.Path) { Type = type, At = call.At }, Shown = shown }
            : null;
    }

    /// <summary>The paths near one no node answers, from the nodes a script runs on: a node's whose name is near the last name
    /// of the path or whose whole path is near it, of the type it asks for where it asks one; nearest first, three at most.</summary>
    private static IReadOnlyList<string> NearPaths(GodotCodeFound found, GodotNodeCall call, IReadOnlyList<GodotTreeNode> runners)
    {
        string path = call.Path.Split(':')[0];
        string rest = call.Path[path.Length..];
        string last = path.TrimEnd('/').Split('/')[^1].TrimStart('%').ToLowerInvariant();
        string folded = path.ToLowerInvariant();
        int most = last.Length <= 3 ? 1 : 2;
        return [.. runners.SelectMany(GodotScenes.PathsFrom)
            .Where(p => p.Path != path && (call.Type is not { } wanted || Fits(wanted, p.Node, found) != false))
            .Select(p => (p.Path, Whole: Core.Spelling.Distance(p.Path.ToLowerInvariant(), folded), Named: Core.Spelling.Distance(p.Node.Name.ToLowerInvariant(), last)))
            .Where(p => Math.Min(p.Whole, p.Named) <= most)
            .OrderBy(p => Math.Min(p.Whole, p.Named)).ThenBy(p => p.Whole).ThenBy(p => p.Path, StringComparer.Ordinal)
            .Select(p => p.Path + rest).Distinct(StringComparer.Ordinal).Take(3)];
    }

    /// <summary>Where a name of a kind the project's files lack would be defined: an input action in project.godot's [input];
    /// an autoload in its [autoload], of a file named as it is (<see cref="AutoloadDefines"/>); a group given to the node the
    /// call is made on where the code tells it, else to the script's own, in each scene it runs in (three at most).</summary>
    private static IReadOnlyList<GodotDefine> Defines(GodotScenes scenes, GodotKindRead read, IReadOnlyList<string> scripts)
    {
        bool project = scenes.Texts.ContainsKey(GodotDefinitions.ProjectRes);
        switch (read.Kind)
        {
            case GodotStringKind.Action:
                return project && read.Name.Length > 0 ? [new GodotDefine(read.Kind, read.Name, GodotDefinitions.ProjectRes) { Section = "input" }] : [];
            case GodotStringKind.Autoload:
                return AutoloadDefines(scenes, read.Name);
            case GodotStringKind.Group:
                List<GodotDefine> defines = [];
                foreach ((string scene, GodotTreeNode runner) in scripts.SelectMany(scenes.RunsOf))
                {
                    GodotTreeNode? node = read.Node is { } path ? GodotScenes.Resolve(runner, path).Found : read.Export is null ? runner : null;
                    if (node is null || read.Name.Length == 0)
                    {
                        continue;
                    }
                    (string written, Passenger.Workspace.GodotNode section) = node.Written.LastOrDefault(w => w.Scene == scene) is { Node: not null } own ? own : node.Declared;
                    if (!defines.Any(d => d.File == written && d.NodePath == section.PathInScene))
                    {
                        defines.Add(new GodotDefine(read.Kind, read.Name, written) { NodePath = section.PathInScene, NodeName = node.Name });
                    }
                }
                return [.. defines.Take(3)];
            default:
                return [];
        }
    }

    /// <summary>An autoload project.godot lacks defined of each of the project's scripts and scenes named as it is, letter case
    /// aside: a scene first, then a script.</summary>
    private static IReadOnlyList<GodotDefine> AutoloadDefines(GodotScenes scenes, string name) =>
        !scenes.Texts.ContainsKey(GodotDefinitions.ProjectRes) || name.Length == 0 ? []
            : [.. scenes.Known.Where(res => res.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || res.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
                    || IsCSharp(res) || res.EndsWith(".gd", StringComparison.OrdinalIgnoreCase))
                .Where(res => System.IO.Path.GetFileNameWithoutExtension(res.Split('/')[^1]).Replace("_", string.Empty, StringComparison.Ordinal)
                    .Equals(name.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
                .OrderBy(res => GodotProject.IsScene(res) ? 0 : 1).ThenBy(res => res, StringComparer.Ordinal).Take(3)
                .Select(res => new GodotDefine(GodotStringKind.Autoload, name, GodotDefinitions.ProjectRes) { Section = "autoload", Value = res })];

    /// <summary>The Godot type a scene's value is, for a member to hold it: an external or sub-resource's own type, a node
    /// path's node's (its C# script's res://, else its engine class), else as its text says (<see cref="TypeOfValue(string)"/>).</summary>
    internal static string? TypeOfValue(GodotTreeNode node, GodotFile file, GodotValue value) => value switch
    {
        { Call: "ExtResource", Text: { } id } => file.External(id)?.Type is { Length: > 0 } type ? type : null,
        { Call: "SubResource", Text: { } id } => file.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == id)?.Text("type"),
        { Call: "NodePath", Text: { Length: > 0 } path } when GodotScenes.Resolve(node, path).Found is { } target =>
            (IsCSharp(target.Script) ? target.Script : target.Type) ?? "NodePath",
        _ => TypeOfValue(file.Written(value)),
    };

    /// <summary>The Godot type a value's text is: bool, int, float, String, StringName, NodePath, Array, Dictionary, or the
    /// call it is written as (<c>Vector2(1, 2)</c>'s Vector2); null for null, or a text not read.</summary>
    internal static string? TypeOfValue(string written)
    {
        string text = written.Trim();
        if (text.Length == 0 || text == "null")
        {
            return null;
        }
        if (text is "true" or "false")
        {
            return "bool";
        }
        if (text[0] == '"')
        {
            return "String";
        }
        if (text.StartsWith("&\"", StringComparison.Ordinal))
        {
            return "StringName";
        }
        if (text.StartsWith("^\"", StringComparison.Ordinal))
        {
            return "NodePath";
        }
        if (text[0] is '[' or '{')
        {
            return text[0] == '[' ? "Array" : "Dictionary";
        }
        if (long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return "int";
        }
        if (text is "inf" or "inf_neg" or "nan" || double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return "float";
        }
        int open = text.IndexOf('(', StringComparison.Ordinal);
        string call = open > 0 ? text[..open] : string.Empty;
        return call.StartsWith("Array[", StringComparison.Ordinal) ? "Array" : call.StartsWith("Dictionary[", StringComparison.Ordinal) ? "Dictionary"
            : call.Length > 0 && call.All(c => char.IsLetterOrDigit(c) || c == '_') ? call : null;
    }

    /// <summary>A Godot type as C# names it, for a fix's title: a script's by its file, a variant's as C# writes it.</summary>
    internal static string TypeShown(string type) => type switch
    {
        _ when type.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) => System.IO.Path.GetFileNameWithoutExtension(type.Split('/')[^1]),
        "String" => "string",
        "PackedStringArray" => "string[]",
        "PackedByteArray" => "byte[]",
        "PackedInt32Array" => "int[]",
        "PackedInt64Array" => "long[]",
        "PackedFloat32Array" => "float[]",
        "PackedFloat64Array" => "double[]",
        "AABB" => "Aabb",
        "RID" => "Rid",
        _ when type.StartsWith("Packed", StringComparison.Ordinal) && type.EndsWith("Array", StringComparison.Ordinal) && type.Length > "PackedArray".Length =>
            type["Packed".Length..^"Array".Length] + "[]",
        _ => type,
    };

    /// <summary>The values of an array as written, at its top: <c>[1, "a"]</c>'s <c>1</c> and <c>"a"</c>.</summary>
    internal static IReadOnlyList<string> Items(string written)
    {
        string text = written.Trim();
        if (text.Length < 2 || text[0] != '[')
        {
            return [];
        }
        List<string> items = [];
        int depth = 0;
        int start = 1;
        for (int at = 1; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '"')
            {
                for (at++; at < text.Length && text[at] != '"'; at++)
                {
                    at += text[at] == '\\' ? 1 : 0;
                }
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && (c == ',' || c == ']'))
            {
                if (text[start..at].Trim() is { Length: > 0 } item)
                {
                    items.Add(item);
                }
                start = at + 1;
                if (c == ']')
                {
                    break;
                }
            }
        }
        return items;
    }

    /// <summary>Whether a node is of a type: its engine class's chain holds an engine type, its C# script's class's chain a
    /// type of the code; null where what it is is not known.</summary>
    public static bool? Fits(GodotTypeRef wanted, GodotTreeNode node, GodotCodeFound found)
    {
        if (wanted.Engine)
        {
            return node.Type is { } type && found.Engine.FirstOrDefault(e => e.Name == type) is { } chain ? chain.Chain.Contains(wanted.Name) : null;
        }
        if (node.Script is null)
        {
            return node.Type is null ? null : false;
        }
        if (!IsCSharp(node.Script))
        {
            return false;
        }
        return found.Scripts.FirstOrDefault(s => s?.Name == node.Script) is { } script ? script.Chain.Contains(wanted.Name) : null;
    }

    /// <summary>A node as its words name it: its engine class, with its script's file beside it.</summary>
    public static string Shown(GodotTreeNode node) =>
        node.Script is { } script ? $"{node.Type ?? "node"} ({script.Split('/')[^1]})" : node.Type ?? "node";

    private static string Article(string name) => (name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(name[0])) ? "an " : "a ") + name;

    /// <summary>Where a node is written in a scene, as its words name it: the scene's file and the line its section starts on.</summary>
    public static string Place(GodotScenes scenes, string scene, GodotTreeNode node)
    {
        (string declaring, Passenger.Workspace.GodotNode declared) = node.Written.LastOrDefault(w => w.Scene == scene) is { Node: not null } own ? own : node.Declared;
        return $"{System.IO.Path.GetFileName(declaring)}:{declared.Section.Line + 1}";
    }

    /// <summary>Whether a scene's own sections give a node its script, rather than an instance bringing it in.</summary>
    private static bool Attached(GodotScenes scenes, string scene, GodotTreeNode node) =>
        node.Written.Any(w => w.Scene == scene && scenes.Files.TryGetValue(scene, out GodotFile? file) && file.External(w.Node.Script)?.Path == node.Script);
}
