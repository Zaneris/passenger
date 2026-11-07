using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's completion from Passenger's own reading of it (<see cref="GDScriptAsked"/>): the names in scope with
/// the keywords and the engine's globals, a value's members after its dot, annotations, node paths from the scenes running
/// the script, the strings Godot's calls take by kind, the engine's virtual functions after <c>func</c>, and type names.</summary>
internal sealed partial class GDScriptAssist
{
    /// <summary>The keywords a statement begins with.</summary>
    private static readonly string[] StatementKeywords = ["if", "elif", "else", "for", "while", "match", "when", "return", "pass", "break", "continue", "var", "const", "breakpoint", "assert"];

    /// <summary>The keywords an expression holds.</summary>
    private static readonly string[] ExpressionKeywords = ["self", "super", "true", "false", "null", "not", "and", "or", "in", "is", "as", "await", "preload", "func", "PI", "TAU", "INF", "NAN"];

    /// <summary>The keywords a class's body declares with.</summary>
    private static readonly string[] ClassKeywords = ["func", "var", "const", "signal", "enum", "class", "class_name", "extends", "static"];

    /// <summary>The characters beyond a name's a node path or a string's text narrows by.</summary>
    private const string PathCharacters = "$%/.:-";

    /// <summary>The calls a GDScript gives a node's path from its own node to, at the argument that takes it.</summary>
    private static readonly Dictionary<string, int> NodePathCalls = new(StringComparer.Ordinal)
    {
        ["get_node"] = 0, ["get_node_or_null"] = 0, ["has_node"] = 0, ["get_node_and_resource"] = 0, ["has_node_and_resource"] = 0, ["NodePath"] = 0,
    };

    /// <summary>The calls a GDScript names a method, signal or variable of an object by, at the argument that names it, and which.</summary>
    private static readonly Dictionary<string, (int Argument, string Kind)> MemberCalls = new(StringComparer.Ordinal)
    {
        ["call"] = (0, "func"), ["call_deferred"] = (0, "func"), ["callv"] = (0, "func"), ["has_method"] = (0, "func"), ["rpc"] = (0, "func"), ["rpc_id"] = (1, "func"),
        ["Callable"] = (1, "func"), ["get_method_argument_count"] = (0, "func"), ["emit_signal"] = (0, "signal"), ["connect"] = (0, "signal"), ["disconnect"] = (0, "signal"),
        ["has_signal"] = (0, "signal"), ["is_connected"] = (0, "signal"), ["get_signal_connection_list"] = (0, "signal"), ["Signal"] = (1, "signal"),
        ["get"] = (0, "var"), ["set"] = (0, "var"), ["set_deferred"] = (0, "var"), ["get_indexed"] = (0, "var"), ["set_indexed"] = (0, "var"), ["tween_property"] = (1, "var"),
    };

