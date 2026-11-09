using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>The cache of a project's global classes as Godot's editor writes it, and whether a run without the editor needs
/// Godot's import for it: every class declared as Godot 4.7.2 listed it (each base below is what it wrote for that script)
/// asks none, a body's edit asks none, and a class renamed, given another base, added, dropped or its script gone asks one;
/// with no cache, only a class_name does.</summary>
public class GodotClassCacheTests
{
    /// <summary>A cache's text as Godot 4.7.2 wrote it, three of its classes.</summary>
    private const string Written = """
        list=[{
        "base": &"Node",
        "class": &"Actor",
        "icon": "",
        "is_abstract": false,
        "is_tool": false,
        "language": &"GDScript",
        "path": "res://actor.gd"
        }, {
        "base": &"",
        "class": &"Dotted",
        "icon": "",
        "is_abstract": false,
        "is_tool": false,
        "language": &"GDScript",
        "path": "res://dotted.gd"
        }, {
        "base": &"Actor",
        "class": &"Hero",
        "icon": "res://icon.svg",
        "is_abstract": false,
        "is_tool": false,
        "language": &"GDScript",
        "path": "res://sub/hero.gd"
        }]

        """;

    /// <summary>The scripts of the project Godot 4.7.2 imported, by res:// path.</summary>
    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["res://actor.gd"] = "class_name Actor\nextends Node\n\nfunc _ready():\n\tpass\n",
        ["res://sub/hero.gd"] = "@icon(\"res://icon.svg\")\nclass_name Hero extends Actor\n",
        ["res://villain.gd"] = "extends \"res://actor.gd\"\nclass_name Villain\n",
        ["res://chain.gd"] = "class_name Chain\nextends \"res://villain.gd\"\n",
        ["res://holder.gd"] = "extends Node\n\nclass Inner extends Node2D:\n\tpass\n",
        ["res://deep.gd"] = "class_name Deep\nextends \"res://holder.gd\".Inner\n",
        ["res://bare.gd"] = "class_name Bare\n",
        ["res://dotted.gd"] = "class_name Dotted\nextends Actor.Nested\n",
        ["res://sub/rel.gd"] = "class_name Rel\nextends \"../actor.gd\"\n",
        ["res://sub/rel2.gd"] = "class_name RelRoot\nextends \"actor.gd\"\n",
        ["res://sub/rel3.gd"] = "class_name RelHere\nextends \"hero.gd\"\n",
        ["res://sub/holder2.gd"] = "extends Node\n\nclass Inner extends Sprite2D:\n\tpass\n",
        ["res://sub/deeper/rel5.gd"] = "class_name RelInnerSub\nextends \"../holder2.gd\".Inner\n",
        ["res://addons/x/tool.gd"] = "@tool\nclass_name XTool\nextends RefCounted\n",
        ["res://plain.gd"] = "extends Node\n",
    };

    /// <summary>What Godot 4.7.2 listed for those scripts: each class's base and path.</summary>
    private static readonly (string Base, string Class, string Path)[] Listed =
    [
        ("Node", "Actor", "res://actor.gd"), ("Actor", "Hero", "res://sub/hero.gd"), ("Node", "Villain", "res://villain.gd"),
        ("Node", "Chain", "res://chain.gd"), ("Node2D", "Deep", "res://deep.gd"), ("RefCounted", "Bare", "res://bare.gd"),
        ("", "Dotted", "res://dotted.gd"), ("", "Rel", "res://sub/rel.gd"), ("Node", "RelRoot", "res://sub/rel2.gd"), ("", "RelHere", "res://sub/rel3.gd"),
        ("", "RelInnerSub", "res://sub/deeper/rel5.gd"), ("RefCounted", "XTool", "res://addons/x/tool.gd"),
    ];

    /// <summary>A cache's text as Godot writes one, listing these classes.</summary>
    private static string Cache(IEnumerable<(string Base, string Class, string Path)> classes) =>
        "list=[" + string.Join(", ", classes.Select(c => $"{{\n\"base\": &\"{c.Base}\",\n\"class\": &\"{c.Class}\",\n\"icon\": \"\",\n\"is_abstract\": false,\n"
            + $"\"is_tool\": false,\n\"language\": &\"GDScript\",\n\"path\": \"{c.Path}\"\n}}")) + "]\n";

    private static bool Stale(string? cache, Dictionary<string, string> scripts, IEnumerable<string> newer, Func<string, bool>? exists = null) =>
        GodotClassCache.Stale(cache, newer, path => scripts.GetValueOrDefault(path), exists ?? scripts.ContainsKey);

    [Fact]
    public void Godots_own_cache_is_read_as_the_classes_it_lists_their_bases_and_scripts()
    {
        IReadOnlyList<GodotClassCache.Entry> read = GodotClassCache.Read(Written);

        GodotClassCache.Entry[] listed = [new("Actor", "Node", "res://actor.gd"), new("Dotted", string.Empty, "res://dotted.gd"), new("Hero", "Actor", "res://sub/hero.gd")];
        Assert.Equal(listed, read);
        Assert.Empty(GodotClassCache.Read("list=[]\n"));
    }

    [Fact]
    public void Every_class_declared_as_Godot_listed_it_asks_no_import()
    {
        Assert.False(Stale(Cache(Listed), Scripts, Scripts.Keys),
            "Scripts whose classes the cache lists as Godot 4.7.2 listed them asked for an import, so every Run after a body's edit would import again: "
            + string.Join(", ", Listed.Where(c => GodotClassCache.Declared(c.Path, Scripts[c.Path], path => Scripts.GetValueOrDefault(path))?.Base != c.Base)
                .Select(c => $"{c.Class} read as {GodotClassCache.Declared(c.Path, Scripts[c.Path], path => Scripts.GetValueOrDefault(path))?.Base ?? "(unsure)"}, listed {c.Base}")));
    }

    [Fact]
    public void A_body_edit_asks_no_import_and_a_class_renamed_rebased_added_dropped_or_gone_asks_one()
    {
        string cache = Cache(Listed);
        Dictionary<string, string> Edited(string path, string text) => new(Scripts, StringComparer.Ordinal) { [path] = text };

        Assert.False(Stale(cache, Edited("res://actor.gd", "class_name Actor\nextends Node\n\nfunc _ready():\n\tprint(\"ready\")\n"), ["res://actor.gd"]),
            "A function's body edited in a class_name script asked for an import");
        Assert.False(Stale(cache, Edited("res://plain.gd", "extends Node\n\nvar speed := 2\n"), ["res://plain.gd"]), "A script declaring no class asked for an import");
        Assert.True(Stale(cache, Edited("res://actor.gd", "class_name Player\nextends Node\n"), ["res://actor.gd"]), "A class renamed asked for no import");
        Assert.True(Stale(cache, Edited("res://actor.gd", "class_name Actor\nextends Node2D\n"), ["res://actor.gd"]), "A class given another base asked for no import");
        Assert.True(Stale(cache, Edited("res://plain.gd", "class_name Plain\nextends Node\n"), ["res://plain.gd"]), "A class_name added asked for no import");
        Assert.True(Stale(cache, Edited("res://bare.gd", "extends RefCounted\n"), ["res://bare.gd"]), "A class_name dropped asked for no import");
        Assert.True(Stale(cache, Scripts, [], path => path != "res://bare.gd"), "A listed class's script gone asked for no import");
    }

    [Fact]
    public void With_no_cache_only_a_script_declaring_a_class_name_asks_an_import()
    {
        Assert.False(Stale(null, Scripts, ["res://plain.gd", "res://holder.gd"]), "A project of no class_name asked for an import for its global classes");
        Assert.True(Stale(null, Scripts, ["res://plain.gd", "res://bare.gd"]), "A class_name in a project never imported asked for no import");
    }
}
