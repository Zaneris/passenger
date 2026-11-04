using System.Text;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A place a Godot project's files write a name: the file (res://), where its characters lie in its text, whether
/// it defines the name (where Go to Declaration goes) or only uses it, and where it is as its words name it ("project.godot:12").</summary>
public sealed record GodotPlace(string Res, int Start, int End, bool Defines, string Shown)
{
    /// <summary>Whether its characters are written as the name says, no escape among them, so a rename can write over them.</summary>
    public bool Plain { get; init; } = true;
}

/// <summary>A name a Godot project defines, what it is as shown beside it, and every place its files write it.</summary>
public sealed record GodotDefined(string Name, string Detail, IReadOnlyList<GodotPlace> Places);

/// <summary>A theme item a project's file writes: its theme type (a node's class, for a scene's override of one), its kind
/// (<see cref="GodotThemeTable.Kinds"/>), its name and the place of the name in its key (<c>Label/colors/font_color</c>,
/// <c>theme_override_colors/font_color</c>).</summary>
public sealed record GodotThemeItem(string Type, string Kind, string Name, GodotPlace Place);

/// <summary>What a Godot project's own files define for the strings its code gives Godot's calls, read as Godot reads them:
/// project.godot's input actions (<c>[input]</c>, beside Godot's own <c>ui_</c> ones), autoloads (<c>[autoload]</c>), global
/// shader uniforms (<c>[shader_globals]</c>), named layers (<c>[layer_names]</c>) and the translations it lists
/// (<c>internationalization/locale/translations</c>: a CSV's keys, a PO file's msgids); the groups its scenes give nodes
/// (<c>groups=[…]</c>); each animation player's animations (its libraries, a scene's own or a resource's, <c>_data</c>'s
/// keys) and each animated sprite's (its SpriteFrames'); an AnimationTree's parameters (<c>parameters/…</c>, as its tree's
/// nodes and conditions make them); and a shader's uniforms (its <c>#include</c>s' too), with the shader a node's material
/// gives it. Each name with every place its files write it, which its rename edits: its GDScripts' strings and words, and
/// its scenes' node paths, among them. Pure, built from <see cref="GodotScenes"/>, so it is read off the UI thread.</summary>
public sealed class GodotDefinitions
{
    /// <summary>The res:// path of the file that makes a folder a Godot project.</summary>
    public const string ProjectRes = "res://project.godot";

    /// <summary>The prefix a ShaderMaterial gives each of its shader's parameters it sets.</summary>
    public const string ShaderParameter = "shader_parameter/";

    /// <summary>The prefix of the property an AnimationTree takes each of its tree's parameters by.</summary>
    public const string TreeParameters = "parameters/";

    /// <summary>The start of a node path through the tree's root, whose next name is an autoload's or the running scene's.</summary>
    public const string RootPath = "/root/";

    /// <summary>How deep a shader's includes are followed.</summary>
    private const int Deepest = 8;

    /// <summary>Godot's own input actions, which every project's input map has beside those of its project.godot.</summary>
    public static readonly IReadOnlyList<string> BuiltInActions =
    [
        "ui_accept", "ui_select", "ui_cancel", "ui_focus_next", "ui_focus_prev", "ui_left", "ui_right", "ui_up", "ui_down", "ui_page_up",
        "ui_page_down", "ui_home", "ui_end", "ui_cut", "ui_copy", "ui_paste", "ui_undo", "ui_redo", "ui_menu", "ui_focus_mode",
        "ui_accessibility_drag_and_drop", "ui_close_dialog", "ui_swap_input_direction", "ui_unicode_start", "ui_colorpicker_delete_preset",
        "ui_graph_duplicate", "ui_graph_delete", "ui_graph_follow_left", "ui_graph_follow_right",
        "ui_filedialog_up_one_level", "ui_filedialog_refresh", "ui_filedialog_show_hidden", "ui_filedialog_find", "ui_filedialog_focus_path",
        "ui_filedialog_delete", "ui_text_completion_query", "ui_text_completion_accept", "ui_text_completion_replace", "ui_text_newline",
        "ui_text_newline_blank", "ui_text_newline_above", "ui_text_indent", "ui_text_dedent", "ui_text_backspace", "ui_text_backspace_word",
        "ui_text_backspace_all_to_left", "ui_text_delete", "ui_text_delete_word", "ui_text_delete_all_to_right", "ui_text_caret_left",
        "ui_text_caret_word_left", "ui_text_caret_right", "ui_text_caret_word_right", "ui_text_caret_up", "ui_text_caret_down",
        "ui_text_caret_line_start", "ui_text_caret_line_end", "ui_text_caret_page_up", "ui_text_caret_page_down", "ui_text_caret_document_start",
        "ui_text_caret_document_end", "ui_text_caret_add_below", "ui_text_caret_add_above", "ui_text_scroll_up", "ui_text_scroll_down",
        "ui_text_select_all", "ui_text_select_word_under_caret", "ui_text_add_selection_for_next_occurrence",
        "ui_text_skip_selection_for_next_occurrence", "ui_text_clear_carets_and_selection", "ui_text_toggle_insert_mode", "ui_text_submit",
    ];

    private readonly GodotScenes scenes;
    private readonly Lazy<GodotFile?> project;
    private readonly Lazy<IReadOnlyList<GodotDefined>> actions;
    private readonly Lazy<IReadOnlyList<GodotDefined>> autoloads;
    private readonly Lazy<IReadOnlyList<GodotDefined>> groups;
    private readonly Lazy<IReadOnlyList<GodotDefined>> globals;
    private readonly Lazy<IReadOnlyList<(string Res, IReadOnlyList<GodotScriptToken> Tokens)>> scripts;
    private readonly Lazy<(IReadOnlyList<GodotDefined> Keys, IReadOnlyList<string> Files, bool Whole)> translations;
    private readonly Lazy<(IReadOnlyList<GodotDefined> Names, bool Whole)> animations;
    private readonly Lazy<(IReadOnlyList<GodotThemeItem> Defined, IReadOnlyList<GodotThemeItem> Overridden, IReadOnlyDictionary<string, string> Variations)> themes;
    private readonly Dictionary<string, (IReadOnlyList<GodotDefined> Names, bool Whole)> uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> lines = new(StringComparer.Ordinal);
    private readonly Lock reading = new();