    /// <summary>The calls a GDScript gives a name of a kind the project defines to, at the arguments that take it.</summary>
    private static readonly Dictionary<string, (GodotStringKind Kind, int[] Arguments)> KindCalls = new(StringComparer.Ordinal)
    {
        ["is_action_pressed"] = (GodotStringKind.Action, [0]), ["is_action_just_pressed"] = (GodotStringKind.Action, [0]), ["is_action_just_released"] = (GodotStringKind.Action, [0]),
        ["is_action_released"] = (GodotStringKind.Action, [0]), ["is_action"] = (GodotStringKind.Action, [0]), ["get_action_strength"] = (GodotStringKind.Action, [0]),
        ["get_action_raw_strength"] = (GodotStringKind.Action, [0]), ["action_press"] = (GodotStringKind.Action, [0]), ["action_release"] = (GodotStringKind.Action, [0]),
        ["get_axis"] = (GodotStringKind.Action, [0, 1]), ["get_vector"] = (GodotStringKind.Action, [0, 1, 2, 3]), ["has_action"] = (GodotStringKind.Action, [0]),
        ["erase_action"] = (GodotStringKind.Action, [0]), ["add_action"] = (GodotStringKind.Action, [0]), ["action_add_event"] = (GodotStringKind.Action, [0]),
        ["action_erase_event"] = (GodotStringKind.Action, [0]), ["action_erase_events"] = (GodotStringKind.Action, [0]), ["action_get_events"] = (GodotStringKind.Action, [0]),
        ["action_has_event"] = (GodotStringKind.Action, [0]), ["action_set_deadzone"] = (GodotStringKind.Action, [0]), ["action_get_deadzone"] = (GodotStringKind.Action, [0]),
        ["event_is_action"] = (GodotStringKind.Action, [1]),
        ["add_to_group"] = (GodotStringKind.Group, [0]), ["remove_from_group"] = (GodotStringKind.Group, [0]), ["is_in_group"] = (GodotStringKind.Group, [0]),
        ["get_nodes_in_group"] = (GodotStringKind.Group, [0]), ["get_first_node_in_group"] = (GodotStringKind.Group, [0]), ["get_node_count_in_group"] = (GodotStringKind.Group, [0]),
        ["has_group"] = (GodotStringKind.Group, [0]), ["call_group"] = (GodotStringKind.Group, [0]), ["notify_group"] = (GodotStringKind.Group, [0]), ["set_group"] = (GodotStringKind.Group, [0]),
        ["call_group_flags"] = (GodotStringKind.Group, [1]), ["notify_group_flags"] = (GodotStringKind.Group, [1]), ["set_group_flags"] = (GodotStringKind.Group, [1]),
        ["play"] = (GodotStringKind.Animation, [0]), ["play_backwards"] = (GodotStringKind.Animation, [0]), ["queue"] = (GodotStringKind.Animation, [0]),
        ["has_animation"] = (GodotStringKind.Animation, [0]), ["get_animation"] = (GodotStringKind.Animation, [0]), ["set_current_animation"] = (GodotStringKind.Animation, [0]),
        ["set_shader_parameter"] = (GodotStringKind.ShaderParameter, [0]), ["get_shader_parameter"] = (GodotStringKind.ShaderParameter, [0]),
        ["tr"] = (GodotStringKind.Translation, [0]), ["tr_n"] = (GodotStringKind.Translation, [0]), ["atr"] = (GodotStringKind.Translation, [0]), ["atr_n"] = (GodotStringKind.Translation, [0]),
        ["global_shader_parameter_set"] = (GodotStringKind.ShaderGlobal, [0]), ["global_shader_parameter_get"] = (GodotStringKind.ShaderGlobal, [0]),
        ["global_shader_parameter_set_override"] = (GodotStringKind.ShaderGlobal, [0]), ["global_shader_parameter_remove"] = (GodotStringKind.ShaderGlobal, [0]),
        ["global_shader_parameter_get_type"] = (GodotStringKind.ShaderGlobal, [0]),
    };

    /// <summary>A control's or window's call taking a theme item's name, and the item's kind.</summary>
    [GeneratedRegex("^(get|has|add|remove)_theme_(color|constant|font_size|font|icon|stylebox)(_override)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeCall();

    /// <summary>What an item of a GDScript's list, or a name hovered, names, which its card says.</summary>
    private abstract record Named;

    /// <summary>A local, a parameter or a member a script declares.</summary>
    private sealed record OfScript(GDScriptMemberInfo Member) : Named;

    /// <summary>An engine class's or built-in type's member, by the class that declares it (<c>@GlobalScope</c> for a utility
    /// function or a global constant), what it is and its type.</summary>
    private sealed record OfEngine(string Owner, string Kind, string Name, GDScriptType Type) : Named;

    /// <summary>A class by its name: the engine's, a built-in type, a project's <c>class_name</c>, an autoload.</summary>
    private sealed record OfClass(string Name, string Kind) : Named;

    /// <summary>A member of a node's C# script.</summary>
    private sealed record OfCSharp(GodotCSharpMember Member) : Named;

    /// <summary>A keyword, annotation, node or string the words say.</summary>
    private sealed record OfWords(string Said) : Named;

    /// <summary>What committing an item puts in (its name where null), how far back from the end the caret goes (inside a
    /// call's parentheses), and what of the text after the caret it replaces.</summary>
    private sealed record Offered(Named What, string? Insert = null, int Back = 0, Replacing Replaces = Replacing.Name);

    private enum Replacing
    {
        Name,
        Path,
        String,
    }

    /// <summary>A list made: where the name it completes starts, its items, and the characters beyond a name's it narrows by.</summary>
    private sealed record Listed(int Start, List<CompletionItem> Items, string WordCharacters = "");

    /// <summary>The engine's globals as items, made once for each API read: its classes, singletons, built-in types,
    /// utility functions, global constants and enums.</summary>
    private static readonly ConditionalWeakTable<GodotEngineApi, GlobalItems> Globals = [];

    /// <summary>The engine's globals as a name's list offers them, and as a type's.</summary>
    private sealed record GlobalItems(List<CompletionItem> Names, List<CompletionItem> Types);

    /// <summary>What each of the engine's global items names, for its card.</summary>
    private static readonly ConditionalWeakTable<CompletionItem, Named> GlobalsNamed = [];

    /// <summary>What the open list's own items name.</summary>
    private readonly Dictionary<CompletionItem, Offered> offered = [];

    /// <summary>What the engine's API calls the owner of its utility functions and global constants.</summary>
    private const string GlobalScope = "@GlobalScope";

    /// <summary>The script's reading the open list was made from.</summary>
    private GodotScriptTypes.Typed? listedFrom;

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        int caret = context.Caret;
        GodotScriptTypes.Typed? typed = await types.TypedAsync(shown);
        if (cancel.IsCancellationRequested || context.Editors.Active != shown)
        {
            return true;
        }
        if (shown.Text.Snapshot != asked)
        {
            // Typed on while the script was read: the list is made for the text as it stands now.
            return await CompleteAsync(trigger, incomplete);
        }
        GDScriptAsked at = typed is null ? new GDScriptAsked(GDScriptAskedKind.None, caret) : GDScriptAsked.At(typed.Text, caret);
        // A sigil typed opens only its own list: '%' takes a remainder, '/' divides, a quote opens any string.
        if (trigger is { } typedSign && !Opens(typedSign, at.Kind))
        {
            completion.Close();
            return true;
        }
        offered.Clear();
        Listed? listed = typed is null ? null : at.Kind switch
        {
            GDScriptAskedKind.Name => InScopeListed(typed, at, caret),
            GDScriptAskedKind.Member => await MembersListedAsync(shown, typed, at),
            GDScriptAskedKind.Annotation => new Listed(at.Start, [.. GDScriptAsked.Annotations(at.ClassLevel).Select(a => Item(a, CompletionItemKind.Keyword, "annotation", "0", new OfWords($"{a}, an annotation")))], "@"),
            GDScriptAskedKind.NodePath => await NodePathsListedAsync(shown, typed, at),
            GDScriptAskedKind.Type => TypesListed(typed, at),
            GDScriptAskedKind.Override => OverridesListed(typed, at),
            GDScriptAskedKind.String => await StringsListedAsync(shown, typed, at, caret),
            _ => null,
        };
        if (cancel.IsCancellationRequested || context.Editors.Active != shown)
        {
            return true;
        }
        if (shown.Text.Snapshot != asked)
        {
            return await CompleteAsync(trigger, incomplete);
        }
        if (listed is not { Items.Count: > 0 })
        {
            // What Passenger's reading has nothing for there, Godot's server is asked for where it serves.
            if (at.Kind != GDScriptAskedKind.None && context.WebOf(shown) is { Serving: true })
            {
                return false;
            }
            completion.Close();
            return true;
        }
        listedFrom = typed;
        // A list typed past before it opens comes narrowed, its first item chosen before it shows: its card shows as it opens.
        completion.Open(new CodeCompletion.Completion(listed.Items, listed.Start, [], false) { Own = this, WordCharacters = listed.WordCharacters, DescribedAsItOpens = true });
        return true;
    }

    /// <summary>Whether a character typed opens the list of a kind of place: a dot a member's, a sigil its own, a quote or a
    /// path's slash a string's.</summary>
    private static bool Opens(string trigger, GDScriptAskedKind kind) => trigger switch
    {
        "." => kind == GDScriptAskedKind.Member,
        "@" => kind == GDScriptAskedKind.Annotation,
        "$" or "%" => kind == GDScriptAskedKind.NodePath,
        "/" => kind is GDScriptAskedKind.NodePath or GDScriptAskedKind.String,
        "\"" or "'" => kind == GDScriptAskedKind.String,
        _ => true,
    };

    /// <summary>An item of the open list, with what it names, as its kind says and sorted by <paramref name="rank"/> first.</summary>
    private CompletionItem Item(string label, CompletionItemKind kind, string? detail, string rank, Named what, string? insert = null, int back = 0,
        Replacing replaces = Replacing.Name)
    {
        CompletionItem item = new() { Label = label, Kind = kind, Detail = detail, FilterText = label, SortText = rank + label };
        offered[item] = new Offered(what, insert, back, replaces);
        return item;
    }

    /// <summary>The names a place may use: the locals in scope there, its class's members, the keywords it may begin with, then
    /// the project's classes and autoloads and the engine's globals; at a class's level a declaration's keywords alone.</summary>
    private Listed InScopeListed(GodotScriptTypes.Typed typed, GDScriptAsked at, int caret)
    {
        List<CompletionItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (at.ClassLevel && at.StatementStart)
        {
            items.AddRange(ClassKeywords.Select(k => Item(k, CompletionItemKind.Keyword, "keyword", "2", new OfWords($"{k}, a GDScript keyword"))));
            return new Listed(at.Start, items);
        }
        foreach (GDScriptMemberInfo local in typed.Analysis.LocalsAt(caret))
        {
            if (seen.Add(local.Name))
            {
                items.Add(MemberItem(typed, local, null, "0"));
            }
        }
        foreach (GDScriptMemberInfo member in typed.Analyzer.MembersInScope(typed.Tree, typed.Analysis.Path, typed.Analysis.ClassAt(caret)))
        {
            if (seen.Add(member.Name))
            {
                items.Add(MemberItem(typed, member, null, RankOf(member, "1")));
            }
        }
        foreach (string keyword in at.StatementStart ? StatementKeywords.Concat(ExpressionKeywords) : ExpressionKeywords)
        {
            if (seen.Add(keyword))
            {
                items.Add(Item(keyword, CompletionItemKind.Keyword, "keyword", "2", new OfWords($"{keyword}, a GDScript keyword")));
            }
        }
        GDScriptProject project = typed.Analyzer.Project;
        foreach (string named in project.ClassNames)
        {
            if (seen.Add(named))
            {
                items.Add(Item(named, CompletionItemKind.Class, project.ClassPath(named), "3", new OfClass(named, "class_name")));
            }
        }
        foreach (string autoload in project.Autoloads)
        {
            if (seen.Add(autoload))
            {
                items.Add(Item(autoload, CompletionItemKind.Module, "autoload", "3", new OfClass(autoload, "autoload")));
            }
        }
        if (typed.Analyzer.Engine is { } api)
        {
            items.AddRange(GlobalsOf(api).Names.Where(i => !seen.Contains(i.Label)));
        }
        return new Listed(at.Start, items);
    }

    /// <summary>The rank a member's item sorts by: a script's own members and what makes the class before what the engine's
    /// classes give it, as Godot's editor lists them by where they are declared.</summary>
    private static string RankOf(GDScriptMemberInfo member, string rank) => member.Declaration is { Script: not null } || MakesPlainly(member) ? rank : rank + "~~";

    /// <summary>Whether a member is the <c>new</c> of a class no script gives an <c>_init</c>, which takes nothing.</summary>
    private static bool MakesPlainly(GDScriptMemberInfo member) => member is { Name: "new", Kind: "func", Declaration: null };

    /// <summary>A member's item: its kind, its type, and for a function the parentheses its call takes, the caret inside them
    /// where it takes arguments.</summary>
    private CompletionItem MemberItem(GodotScriptTypes.Typed typed, GDScriptMemberInfo member, GDScriptType? owner, string rank)
    {
        Named what = NamedOf(member, owner);
        CompletionItemKind kind = member.Kind switch
        {
            "func" => CompletionItemKind.Method,
            "const" => CompletionItemKind.Constant,
            "signal" => CompletionItemKind.Event,
            "enum" => CompletionItemKind.Enum,
            "class" => CompletionItemKind.Class,
            _ => CompletionItemKind.Variable,
        };
        string rankOf = member.Name.StartsWith('_') ? rank + "~" : rank;
        if (member.Kind == "func")
        {
            bool takes = SignatureOf(typed, what) is not { Parameters.Count: 0, Varargs: false };
            // A function returning nothing says void, as its signature writes it, not its value's null.
            string returns = member.Type.IsVariant ? member.Kind : member.Type is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } ? "void" : member.Type.ToString();
            return Item(member.Name, kind, returns, rankOf, what, member.Name + "()", takes ? 1 : 0);
        }
        return Item(member.Name, kind, member.Type.IsVariant ? member.Kind : member.Type.ToString(), rankOf, what);
    }