    public GodotDefinitions(GodotScenes scenes)
    {
        this.scenes = scenes;
        project = new(() => scenes.Texts.TryGetValue(ProjectRes, out string? text) ? GodotFile.Parse(text) : null);
        scripts = new(() => [.. scenes.Texts.Where(t => GodotScriptDeclarations.IsScript(t.Key)).OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => (t.Key, GodotScriptStrings.Read(t.Value)))]);
        actions = new(() => Scripted(Keys("input", "input action"), ActionCalls));
        autoloads = new(() => Autoloaded(Keys("autoload", "autoload")));
        groups = new(() => Scripted(ReadGroups(), GroupCalls));
        globals = new(() => Scripted(ReadGlobals(), GlobalCalls));
        translations = new(ReadTranslations);
        animations = new(ReadAnimations);
        themes = new(ReadThemes);
    }

    /// <summary>The functions a GDScript gives an input action's name to, at the arguments that take it.</summary>
    private static readonly Dictionary<string, int[]> ActionCalls = new(StringComparer.Ordinal)
    {
        ["is_action_pressed"] = [0], ["is_action_just_pressed"] = [0], ["is_action_just_released"] = [0], ["is_action_released"] = [0], ["is_action"] = [0],
        ["get_action_strength"] = [0], ["get_action_raw_strength"] = [0], ["action_press"] = [0], ["action_release"] = [0], ["get_axis"] = [0, 1],
        ["get_vector"] = [0, 1, 2, 3], ["has_action"] = [0], ["erase_action"] = [0], ["add_action"] = [0], ["action_add_event"] = [0], ["action_erase_event"] = [0],
        ["action_erase_events"] = [0], ["action_get_events"] = [0], ["action_has_event"] = [0], ["action_set_deadzone"] = [0], ["action_get_deadzone"] = [0],
        ["event_is_action"] = [1],
    };

    /// <summary>The functions a GDScript gives a group's name to, at the arguments that take it.</summary>
    private static readonly Dictionary<string, int[]> GroupCalls = new(StringComparer.Ordinal)
    {
        ["add_to_group"] = [0], ["remove_from_group"] = [0], ["is_in_group"] = [0], ["get_nodes_in_group"] = [0], ["get_first_node_in_group"] = [0],
        ["get_node_count_in_group"] = [0], ["has_group"] = [0], ["call_group"] = [0], ["notify_group"] = [0], ["set_group"] = [0],
        ["call_group_flags"] = [1], ["notify_group_flags"] = [1], ["set_group_flags"] = [1],
    };

    /// <summary>The functions a GDScript gives a global shader uniform's name to, at the arguments that take it.</summary>
    private static readonly Dictionary<string, int[]> GlobalCalls = new(StringComparer.Ordinal)
    {
        ["global_shader_parameter_set"] = [0], ["global_shader_parameter_get"] = [0], ["global_shader_parameter_set_override"] = [0],
        ["global_shader_parameter_remove"] = [0], ["global_shader_parameter_add"] = [0], ["global_shader_parameter_get_type"] = [0],
    };

    /// <summary>The functions a GDScript gives a translation's key to, at the arguments that take it.</summary>
    private static readonly Dictionary<string, int[]> TranslationCalls = new(StringComparer.Ordinal)
    {
        ["tr"] = [0], ["tr_n"] = [0], ["atr"] = [0], ["atr_n"] = [0], ["translate"] = [0], ["translate_plural"] = [0],
    };

    /// <summary>Whether a file is one of these that is not a scene or resource: project.godot, a shader or shader include,
    /// a CSV or PO translation.</summary>
    public static bool Reads(string path) =>
        Path.GetFileName(path).Equals(GodotProject.FileName, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".po", StringComparison.OrdinalIgnoreCase);

    /// <summary>project.godot's input actions, each by its key.</summary>
    public IReadOnlyList<GodotDefined> Actions => actions.Value;

    /// <summary>project.godot's autoloads, each by its key, the node Godot adds under /root by that name.</summary>
    public IReadOnlyList<GodotDefined> Autoloads => autoloads.Value;

    /// <summary>Whether an autoload of a name is a singleton, its value marked with <c>*</c>, which code reads by its name.</summary>
    public bool IsSingleton(string name) =>
        project.Value?.Sections.FirstOrDefault(s => s.Tag == "autoload")?.Property(name)?.Value.Text is { } value && value.StartsWith('*');

    /// <summary>The res:// path of the script or scene an autoload loads; null for none of the name.</summary>
    public string? AutoloadFile(string name) =>
        project.Value?.Sections.FirstOrDefault(s => s.Tag == "autoload")?.Property(name)?.Value.Text is { } value ? value.TrimStart('*') : null;

    /// <summary>The events an input action of project.godot takes (its value's <c>events</c>), each said as a person names it
    /// (<see cref="GodotInputEvents"/>); none for an action of none or none of the name.</summary>
    public IReadOnlyList<string> EventsOf(string action)
    {
        if (project.Value is not { } file || file.Sections.FirstOrDefault(s => s.Tag == "input")?.Property(action) is not { } property
            || DictionaryOf(file.Text, property.Value).FirstOrDefault(e => e.Key == "events") is not { } events)
        {
            return [];
        }
        return [.. ObjectsIn(file.Text, events.Value.Start, events.Value.End).Select(o => GodotInputEvents.Shown(o.Type, o.Fields))];
    }

    /// <summary>Where project.godot sets a key of a section (<c>application</c>, <c>run/main_scene</c>): its key's place and
    /// the string it gives (null for another value); null where it sets none.</summary>
    public (GodotPlace Place, string? Text)? SettingOf(string section, string key)
    {
        if (project.Value is not { } file || file.Sections.FirstOrDefault(s => s.Tag == section)?.Property(key) is not { } property)
        {
            return null;
        }
        return (new GodotPlace(ProjectRes, property.KeyStart, property.KeyStart + property.Key.Length, true, $"{GodotProject.FileName}:{file.LineOf(property.KeyStart) + 1}"),
            property.Value.Text);
    }

    /// <summary>The strings a setting of project.godot holds (<c>editor_plugins</c>' <c>enabled</c>); none where it sets none.</summary>
    public IReadOnlyList<string> StringsOf(string section, string key) =>
        project.Value is { } file && file.Sections.FirstOrDefault(s => s.Tag == section)?.Property(key) is { } property
            ? [.. StringsIn(file.Text, property.Value).Select(s => s.Text)] : [];

    /// <summary>The objects an array's text holds at its top (<c>Object(InputEventKey,"keycode":0,…)</c>), each its class and
    /// its fields' values as written.</summary>
    internal static IEnumerable<(string Type, IReadOnlyDictionary<string, string> Fields)> ObjectsIn(string text, int start, int end)
    {
        const string Call = "Object(";
        for (int at = text.IndexOf(Call, start, end - start, StringComparison.Ordinal); at >= 0 && at < end;
            at = at + Call.Length < end ? text.IndexOf(Call, at + Call.Length, end - at - Call.Length, StringComparison.Ordinal) : -1)
        {
            int open = at + Call.Length;
            int close = ItemEnd(text, open, end);
            while (close < end && text[close] == ',')
            {
                close = ItemEnd(text, close + 1, end);
            }
            int comma = text.IndexOf(',', open);
            string type = (comma >= 0 && comma < close ? text[open..comma] : text[open..close]).Trim();
            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            int item = comma >= 0 && comma < close ? comma + 1 : close;
            while (item < close)
            {
                int next = ItemEnd(text, item, close);
                string pair = text[item..next].Trim();
                if (pair.StartsWith('"') && GodotFile.Quoted(pair, 0) is (string key, int keyEnd, _) && pair.IndexOf(':', keyEnd) is var colon and > 0)
                {
                    fields[key] = pair[(colon + 1)..].Trim();
                }
                item = next + 1;
            }
            yield return (type, fields);
        }
    }

    /// <summary>The groups the project's scenes give nodes, each with every node's place.</summary>
    public IReadOnlyList<GodotDefined> Groups => groups.Value;

    /// <summary>project.godot's global shader uniforms, each by its key, with each shader's <c>global uniform</c> of it and its uses.</summary>
    public IReadOnlyList<GodotDefined> ShaderGlobals => globals.Value;

    /// <summary>The kinds of layers project.godot's [layer_names] names, in the order Godot's Project Settings lists them.</summary>
    public static readonly IReadOnlyList<string> LayerKinds = ["2d_render", "2d_physics", "2d_navigation", "3d_render", "3d_physics", "3d_navigation", "avoidance"];

    /// <summary>How many layers of a kind of project.godot's [layer_names] Godot numbers (<c>2d_physics</c>'s 32, a render's 20).</summary>
    public static int LayerCount(string layers) => layers.EndsWith("_render", StringComparison.Ordinal) ? 20 : 32;

    /// <summary>A kind of layers as its words name it: "2D physics", "3D render", "avoidance".</summary>
    public static string LayersShown(string layers) => layers.Split('_') is [{ } dimension, { } what] ? $"{dimension.ToUpperInvariant()} {what}" : layers;

    /// <summary>Every layer of a kind by its number (from 1), each with the name project.godot's [layer_names] gives it as its
    /// detail and the place of its key, or none where it is not named.</summary>
    public IReadOnlyList<GodotDefined> Layers(string layers)
    {
        GodotSection? named = project.Value?.Sections.FirstOrDefault(s => s.Tag == "layer_names");
        List<GodotDefined> numbered = [];
        for (int number = 1; number <= LayerCount(layers); number++)
        {
            string key = $"{layers}/layer_{number}";
            string shown = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (named?.Property(key) is { Value.Text: { Length: > 0 } name } property && project.Value is { } file)
            {
                numbered.Add(new GodotDefined(shown, name, [new GodotPlace(ProjectRes, property.KeyStart, property.KeyStart + key.Length, true, $"{GodotProject.FileName}:{file.LineOf(property.KeyStart) + 1}")]));
            }
            else
            {
                numbered.Add(new GodotDefined(shown, string.Empty, []));
            }
        }
        return numbered;
    }

    /// <summary>The renderer project.godot names (<c>rendering/renderer/rendering_method</c>), whose shading language Godot's
    /// compiler takes: <c>forward_plus</c>, <c>mobile</c> or <c>gl_compatibility</c>, Forward+ for none or another.</summary>
    public string RenderingMethod => SettingOf("rendering", "renderer/rendering_method")?.Text is { } method && method is "mobile" or "gl_compatibility"
        ? method : ForwardPlus;

    /// <summary>Godot's default renderer, as project.godot names it.</summary>
    public const string ForwardPlus = "forward_plus";

    /// <summary>The res:// path of the project's custom theme (project.godot's <c>gui/theme/custom</c>), which every node of
    /// the game looks its theme items up in before Godot's default theme; null for none.</summary>
    public string? CustomTheme => SettingOf("gui", "theme/custom")?.Text is { Length: > 0 } res ? res : null;

    /// <summary>Every item the project's themes define: each Theme resource's, a file's own or one a file holds within it,
    /// the custom theme's first.</summary>
    public IReadOnlyList<GodotThemeItem> ThemeItems => themes.Value.Defined;

    /// <summary>Every theme item the project's scenes override on a node (<c>theme_override_colors/font_color</c>), by the
    /// node's class.</summary>
    public IReadOnlyList<GodotThemeItem> ThemeOverrides => themes.Value.Overridden;

    /// <summary>The type variations the project's themes declare (<c>TitleLabel/base_type = &amp;"Label"</c>), each with the
    /// type it varies, the custom theme's first.</summary>
    public IReadOnlyDictionary<string, string> ThemeVariations => themes.Value.Variations;

    /// <summary>The theme types Godot looks an item up in for a node's class and the type variation its scene gives it, as
    /// a theme's type dependencies go: the variation, each type it varies in turn (the project's themes' declarations
    /// before the default theme's) until the class, then the class and its bases; a type named alone, its own chain.</summary>
    public IReadOnlyList<string> ThemeChain(string type, string? variation = null)
    {
        GodotThemeTable table = GodotThemeTable.Carried;
        List<string> chain = [];
        for (string? at = variation; at is { Length: > 0 } && at != type && !chain.Contains(at);
            at = ThemeVariations.TryGetValue(at, out string? varied) || table.Variations.TryGetValue(at, out varied) ? varied : null)
        {
            chain.Add(at);
        }
        chain.AddRange(table.Chain(type).Where(t => !chain.Contains(t)));
        return chain;
    }

    private (IReadOnlyList<GodotThemeItem>, IReadOnlyList<GodotThemeItem>, IReadOnlyDictionary<string, string>) ReadThemes()
    {
        const string Override = "theme_override_";
        string? custom = CustomTheme;
        List<GodotThemeItem> defined = [];
        List<GodotThemeItem> overridden = [];
        Dictionary<string, string> variations = new(StringComparer.Ordinal);
        foreach ((string res, GodotFile file) in scenes.Files.OrderBy(f => f.Key != custom).ThenBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (GodotSection section in file.Sections)
            {
                bool theme = section.Tag == "resource" ? file.Header?.Text("type") == "Theme" : section.Tag == "sub_resource" && section.Text("type") == "Theme";
                string? type = section.Tag == "node" ? new GodotNode(section).Type : null;
                foreach (GodotEntry property in theme || section.Tag == "node" ? section.Properties : [])
                {
                    string[] parts = property.Key.Split('/');
                    if (theme && parts is [{ Length: > 0 } varied, "base_type"] && property.Value.Text is { Length: > 0 } based)
                    {
                        variations.TryAdd(varied, based);
                        continue;
                    }
                    bool named = theme ? parts.Length == 3 && parts[0].Length > 0 : parts.Length == 2 && parts[0].StartsWith(Override, StringComparison.Ordinal);
                    string kind = named ? theme ? parts[1] : parts[0][Override.Length..] : string.Empty;
                    if (!named || !GodotThemeTable.Kinds.Contains(kind) || parts[^1].Length == 0)
                    {
                        continue;
                    }
                    int start = property.KeyStart + property.Key.Length - parts[^1].Length;
                    GodotPlace place = new(res, start, start + parts[^1].Length, theme, $"{Path.GetFileName(res)}:{file.LineOf(start) + 1}")
                    {
                        Plain = file.Text.AsSpan(property.KeyStart).StartsWith(property.Key, StringComparison.Ordinal),
                    };
                    (theme ? defined : overridden).Add(new GodotThemeItem(theme ? parts[0] : type ?? string.Empty, kind, parts[^1], place));
                }
            }
        }
        return (defined, overridden, variations);
    }

    /// <summary>The keys of the translations project.godot lists, each with its place in every file.</summary>
    public IReadOnlyList<GodotDefined> Translations => translations.Value.Keys;

    /// <summary>The CSV and PO files project.godot's translations are read from, by res:// path.</summary>
    public IReadOnlyList<string> TranslationFiles => translations.Value.Files;

    /// <summary>Whether every translation project.godot lists was read: none binary, none missing.</summary>
    public bool TranslationsWhole => translations.Value.Whole;

    /// <summary>Every animation player's animations in every scene, a name's places merged.</summary>
    public IReadOnlyList<GodotDefined> Animations => animations.Value.Names;

    /// <summary>Whether every animation library of every scene was read (none a binary resource).</summary>
    public bool AnimationsWhole => animations.Value.Whole;

    /// <summary>Whether a node holds animations: animation libraries, as an AnimationPlayer or AnimationTree whose sections give it
    /// some does, or SpriteFrames, as an animated sprite whose sections give it some does.</summary>
    public static bool IsPlayer(GodotTreeNode node) =>
        node.Written.Any(w => w.Node.Section.Properties.Any(p => p.Key is "libraries" or "sprite_frames" || p.Key.StartsWith("libraries/", StringComparison.Ordinal)));

    /// <summary>An animation player's animations, by their names in it (a library's own after its name and '/'), each with
    /// its key in the library and the player's sections naming it (<c>autoplay</c>); and whether each of its libraries was read.</summary>
    public (IReadOnlyList<GodotDefined> Names, bool Whole) AnimationsOf(GodotTreeNode player)
    {
        if (player.Written.Select(w => (w.Scene, w.Node.Section.Property("sprite_frames")?.Value)).LastOrDefault(w => w.Value is not null) is ({ } framed, { } frames))
        {
            return FramesOf(player, framed, frames);
        }
        Dictionary<string, (string Res, GodotValue Value)> libraries = new(StringComparer.Ordinal);
        foreach ((string res, GodotNode section) in player.Written)
        {
            if (!scenes.Files.TryGetValue(res, out GodotFile? file))
            {
                continue;
            }
            foreach (GodotEntry property in section.Section.Properties)
            {
                if (property.Key.StartsWith("libraries/", StringComparison.Ordinal))
                {
                    libraries[property.Key["libraries/".Length..]] = (res, property.Value);
                }
                else if (property.Key == "libraries")
                {
                    foreach (Entry entry in DictionaryOf(file.Text, property.Value))
                    {
                        libraries[entry.Key] = (res, entry.Value);
                    }
                }
            }
        }
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        bool whole = true;
        foreach ((string library, (string res, GodotValue value)) in libraries)
        {
            (string Res, GodotSection Section)? holding = Resource(res, value);
            if (holding is not ({ } at, { } section) || section.Property("_data") is not { } data)
            {
                whole &= holding is not null;
                continue;
            }
            GodotFile file = scenes.Files[at];
            foreach (Entry entry in DictionaryOf(file.Text, data.Value))
            {
                string name = library.Length == 0 ? entry.Key : $"{library}/{entry.Key}";
                Add(found, name, new GodotPlace(at, entry.KeyStart, entry.KeyEnd, true, $"{Path.GetFileName(at)}:{file.LineOf(entry.KeyStart) + 1}, {player.Name}") { Plain = entry.KeyPlain });
                // An older file names the animation again in its own resource_name.
                if (Resource(at, entry.Value) is ({ } own, { } animation) && animation.Property("resource_name")?.Value is { Text: { } named, Plain: true } written && named == entry.Key)
                {
                    Add(found, name, new GodotPlace(own, written.TextStart, written.TextEnd, false, $"{Path.GetFileName(own)}:{scenes.Files[own].LineOf(written.TextStart) + 1}"));
                }
            }
        }
        foreach ((string res, GodotNode section) in player.Written)
        {
            foreach (GodotEntry property in section.Section.Properties.Where(p => p.Key is "autoplay" or "current_animation" or "assigned_animation"))
            {
                if (property.Value is { Text: { Length: > 0 } named, Plain: true } value && found.ContainsKey(named))
                {
                    Add(found, named, new GodotPlace(res, value.TextStart, value.TextEnd, false, $"{Path.GetFileName(res)}:{scenes.Files[res].LineOf(value.TextStart) + 1}, {player.Name}"));
                }
            }
        }
        return ([.. found.Select(f => new GodotDefined(f.Key, $"animation of {player.Name}", f.Value))], whole);
    }

    /// <summary>An animated sprite's animations, by the names its SpriteFrames' <c>animations</c> give them, each with the
    /// sprite's sections naming it (<c>animation</c>, <c>autoplay</c>); and whether its frames were read.</summary>
    private (IReadOnlyList<GodotDefined> Names, bool Whole) FramesOf(GodotTreeNode sprite, string res, GodotValue frames)
    {
        if (Resource(res, frames) is not ({ } at, { } section) || section.Property("animations")?.Value is not { } listed)
        {
            return ([], false);
        }
        GodotFile file = scenes.Files[at];
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        foreach (GodotValue item in ItemsOf(file.Text, listed))
        {
            if (DictionaryOf(file.Text, item).FirstOrDefault(e => e.Key == "name")?.Value is { Text: { } name } value)
            {
                Add(found, name, new GodotPlace(at, value.TextStart, value.TextEnd, true, $"{Path.GetFileName(at)}:{file.LineOf(value.TextStart) + 1}, {sprite.Name}") { Plain = value.Plain });
            }
        }
        foreach ((string written, GodotNode node) in sprite.Written)
        {
            foreach (GodotEntry property in node.Section.Properties.Where(p => p.Key is "animation" or "autoplay"))
            {
                if (property.Value is { Text: { Length: > 0 } named, Plain: true } value && found.ContainsKey(named))
                {
                    Add(found, named, new GodotPlace(written, value.TextStart, value.TextEnd, false, $"{Path.GetFileName(written)}:{scenes.Files[written].LineOf(value.TextStart) + 1}, {sprite.Name}"));
                }
            }
        }
        return ([.. found.Select(f => new GodotDefined(f.Key, $"animation of {sprite.Name}", f.Value))], true);
    }

    /// <summary>The parameters an AnimationTree's tree makes (<c>parameters/playback</c>, <c>parameters/conditions/run</c>,
    /// <c>parameters/Blend2/blend_amount</c>), each by its whole path with where its files write what makes it (a condition's
    /// value, a node's name, the tree's root); and whether every node of the tree is of a kind whose parameters are known.</summary>
    public (IReadOnlyList<GodotDefined> Names, bool Whole) TreeParametersOf(GodotTreeNode tree)
    {
        (string Res, GodotEntry Property)? root = null;
        foreach ((string res, GodotNode written) in tree.Written)
        {
            if (written.Section.Property("tree_root") is { } property)
            {
                root = (res, property);
            }
        }
        if (root is not ({ } at, { } key) || Resource(at, key.Value) is not ({ } file, { } section))
        {
            return ([], false);
        }
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        Dictionary<string, string> kinds = new(StringComparer.Ordinal);
        GodotPlace place = new(at, key.KeyStart, key.KeyStart + key.Key.Length, true, $"{Path.GetFileName(at)}:{scenes.Files[at].LineOf(key.KeyStart) + 1}, {tree.Name}");
        bool whole = TreeNode(file, section, TreeParameters, place, found, kinds, 0);
        return ([.. found.Select(f => new GodotDefined(f.Key, $"{kinds[f.Key]} parameter of {tree.Name}", f.Value))], whole);
    }

    /// <summary>The parameters each kind of a tree's node makes of its own, beside those every node does.</summary>
    private static readonly Dictionary<string, string[]> TreeNodeParameters = new(StringComparer.Ordinal)
    {
        ["AnimationNodeAnimation"] = [], ["AnimationNodeOutput"] = [], ["AnimationNodeStartState"] = [], ["AnimationNodeEndState"] = [], ["AnimationNodeBlendTree"] = [],
        ["AnimationNodeStateMachine"] = ["playback"],
        ["AnimationNodeBlendSpace1D"] = ["blend_position", "closest", "length_internal"], ["AnimationNodeBlendSpace2D"] = ["blend_position", "closest", "length_internal"],
        ["AnimationNodeBlend2"] = ["blend_amount"], ["AnimationNodeBlend3"] = ["blend_amount"], ["AnimationNodeAdd2"] = ["add_amount"], ["AnimationNodeAdd3"] = ["add_amount"],
        ["AnimationNodeSub2"] = ["sub_amount"], ["AnimationNodeTimeScale"] = ["scale"], ["AnimationNodeTimeSeek"] = ["seek_request"],
        ["AnimationNodeOneShot"] = ["request", "active", "internal_active", "time_to_restart", "fade_in_remaining", "fade_out_remaining"],
        ["AnimationNodeTransition"] = ["current_state", "transition_request", "current_index", "prev_xfading", "prev_index"],
    };

    /// <summary>The parameters every node of a tree makes.</summary>
    private static readonly string[] EveryTreeNode = ["current_length", "current_position", "current_delta"];

    /// <summary>A tree's node's parameters under a path into <paramref name="found"/>, its children's under theirs (a state
    /// machine's states and its transitions' conditions, a blend tree's nodes); whether every node is of a known kind.</summary>
    private bool TreeNode(string res, GodotSection node, string path, GodotPlace place, Dictionary<string, List<GodotPlace>> found, Dictionary<string, string> kinds, int depth)
    {
        string? type = node.Tag == "sub_resource" ? node.Text("type") : scenes.Files[res].Header?.Text("type");
        if (type is null || !TreeNodeParameters.TryGetValue(type, out string[]? own) || depth > Deepest)
        {
            return false;
        }
        string kind = type["AnimationNode".Length..];
        foreach (string parameter in own.Concat(EveryTreeNode))
        {
            Add(found, path + parameter, place);
            kinds.TryAdd(path + parameter, kind);
        }
        GodotFile file = scenes.Files[res];
        bool whole = true;
        string children = type == "AnimationNodeStateMachine" ? "states/" : type == "AnimationNodeBlendTree" ? "nodes/" : string.Empty;
        foreach (GodotEntry property in node.Properties.Where(p => children.Length > 0 && p.Key.StartsWith(children, StringComparison.Ordinal) && p.Key.EndsWith("/node", StringComparison.Ordinal)))
        {
            string name = property.Key[children.Length..^"/node".Length];
            if (name.Length == 0 || name.Contains('/') || type == "AnimationNodeBlendTree" && name == "output")
            {
                continue;
            }
            int start = property.KeyStart + children.Length;
            GodotPlace named = new(res, start, start + name.Length, true, $"{Path.GetFileName(res)}:{file.LineOf(start) + 1}") { Plain = file.Text.AsSpan(property.KeyStart).StartsWith(property.Key, StringComparison.Ordinal) };
            whole &= Resource(res, property.Value) is ({ } at, { } child) && TreeNode(at, child, $"{path}{name}/", named, found, kinds, depth + 1);
        }
        if (type == "AnimationNodeStateMachine" && node.Property("transitions")?.Value is { } transitions)
        {
            foreach ((string id, _, _) in file.CallsIn(transitions, "SubResource"))
            {
                if (file.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == id)?.Property("advance_condition")?.Value is { Text: { Length: > 0 } condition } value)
                {
                    string parameter = $"{path}conditions/{condition}";
                    Add(found, parameter, new GodotPlace(res, value.TextStart, value.TextEnd, true, $"{Path.GetFileName(res)}:{file.LineOf(value.TextStart) + 1}") { Plain = value.Plain });
                    kinds.TryAdd(parameter, "condition");
                }
            }
        }
        return whole;
    }

    /// <summary>A name's places with those the project's GDScripts write it at: each string given straight to one of some calls.</summary>
    private IReadOnlyList<GodotDefined> Scripted(IReadOnlyList<GodotDefined> names, IReadOnlyDictionary<string, int[]> calls)
    {
        Dictionary<string, List<GodotPlace>> more = new(StringComparer.Ordinal);
        foreach ((string res, IReadOnlyList<GodotScriptToken> tokens) in scripts.Value)
        {
            foreach (GodotScriptToken token in GodotScriptStrings.GivenTo(tokens, calls))
            {
                Add(more, token.Text, ScriptPlace(res, token.Start, token.End, token.Plain));
            }
        }
        return [.. names.Select(n => more.TryGetValue(n.Name, out List<GodotPlace>? places) ? n with { Places = [.. n.Places, .. places] } : n)];
    }

    /// <summary>The autoloads' places with those each is named at elsewhere: the name in a node path through /root, in a
    /// GDScript's string or a scene's NodePath, and a GDScript's word naming its node, not after a '.'.</summary>
    private IReadOnlyList<GodotDefined> Autoloaded(IReadOnlyList<GodotDefined> names)
    {
        Dictionary<string, List<GodotPlace>> more = new(StringComparer.Ordinal);
        HashSet<string> named = [.. names.Select(n => n.Name)];
        foreach ((string res, IReadOnlyList<GodotScriptToken> tokens) in scripts.Value)
        {
            foreach (GodotScriptToken token in tokens)
            {
                string? name = token.Word ? token.AfterDot ? null : token.Text : RootName(token.Text);
                if (name is not null && named.Contains(name))
                {
                    int start = token.Word ? token.Start : token.Start + RootPath.Length;
                    Add(more, name, ScriptPlace(res, start, start + name.Length, token.Plain));
                }
            }
        }
        foreach ((string res, GodotFile file) in scenes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (GodotEntry entry in file.Sections.SelectMany(s => s.Attributes.Concat(s.Properties)))
            {
                foreach ((string path, int start, int end) in file.CallsIn(entry.Value, "NodePath"))
                {
                    if (RootName(path) is { } name && named.Contains(name) && end - start == path.Length)
                    {
                        int at = start + RootPath.Length;
                        Add(more, name, new GodotPlace(res, at, at + name.Length, false, $"{Path.GetFileName(res)}:{file.LineOf(at) + 1}"));
                    }
                }
            }
        }
        return [.. names.Select(n => more.TryGetValue(n.Name, out List<GodotPlace>? places) ? n with { Places = [.. n.Places, .. places] } : n)];

        static string? RootName(string path)
        {
            if (!path.StartsWith(RootPath, StringComparison.Ordinal))
            {
                return null;
            }
            int end = path.IndexOfAny(['/', ':'], RootPath.Length);
            string name = end < 0 ? path[RootPath.Length..] : path[RootPath.Length..end];
            return name.Length > 0 ? name : null;
        }
    }

    /// <summary>A place in a GDScript, a use of a name, as its words name it ("player.gd:12").</summary>
    private GodotPlace ScriptPlace(string res, int start, int end, bool plain) =>
        new(res, start, end, false, $"{Path.GetFileName(res)}:{LineOf(res, start) + 1}") { Plain = plain };

    /// <summary>project.godot's global shader uniforms, each with every shader's <c>global uniform</c> declaring it and its uses there.</summary>
    private IReadOnlyList<GodotDefined> ReadGlobals()
    {
        IReadOnlyList<GodotDefined> keys = Keys("shader_globals", "global shader uniform");
        if (keys.Count == 0)
        {
            return keys;
        }
        HashSet<string> named = [.. keys.Select(k => k.Name)];
        Dictionary<string, List<GodotPlace>> more = new(StringComparer.Ordinal);
        foreach (string shader in Shaders)
        {
            if (CodeOf(shader) is not ({ } res, { } text, int offset, bool plain))
            {
                continue;
            }
            List<(string Name, int Start, int End)> declared = [];
            List<(string Name, int Start, int End)> used = [];
            List<(string Name, int Start, int End)> global = [];
            ShaderTokens(text, declared, used, global);
            HashSet<string> own = [.. global.Select(g => g.Name).Where(named.Contains)];
            foreach ((string name, int start, int end) in global.Where(g => own.Contains(g.Name)).Concat(used.Where(u => own.Contains(u.Name))).OrderBy(p => p.Start))
            {
                Add(more, name, new GodotPlace(res, offset + start, offset + end, false, $"{Shown(shader)}:{LineOf(res, offset + start) + 1}") { Plain = plain });
            }
        }
        return [.. keys.Select(k => more.TryGetValue(k.Name, out List<GodotPlace>? places) ? k with { Places = [.. k.Places, .. places] } : k)];
    }

    /// <summary>The shader a node's material gives it, by the node's property holding the material (<c>material</c>): its
    /// file's res:// path, or for one written in a scene or resource, that file's path and the section's id after "::";
    /// null where the node's sections set no ShaderMaterial there, or it gives no shader.</summary>
    public string? ShaderOf(GodotTreeNode node, string member)
    {
        (string Res, GodotValue Value)? material = null;
        foreach ((string res, GodotNode section) in node.Written)
        {
            if (section.Section.Property(member) is { } property)
            {
                material = (res, property.Value);
            }
        }
        if (material is not ({ } at, { } value) || Resource(at, value) is not ({ } file, { } holder) || Shader(file, holder) is not { } shader)
        {
            return null;
        }
        return shader;
    }

    /// <summary>A shader's uniforms a material can set (neither <c>global</c> nor <c>instance</c> ones), its includes' too,
    /// each with its declaration and every use in the shader's code; and whether its text and every include was read. By
    /// the shader's res:// path, or a file's and a section's id for one written in a scene (<see cref="ShaderOf"/>).</summary>
    public (IReadOnlyList<GodotDefined> Names, bool Whole) Uniforms(string shader)
    {
        lock (reading)
        {
            if (uniforms.TryGetValue(shader, out (IReadOnlyList<GodotDefined>, bool) kept))
            {
                return kept;
            }
        }
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        bool whole = ReadShader(shader, found, [], 0);
        foreach ((string res, GodotFile file) in scenes.Files)
        {
            foreach (GodotSection section in file.Sections.Where(s => Shader(res, s) == shader))
            {
                foreach (GodotEntry property in section.Properties.Where(p => p.Key.StartsWith(ShaderParameter, StringComparison.Ordinal)))
                {
                    string name = property.Key[ShaderParameter.Length..];
                    if (found.ContainsKey(name) && file.Text.AsSpan(property.KeyStart).StartsWith(property.Key, StringComparison.Ordinal))
                    {
                        int start = property.KeyStart + ShaderParameter.Length;
                        Add(found, name, new GodotPlace(res, start, start + name.Length, false, $"{Path.GetFileName(res)}:{file.LineOf(start) + 1}"));
                    }
                }
            }
        }
        (IReadOnlyList<GodotDefined>, bool) read = ([.. found.Select(f => new GodotDefined(f.Key, $"uniform of {Shown(shader)}", f.Value))], whole);
        lock (reading)
        {
            uniforms[shader] = read;
        }
        return read;
    }

    /// <summary>The shaders a project's files hold: each .gdshader file and each Shader a scene or resource writes.</summary>
    public IReadOnlyList<string> Shaders =>
        [.. scenes.Texts.Keys.Where(k => k.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal),
            .. scenes.Files.OrderBy(f => f.Key, StringComparer.Ordinal).SelectMany(f => f.Value.Sections.Where(s => s.Tag == "sub_resource" && s.Text("type") == "Shader")
                .Select(s => $"{f.Key}::{s.Text("id")}"))];

    /// <summary>A shader as its words name it: its file's name, or for one a scene writes, the scene's.</summary>
    public static string Shown(string shader) => shader.Split("::")[0].Split('/')[^1];

    /// <summary>A place's line (from zero) in its file.</summary>
    public int LineOf(string res, int offset) =>
        scenes.Files.TryGetValue(res, out GodotFile? file) ? file.LineOf(offset) : scenes.Texts.TryGetValue(res, out string? text) ? LineIn(res, text, offset) : 0;

    /// <summary>A file's text as these were read from it; null for none.</summary>
    public string? TextOf(string res) => scenes.Files.TryGetValue(res, out GodotFile? file) ? file.Text : scenes.Texts.GetValueOrDefault(res);

    private IReadOnlyList<GodotDefined> Keys(string tag, string detail)
    {
        if (project.Value is not { } file || file.Sections.FirstOrDefault(s => s.Tag == tag) is not { } section)
        {
            return [];
        }
        List<GodotDefined> keys = [];
        foreach (GodotEntry property in section.Properties)
        {
            bool quoted = file.Text[property.KeyStart] == '"';
            int start = property.KeyStart + (quoted ? 1 : 0);
            bool plain = file.Text.AsSpan(start).StartsWith(property.Key, StringComparison.Ordinal);
            keys.Add(new GodotDefined(property.Key, detail, [new GodotPlace(ProjectRes, start, start + property.Key.Length, true, $"{GodotProject.FileName}:{file.LineOf(start) + 1}") { Plain = plain }]));
        }
        return keys;
    }

    private IReadOnlyList<GodotDefined> ReadGroups()
    {
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        foreach ((string res, GodotFile file) in scenes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (GodotNode node in file.Nodes)
            {
                if (node.Section.Attribute("groups")?.Value is { } value)
                {
                    foreach ((string group, int start, int end, bool plain) in StringsIn(file.Text, value))
                    {
                        Add(found, group, new GodotPlace(res, start, end, true, $"{Path.GetFileName(res)}:{file.LineOf(start) + 1}, {node.Name}") { Plain = plain });
                    }
                }
            }
        }
        return [.. found.Select(f => new GodotDefined(f.Key, $"group of {Plural.Of(f.Value.Count, "node")}", f.Value))];
    }

    private (IReadOnlyList<GodotDefined>, bool) ReadAnimations()
    {
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        Dictionary<string, string> details = new(StringComparer.Ordinal);
        List<GodotTreeNode> players = [];
        bool whole = true;
        foreach (string scene in scenes.Files.Keys.Order(StringComparer.Ordinal).Where(s => scenes.Files[s].Header?.Tag == "gd_scene"))
        {
            foreach (GodotTreeNode player in scenes.Tree(scene)?.Below().Where(IsPlayer) ?? [])
            {
                if (players.Any(p => p.Same(player)))
                {
                    continue;
                }
                players.Add(player);
                (IReadOnlyList<GodotDefined> names, bool read) = AnimationsOf(player);
                whole &= read;
                foreach (GodotDefined name in names)
                {
                    details.TryAdd(name.Name, name.Detail);
                    foreach (GodotPlace place in name.Places)
                    {
                        Add(found, name.Name, place);
                    }
                }
            }
        }
        return ([.. found.Select(f => new GodotDefined(f.Key, details[f.Key], f.Value))], whole);
    }

    private (IReadOnlyList<GodotDefined>, IReadOnlyList<string>, bool) ReadTranslations()
    {
        if (project.Value is not { } file || file.Sections.FirstOrDefault(s => s.Tag == "internationalization")?.Property("locale/translations") is not { } listed)
        {
            return ([], [], true);
        }
        List<string> files = [];
        bool whole = true;
        foreach ((string res, _, _, _) in StringsIn(file.Text, listed.Value))
        {
            string? read = res.EndsWith(".po", StringComparison.OrdinalIgnoreCase) ? res
                : res.EndsWith(".translation", StringComparison.OrdinalIgnoreCase) && CsvOf(res) is { } csv ? csv : null;
            if (read is null || !scenes.Texts.ContainsKey(read))
            {
                whole = false;
            }
            else if (!files.Contains(read))
            {
                files.Add(read);
            }
        }
        Dictionary<string, List<GodotPlace>> found = new(StringComparer.Ordinal);
        foreach (string res in files)
        {
            string text = scenes.Texts[res];
            IEnumerable<(string Key, int Start, int End, bool Plain)> keys = res.EndsWith(".po", StringComparison.OrdinalIgnoreCase) ? PoKeys(text) : CsvKeys(text);
            foreach ((string key, int start, int end, bool plain) in keys)
            {
                Add(found, key, new GodotPlace(res, start, end, true, $"{Path.GetFileName(res)}:{LineIn(res, text, start) + 1}") { Plain = plain });
            }
        }
        IReadOnlyList<GodotDefined> keyed = Scripted([.. found.Select(f => new GodotDefined(f.Key, "translation", f.Value))], TranslationCalls);
        return (keyed, files, whole);
    }

    /// <summary>The CSV a translation Godot imports from one names: <c>res://text.en.translation</c>'s <c>res://text.csv</c>.</summary>
    internal static string? CsvOf(string translation)
    {
        string stem = translation[..^".translation".Length];
        int dot = stem.LastIndexOf('.');
        return dot > stem.LastIndexOf('/') ? stem[..dot] + ".csv" : null;
    }

    /// <summary>A CSV translation's keys: the first cell of each row after the first (its locales), an empty one passed
    /// over; the cells parted by the delimiter its first row uses most (a comma, a semicolon or a tab).</summary>
    internal static IEnumerable<(string Key, int Start, int End, bool Plain)> CsvKeys(string text)
    {
        int firstEnd = text.IndexOf('\n') is var n and >= 0 ? n : text.Length;
        string first = text[..firstEnd];
        char delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => first.Count(c => c == d)).First();
        int at = 0;
        bool header = true;
        while (at < text.Length)
        {
            (string cell, int start, int end, bool plain, int next) = Cell(text, at, delimiter);
            // The rest of the row: each cell after one that ended at a delimiter, a quoted one's newlines held in it.
            int row = next;
            while (row < text.Length && text[row - 1] == delimiter)
            {
                row = Cell(text, row, delimiter).Next;
            }
            if (!header && cell.Length > 0)
            {
                yield return (cell, start, end, plain);
            }
            header = false;
            at = row <= at ? text.Length : row;
        }
    }

    /// <summary>A CSV cell from its start: what it says (a quoted one's doubled quotes read), where its characters lie, whether
    /// they are written as it says, and where the next cell or row starts (past its delimiter or line's end).</summary>
    private static (string Cell, int Start, int End, bool Plain, int Next) Cell(string text, int at, char delimiter)
    {
        if (at < text.Length && text[at] == '"')
        {
            StringBuilder said = new();
            int i = at + 1;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        said.Append('"');
                        i += 2;
                        continue;
                    }
                    break;
                }
                said.Append(text[i++]);
            }
            int end = i;
            int after = Math.Min(i + 1, text.Length);
            while (after < text.Length && text[after] is not '\n' && text[after] != delimiter)
            {
                after++;
            }
            return (said.ToString(), at + 1, end, end - at - 1 == said.Length, Math.Min(after + 1, text.Length));
        }
        int stop = at;
        while (stop < text.Length && text[stop] != delimiter && text[stop] != '\n')
        {
            stop++;
        }
        int trimmed = stop > at && text[stop - 1] == '\r' ? stop - 1 : stop;
        return (text[at..trimmed], at, trimmed, true, Math.Min(stop + 1, text.Length));
    }

    /// <summary>A PO file's msgids, each string after <c>msgid</c> with the strings on the lines after it joined; the header's
    /// empty one passed over. A msgid written over several lines, or with escapes, is read but not placed to be renamed.</summary>
    internal static IEnumerable<(string Key, int Start, int End, bool Plain)> PoKeys(string text)
    {
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at) is var n and >= 0 ? n : text.Length;
            string line = text[at..end].TrimEnd('\r');
            if (line.StartsWith("msgid \"", StringComparison.Ordinal))
            {
                int quote = at + "msgid ".Length;
                (string said, int close, _) = GodotFile.Quoted(text, quote);
                StringBuilder whole = new(said);
                bool joined = false;
                int next = end + 1;
                while (next < text.Length && text[next] == '"')
                {
                    (string more, _, _) = GodotFile.Quoted(text, next);
                    whole.Append(more);
                    joined = true;
                    next = text.IndexOf('\n', next) is var m and >= 0 ? m + 1 : text.Length;
                }
                if (whole.Length > 0)
                {
                    yield return (whole.ToString(), quote + 1, close, !joined && close - quote - 1 == said.Length);
                }
                at = Math.Max(next, end + 1);
                continue;
            }
            at = end + 1;
        }
    }

    /// <summary>A shader's uniforms into <paramref name="found"/>, its includes followed; whether its text and each include's was read.</summary>
    private bool ReadShader(string shader, Dictionary<string, List<GodotPlace>> found, HashSet<string> through, int depth)
    {
        if (depth > Deepest || !through.Add(shader))
        {
            return true;
        }
        if (CodeOf(shader) is not ({ } res, { } text, int offset, bool plain))
        {
            return false;
        }
        bool whole = true;
        List<(string Name, int Start, int End)> declared = [];
        List<(string Name, int Start, int End)> used = [];
        foreach ((string include, bool read) in ShaderTokens(text, declared, used))
        {
            whole &= read && ReadShader(include, found, through, depth + 1);
        }
        HashSet<string> own = [.. declared.Select(d => d.Name)];
        foreach ((string name, int start, int end) in declared)
        {
            Add(found, name, new GodotPlace(res, offset + start, offset + end, true, $"{Shown(shader)}:{LineOf(res, offset + start) + 1}") { Plain = plain });
        }
        foreach ((string name, int start, int end) in used.Where(u => own.Contains(u.Name) || found.ContainsKey(u.Name)))
        {
            Add(found, name, new GodotPlace(res, offset + start, offset + end, false, $"{Shown(shader)}:{LineOf(res, offset + start) + 1}") { Plain = plain });
        }
        return whole;
    }

    /// <summary>A shader's code: the file holding it, its text, where that text starts in the file, and whether places in it
    /// are places in the file (a Shader's code written in a scene without escapes); null for a shader not read.</summary>
    private (string Res, string Text, int Offset, bool Plain)? CodeOf(string shader)
    {
        if (shader.Split("::") is [{ } res, { } id])
        {
            return scenes.Files.TryGetValue(res, out GodotFile? file) && file.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == id)?.Property("code")?.Value
                is { Text: { } code } value ? (res, code, value.TextStart, value.Plain) : null;
        }
        if (scenes.Files.TryGetValue(shader, out GodotFile? resource))
        {
            return resource.Resource?.Property("code")?.Value is { Text: { } code } written ? (shader, code, written.TextStart, written.Plain) : null;
        }
        return scenes.Texts.TryGetValue(shader, out string? text) ? (shader, text, 0, true) : null;
    }

    /// <summary>A shader's uniforms a material sets (into <paramref name="declared"/>), its <c>global uniform</c>s (into
    /// <paramref name="global"/>, where given) and every other identifier of its code not after a '.' (into
    /// <paramref name="used"/>), its comments passed over; and each file it includes, by res:// path.</summary>
    internal static IReadOnlyList<(string Include, bool Read)> ShaderTokens(string text, List<(string Name, int Start, int End)> declared, List<(string Name, int Start, int End)> used,
        List<(string Name, int Start, int End)>? global = null)
    {
        List<(string, bool)> includes = [];
        List<(string Word, int Start, int End)> words = [];
        List<(int At, char Mark)> marks = [];
        int at = 0;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                at = text.IndexOf('\n', at) is var n and >= 0 ? n : text.Length;
            }
            else if (c == '/' && at + 1 < text.Length && text[at + 1] == '*')
            {
                at = text.IndexOf("*/", at + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : text.Length;
            }
            else if (c == '#')
            {
                int end = text.IndexOf('\n', at) is var n and >= 0 ? n : text.Length;
                string line = text[at..end];
                if (line.StartsWith("#include", StringComparison.Ordinal) && line.IndexOf('"') is var open and >= 0 && line.IndexOf('"', open + 1) is var shut and > 0)
                {
                    string include = line[(open + 1)..shut];
                    includes.Add((include, include.StartsWith("res://", StringComparison.Ordinal)));
                }
                at = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
                {
                    at++;
                }
                words.Add((text[start..at], start, at));
            }
            else
            {
                if (!char.IsWhiteSpace(c))
                {
                    marks.Add((at, c));
                }
                at++;
            }
        }
        for (int i = 0; i < words.Count; i++)
        {
            (string word, int start, int end) = words[i];
            if (word != "uniform")
            {
                char before = Before(marks, start);
                if (before != '.' && !(i > 0 && words[i - 1].Word == "uniform"))
                {
                    used.Add((word, start, end));
                }
                continue;
            }
            bool shared = i > 0 && words[i - 1].Word is "global" or "instance" && Between(marks, words[i - 1].End, start);
            if (shared && words[i - 1].Word == "instance")
            {
                continue;
            }
            // The uniform's name is the last word before its ':', '=', ';' or '['.
            int last = -1;
            for (int j = i + 1; j < words.Count; j++)
            {
                if (marks.Any(m => m.At >= words[j - 1].End && m.At < words[j].Start && m.Mark is ':' or '=' or ';' or '['))
                {
                    break;
                }
                last = j;
            }
            if (last > i)
            {
                (shared ? global ?? [] : declared).Add(words[last]);
            }
        }
        used.RemoveAll(u => declared.Any(d => d.Start == u.Start) || global?.Any(g => g.Start == u.Start) == true);
        return includes;

        static char Before(List<(int At, char Mark)> marks, int start)
        {
            int index = Sorted.FirstAtOrAfter(marks, static m => m.At, start) - 1;
            return index >= 0 ? marks[index].Mark : '\0';
        }

        static bool Between(List<(int At, char Mark)> marks, int from, int to) => !marks.Any(m => m.At > from && m.At < to);
    }

    /// <summary>The shader a ShaderMaterial section gives (its <c>shader</c>): its file's res:// path, or a file's path and a
    /// section's id for one written there; null for a section of no shader.</summary>
    private string? Shader(string res, GodotSection material)
    {
        if (!scenes.Files.TryGetValue(res, out GodotFile? file)
            || !(material.Tag == "sub_resource" ? material.Text("type") == "ShaderMaterial" : material.Tag == "resource" && file.Header?.Text("type") == "ShaderMaterial")
            || material.Property("shader")?.Value is not { Text: { } id } shader)
        {
            return null;
        }
        if (shader.Call == "SubResource")
        {
            return file.Sections.Any(s => s.Tag == "sub_resource" && s.Text("id") == id) ? $"{res}::{id}" : null;
        }
        return shader.Call == "ExtResource" && file.External(id)?.Path is { } path ? path : null;
    }

    /// <summary>The section a resource value names in the file writing it: a SubResource's section there, an ExtResource's
    /// file's own resource section; null for any other value, or one naming a file not read.</summary>
    private (string Res, GodotSection Section)? Resource(string res, GodotValue value)
    {
        if (!scenes.Files.TryGetValue(res, out GodotFile? file) || value.Text is not { } id)
        {
            return null;
        }
        if (value.Call == "SubResource")
        {
            return file.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == id) is { } section ? (res, section) : null;
        }
        return value.Call == "ExtResource" && file.External(id)?.Path is { } path && scenes.Files.TryGetValue(path, out GodotFile? other) && other.Resource is { } own
            ? (path, own) : null;
    }

    /// <summary>The values of an array value at its top, each read as a property's value is.</summary>
    internal static IReadOnlyList<GodotValue> ItemsOf(string text, GodotValue value)
    {
        List<GodotValue> items = [];
        if (value.Start >= value.End || text[value.Start] != '[')
        {
            return items;
        }
        int at = value.Start + 1;
        while (at < value.End)
        {
            while (at < value.End && (char.IsWhiteSpace(text[at]) || text[at] == ','))
            {
                at++;
            }
            if (at >= value.End || text[at] == ']')
            {
                break;
            }
            int end = ItemEnd(text, at, value.End);
            items.Add(ValueOf(text, at, end));
            at = end == at ? end + 1 : end;
        }
        return items;
    }

    /// <summary>The strings a value holds anywhere in it (an array's, a PackedStringArray's), each with where its characters
    /// lie and whether they are written as it says.</summary>
    internal static IEnumerable<(string Text, int Start, int End, bool Plain)> StringsIn(string text, GodotValue value)
    {
        int at = value.Start;
        while (at < value.End)
        {
            if (text[at] != '"')
            {
                at++;
                continue;
            }
            (string said, int close, int next) = GodotFile.Quoted(text, at);
            yield return (said, at + 1, close, close - at - 1 == said.Length);
            at = next;
        }
    }

    /// <summary>A dictionary's entry as Godot's variant text writes it (<c>&amp;"run": SubResource("Animation_1")</c>): its key's
    /// string and where its characters lie, and its value, read as a value of a property is.</summary>
    internal sealed record Entry(string Key, int KeyStart, int KeyEnd, bool KeyPlain, GodotValue Value);

    /// <summary>The entries of a dictionary value whose keys are strings or StringNames.</summary>
    internal static IReadOnlyList<Entry> DictionaryOf(string text, GodotValue value)
    {
        List<Entry> entries = [];
        if (value.Start >= value.End || text[value.Start] != '{')
        {
            return entries;
        }
        int at = value.Start + 1;
        while (at < value.End)
        {
            while (at < value.End && (char.IsWhiteSpace(text[at]) || text[at] == ','))
            {
                at++;
            }
            if (at >= value.End || text[at] == '}')
            {
                break;
            }
            int quote = text[at] == '&' ? at + 1 : at;
            if (quote >= value.End || text[quote] != '"')
            {
                break;
            }
            (string key, int close, int next) = GodotFile.Quoted(text, quote);
            at = next;
            while (at < value.End && text[at] is ' ' or '\t' or '\n' or '\r')
            {
                at++;
            }
            if (at >= value.End || text[at] != ':')
            {
                break;
            }
            int start = at + 1;
            while (start < value.End && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
            int end = ItemEnd(text, start, value.End);
            entries.Add(new Entry(key, quote + 1, close, close - quote - 1 == key.Length, ValueOf(text, start, end)));
            at = end;
        }
        return entries;
    }

    /// <summary>Where an item of a dictionary or array ends: at its ',' or the closing bracket, outside its own brackets and strings.</summary>
    private static int ItemEnd(string text, int at, int limit)
    {
        int depth = 0;
        while (at < limit)
        {
            char c = text[at];
            if (c == '"')
            {
                at = GodotFile.Quoted(text, at).Next;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth-- == 0)
                {
                    return at;
                }
            }
            else if (c == ',' && depth == 0)
            {
                return at;
            }
            at++;
        }
        return at;
    }

    /// <summary>A value between two offsets read as a property's is: a call's one string (<c>SubResource("x")</c>), a string's or a StringName's.</summary>
    private static GodotValue ValueOf(string text, int start, int end)
    {
        int stop = end;
        while (stop > start && char.IsWhiteSpace(text[stop - 1]))
        {
            stop--;
        }
        return GodotFile.ValueAt(text, start, stop);
    }

    private static void Add(Dictionary<string, List<GodotPlace>> found, string name, GodotPlace place)
    {
        if (!found.TryGetValue(name, out List<GodotPlace>? places))
        {
            found[name] = places = [];
        }
        if (!places.Any(p => p.Res == place.Res && p.Start == place.Start))
        {
            places.Add(place);
        }
    }

    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }
        return [.. starts];
    }

    private int LineIn(string res, string text, int offset)
    {
        int[]? starts;
        lock (reading)
        {
            if (!lines.TryGetValue(res, out starts))
            {
                lines[res] = starts = LineStarts(text);
            }
        }
        return Math.Max(0, Sorted.FirstAfter<int>(starts, offset) - 1);
    }
}