    /// <summary>What a member of a type names: a built-in type's or an engine class's member by its owner, else a script's.</summary>
    private static Named NamedOf(GDScriptMemberInfo member, GDScriptType? owner) =>
        owner is { Kind: GDScriptTypeKind.Builtin } builtin ? new OfEngine(builtin.Name, member.Kind, member.Name, member.Type)
            : member.Declaration is { EngineClass: { } engine } ? new OfEngine(engine, member.Kind, member.Name, member.Type)
            : new OfScript(member);

    /// <summary>The engine's globals as items, made once for an API.</summary>
    private static GlobalItems GlobalsOf(GodotEngineApi api) => Globals.GetValue(api, static api =>
    {
        List<CompletionItem> names = [];
        List<CompletionItem> types = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Add(List<CompletionItem> into, string label, CompletionItemKind kind, string detail, string rank, Named what)
        {
            CompletionItem item = new() { Label = label, Kind = kind, Detail = detail, FilterText = label, SortText = rank + label };
            GlobalsNamed.AddOrUpdate(item, what);
            into.Add(item);
        }
        foreach (string builtin in api.Builtins.Keys.Where(b => b != "Nil").Order(StringComparer.Ordinal))
        {
            Add(names, builtin, CompletionItemKind.Struct, "built-in type", "4", new OfClass(builtin, "type"));
            Add(types, builtin, CompletionItemKind.Struct, "built-in type", "1", new OfClass(builtin, "type"));
            seen.Add(builtin);
        }
        foreach (string engine in api.Classes.Keys.Order(StringComparer.Ordinal))
        {
            string detail = api.Singletons.ContainsKey(engine) ? "singleton" : "class";
            Add(names, engine, CompletionItemKind.Class, detail, "4", new OfClass(engine, "class"));
            Add(types, engine, CompletionItemKind.Class, "class", "2", new OfClass(engine, "class"));
            seen.Add(engine);
        }
        foreach (string singleton in api.Singletons.Keys.Where(seen.Add).Order(StringComparer.Ordinal))
        {
            Add(names, singleton, CompletionItemKind.Module, "singleton", "4", new OfClass(api.Singletons[singleton], "class"));
        }
        foreach (GodotApiMethod function in api.Functions.Values.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            Add(names, function.Name, CompletionItemKind.Function, function.Returns, "4", new OfEngine(GlobalScope, "func", function.Name, GDScriptType.Variant));
        }
        foreach (string global in api.GlobalEnums.Keys.Order(StringComparer.Ordinal))
        {
            Add(names, global, CompletionItemKind.Enum, "enum", "5", new OfEngine(GlobalScope, "enum", global, GDScriptType.Variant));
            Add(types, global, CompletionItemKind.Enum, "enum", "3", new OfEngine(GlobalScope, "enum", global, GDScriptType.Variant));
        }
        foreach ((string value, string owner) in api.GlobalEnumValues.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            Add(names, value, CompletionItemKind.EnumMember, owner, "5", new OfEngine(GlobalScope, "const", value, GDScriptType.Variant));
        }
        foreach (string constant in api.GlobalConstants.Order(StringComparer.Ordinal))
        {
            Add(names, constant, CompletionItemKind.Constant, "constant", "5", new OfEngine(GlobalScope, "const", constant, GDScriptType.Variant));
        }
        return new GlobalItems(names, types);
    });

    /// <summary>The members of the value before a dot, a node's C# script's first, and the engine's or the script's after.</summary>
    private async Task<Listed?> MembersListedAsync(OpenDocument shown, GodotScriptTypes.Typed typed, GDScriptAsked at)
    {
        int dot = at.Start - 1;
        IReadOnlyList<GodotCSharpMember> csharp = await CSharpMembersAsync(context, shown, typed, dot);
        if (typed.Analysis.NodeAt(dot - 1) is not ({ } node, _) || node is not GDScriptExpression)
        {
            return null;
        }
        GDScriptType owner = typed.Analysis.TypeOf(node);
        List<CompletionItem> items = [];
        foreach (GodotCSharpMember member in csharp)
        {
            CompletionItem item = ItemOf(member);
            offered[item] = new Offered(new OfCSharp(member), member.Kind == "func" ? member.Name + "()" : null, member.Kind == "func" && !member.Shown.Contains("()", StringComparison.Ordinal) ? 1 : 0);
            items.Add(item);
        }
        if (!owner.IsVariant)
        {
            items.AddRange(typed.Analyzer.Members(owner).Where(m => csharp.All(c => c.Name != m.Name)).Select(m => MemberItem(typed, m, owner, RankOf(m, "0"))));
        }
        return new Listed(at.Start, items);
    }

    /// <summary>The node paths a script's scenes give from the node each runs it on, its unique names after <c>%</c>, each
    /// with its class and scene, quoted where a name holds more than a name's characters.</summary>
    private async Task<Listed?> NodePathsListedAsync(OpenDocument shown, GodotScriptTypes.Typed typed, GDScriptAsked at)
    {
        if (context.GodotOf(shown.Path) is not ({ } index, { } folder))
        {
            return null;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        string res = typed.Analysis.Path;
        List<(string Path, string Detail)> paths = await BackgroundWork.Run(() => NodePaths(scenes, res));
        List<CompletionItem> items = [];
        foreach ((string path, string detail) in paths)
        {
            bool unique = path.StartsWith('%');
            string bare = unique ? path[1..] : path;
            string label = (unique ? "%" : "$") + bare;
            string insert = bare.All(c => AssistContext.IsIdentifierChar(c) || c == '/') ? label : $"{label[0]}\"{bare}\"";
            items.Add(Item(label, CompletionItemKind.Field, detail, " ", new OfWords($"{label}: {detail}"), insert, 0, Replacing.Path));
        }
        return new Listed(at.Start, items, PathCharacters);
    }

    /// <summary>Each node path the scenes running a script give from its node, once, with its class and the scene it is in.</summary>
    private static List<(string Path, string Detail)> NodePaths(GodotScenes scenes, string script)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach ((string scene, GodotTreeNode node) in scenes.RunsOf(script))
        {
            foreach ((string path, GodotTreeNode target) in GodotScenes.PathsFrom(node))
            {
                found.TryAdd(path, $"{GodotChecks.Shown(target)} · {Path.GetFileName(scene)}");
            }
        }
        return [.. found.Select(f => (f.Key, f.Value))];
    }

    /// <summary>The types a place may name: the script's own classes and enums, the project's classes, the built-in types,
    /// the engine's classes and enums, and after <c>-&gt;</c> <c>void</c>.</summary>
    private Listed TypesListed(GodotScriptTypes.Typed typed, GDScriptAsked at)
    {
        List<CompletionItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (at.Returns)
        {
            items.Add(Item("void", CompletionItemKind.Keyword, "keyword", "0", new OfWords("void: the function returns nothing")));
        }
        items.Add(Item("Variant", CompletionItemKind.Struct, "any value", "1", new OfClass("Variant", "type")));
        foreach (GDScriptMemberInfo own in typed.Analyzer.Members(GDScriptAnalyzer.ScriptClass(typed.Tree, typed.Analysis.Path)).Where(m => m.Kind is "class" or "enum"))
        {
            if (seen.Add(own.Name))
            {
                items.Add(MemberItem(typed, own, null, "0"));
            }
        }
        GDScriptProject project = typed.Analyzer.Project;
        foreach (string named in project.ClassNames)
        {
            if (seen.Add(named))
            {
                items.Add(Item(named, CompletionItemKind.Class, project.ClassPath(named), "0", new OfClass(named, "class_name")));
            }
        }
        if (typed.Analyzer.Engine is { } api)
        {
            items.AddRange(GlobalsOf(api).Types.Where(i => !seen.Contains(i.Label)));
        }
        return new Listed(at.Start, items);
    }

    /// <summary>The engine's virtual functions the script's class may override and has not, each put in whole: its name, its
    /// parameters and what it returns, then the colon its body follows.</summary>
    private Listed? OverridesListed(GodotScriptTypes.Typed typed, GDScriptAsked at)
    {
        if (typed.Analyzer.Engine is not { } api)
        {
            return null;
        }
        GDScriptType own = GDScriptAnalyzer.ScriptClass(typed.Tree, typed.Analysis.Path);
        HashSet<string> declared = [.. typed.Tree.Members.Select(m => m.Node).OfType<GDScriptFunction>().Select(f => f.Identifier?.Name).OfType<string>()];
        GDScriptType? native = own;
        for (int depth = 0; native is not null && native.Kind != GDScriptTypeKind.Native && depth < 64; depth++)
        {
            native = typed.Analyzer.BaseOf(native);
        }
        List<CompletionItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (GodotApiClass engine in api.Chain(native?.Name ?? "RefCounted"))
        {
            foreach (GodotApiMethod method in engine.Methods.Values.Where(m => m.IsVirtual && !declared.Contains(m.Name) && m.Name != "free").OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(method.Name))
                {
                    continue;
                }
                GDScriptSignature signature = GDScriptSignature.Of(method, engine.Name, typed.Analyzer);
                string parameters = string.Join(", ", signature.Parameters.Select(p => p with { Default = null }));
                string insert = $"{method.Name}({parameters}) -> {signature.Returns}:";
                items.Add(Item(method.Name, CompletionItemKind.Method, $"{engine.Name}'s", method.Name.StartsWith('_') ? "0" : "1", new OfEngine(engine.Name, "func", method.Name, GDScriptType.Variant), insert));
            }
        }
        return new Listed(at.Start, items);
    }

    /// <summary>What a string may say where Godot's call takes it: a node's path from the script's node (an autoload's after
    /// <c>/root/</c>), a file's res:// path, a method, signal or variable of what the call is made on, or a name of a kind the
    /// project defines (<see cref="GodotChecks.Named(GodotScenes, GodotStringKind, IReadOnlyList{string}, string?, string?, string?)"/>);
    /// null for a string of none.</summary>
    private async Task<Listed?> StringsListedAsync(OpenDocument shown, GodotScriptTypes.Typed typed, GDScriptAsked at, int caret)
    {
        string text = typed.Text;
        string typedSoFar = text[at.Start..caret];
        GDScriptCallAt? call = at.Argument ? at.Call : null;
        if (context.GodotOf(shown.Path) is not ({ } index, { } folder))
        {
            return null;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        string res = typed.Analysis.Path;
        List<CompletionItem> items = [];
        if (typedSoFar.StartsWith("res://", StringComparison.Ordinal) || call is { Name: "load" or "preload" or "change_scene_to_file" or "exists", Argument: 0 })
        {
            IReadOnlyList<string> files = index.FilesUnder(folder);
            foreach (string file in files)
            {
                if (GodotScenes.ResPathOf(folder, file) is { } path && !path.EndsWith(".import", StringComparison.Ordinal) && !path.EndsWith(".uid", StringComparison.Ordinal))
                {
                    items.Add(Item(path, CompletionItemKind.File, Path.GetExtension(path), " ", new OfWords(path), null, 0, Replacing.String));
                }
            }
            return new Listed(at.Start, items, PathCharacters);
        }
        if (call is null)
        {
            return null;
        }
        if (NodePathCalls.TryGetValue(call.Name, out int pathArgument) && pathArgument == call.Argument)
        {
            if (typedSoFar.StartsWith('/'))
            {
                items.AddRange(scenes.Defined.Autoloads.Select(a => Item(GodotDefinitions.RootPath + a.Name, CompletionItemKind.Module, "autoload", " ",
                    new OfWords($"{a.Name}, an autoload"), null, 0, Replacing.String)));
            }
            else
            {
                List<(string Path, string Detail)> paths = await BackgroundWork.Run(() => NodePaths(scenes, res));
                items.AddRange(paths.Select(p => Item(p.Path, CompletionItemKind.Field, p.Detail, " ", new OfWords($"{p.Path}: {p.Detail}"), null, 0, Replacing.String)));
            }
            return new Listed(at.Start, items, PathCharacters);
        }
        if (MemberCalls.TryGetValue(call.Name, out (int Argument, string Kind) member) && member.Argument == call.Argument)
        {
            GDScriptType owner = ReceiverType(typed, call) ?? GDScriptAnalyzer.ScriptClass(typed.Tree, res);
            foreach (GDScriptMemberInfo named in owner.IsVariant ? [] : typed.Analyzer.Members(owner).Where(m => m.Kind == member.Kind))
            {
                CompletionItem item = MemberItem(typed, named, owner, named.Name.StartsWith('_') ? "1" : "0");
                offered[item] = offered[item] with { Insert = null, Back = 0, Replaces = Replacing.String };
                items.Add(item);
            }
            return new Listed(at.Start, items);
        }
        if (KindOf(call) is not { } kind)
        {
            return null;
        }
        (string? node, string? export, string? held) = Told(typed, call);
        string? themeType = GodotChecks.IsTheme(kind) ? NativeOf(typed, ReceiverType(typed, call) ?? GDScriptAnalyzer.ScriptClass(typed.Tree, res)) : null;
        GodotChecks.Candidates candidates = await BackgroundWork.Run(() => GodotChecks.Named(scenes, kind, [res], node, export, themeType ?? held));
        items.AddRange(candidates.Names.Select(n => Item(n.Name, CompletionItemKind.Value, n.Places.FirstOrDefault() is { } place ? $"{n.Detail} · {place.Shown}" : n.Detail, " ",
            new OfWords($"{n.Name}: {n.Detail}"), null, 0, Replacing.String)));
        return new Listed(at.Start, items, PathCharacters);
    }

    /// <summary>The kind of name a string given to a call takes, by the call's name and the argument it is (a theme item's by
    /// a control's theme calls); null for none.</summary>
    private static GodotStringKind? KindOf(GDScriptCallAt call)
    {
        if (KindCalls.TryGetValue(call.Name, out (GodotStringKind Kind, int[] Arguments) known) && known.Arguments.Contains(call.Argument))
        {
            return known.Kind;
        }
        return call.Argument == 0 && ThemeCall().Match(call.Name) is { Success: true } theme ? theme.Groups[2].Value switch
        {
            "color" => GodotStringKind.ThemeColor,
            "constant" => GodotStringKind.ThemeConstant,
            "font" => GodotStringKind.ThemeFont,
            "font_size" => GodotStringKind.ThemeFontSize,
            "icon" => GodotStringKind.ThemeIcon,
            _ => GodotStringKind.ThemeStyle,
        } : null;
    }

    /// <summary>What a call is made on as the scenes tell it: a node by its path from the script's node (<c>"."</c> for the
    /// script's own, <c>$Anim</c>'s, <c>get_node("Anim")</c>'s), an exported variable a scene sets, and the property holding
    /// a material (<c>$Sprite.material</c>'s).</summary>
    private static (string? Node, string? Export, string? Member) Told(GodotScriptTypes.Typed typed, GDScriptCallAt call)
    {
        if (call.Receiver is not { } receiver || receiver == "self")
        {
            return (".", null, null);
        }
        string? member = null;
        int dot = receiver.LastIndexOf('.');
        if (dot > 0 && receiver[(dot + 1)..].All(AssistContext.IsIdentifierChar) && !receiver.EndsWith(')'))
        {
            member = receiver[(dot + 1)..];
            receiver = receiver[..dot];
        }
        Match path = Regex.Match(receiver, @"^(?:\$""(?<path>[^""]*)""|\$(?<path>[\w/]+)|%(?<unique>\w+)|get_node(?:_or_null)?\(\s*[""'](?<path>[^""']*)[""']\s*\))$", RegexOptions.CultureInvariant);
        if (path.Success)
        {
            return (path.Groups["unique"].Success ? "%" + path.Groups["unique"].Value : path.Groups["path"].Value, null, member);
        }
        if (receiver.All(AssistContext.IsIdentifierChar) && typed.Tree.Members.Select(m => m.Node).OfType<GDScriptVariable>()
            .Any(v => v.Identifier?.Name == receiver && v.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal))))
        {
            return (null, receiver, member);
        }
        return (null, null, member);
    }

    /// <summary>The type of what a call is made on, read where it is written before the call's name; null where nothing is
    /// before it or it is not read.</summary>
    private static GDScriptType? ReceiverType(GodotScriptTypes.Typed typed, GDScriptCallAt call)
    {
        if (call.Receiver is null || call.NameStart < 2 || typed.Analysis.NodeAt(call.NameStart - 2) is not ({ } node, _) || node is not GDScriptExpression)
        {
            return null;
        }
        GDScriptType type = typed.Analysis.TypeOf(node);
        return type.IsVariant ? null : type;
    }

    /// <summary>The engine class a type is or extends; null for none.</summary>
    private static string? NativeOf(GodotScriptTypes.Typed typed, GDScriptType type)
    {
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is not null && depth < 64; depth++, at = typed.Analyzer.BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return at.Name;
            }
        }
        return null;
    }
}
