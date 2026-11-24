using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;
using Passenger.Workspace.GodotShader;

namespace Passenger.Tests;

/// <summary>Passenger's ports of Godot held to Godot itself: GDScripts, shaders and binary scenes made at random from a seed,
/// each read by Passenger and by a headless Godot 4.7 or 4.8, whose errors and readings must be Passenger's, in the same words on the same
/// lines. A difference is cut down a line at a time to the smallest text still differing and named with its seed; what differs
/// on purpose is listed with why. Without either Godot the test skips saying why.</summary>
[Collection(Beside.Name)]
public sealed partial class GodotDifferentialTests
{
    /// <summary>The Godots the tests are written for: CI's, and the 4.8 Passenger's ports follow.</summary>
    private static readonly string[] Pinned = ["4.7", "4.8"];

    private const int Seed = 2095;

    private const int Written = 120;

    /// <summary>What Godot says of a script that Passenger does not, on purpose, and why.</summary>
    private static readonly (Regex Said, string Why)[] Kept =
    [
        (KeptConstant(), "Godot's loader gives a constant of a type its target takes none of a second error at the same place, "
            + "which Passenger gives only where Godot's server does, its other error there saying the same"),
    ];

    /// <summary>What Godot says of scripts and shaders written at random, and how it reads binary scenes, Passenger's own.</summary>
    [Fact]
    public Task Passengers_ports_read_what_is_made_at_random_as_godot_does() => Cases.AllAsync(
        (nameof(Gdscript_errors_are_godots_own_on_scripts_written_at_random), Gdscript_errors_are_godots_own_on_scripts_written_at_random),
        (nameof(A_shaders_first_error_is_godots_own_on_shaders_written_at_random), A_shaders_first_error_is_godots_own_on_shaders_written_at_random),
        (nameof(A_binary_scene_is_read_and_moved_as_godot_reads_it), A_binary_scene_is_read_and_moved_as_godot_reads_it));

    private static async Task Gdscript_errors_are_godots_own_on_scripts_written_at_random()
    {
        GodotFound godot = Pinned4();
        string folder = PathNames.Canonical(TestFolders.Create("differential-"));
        try
        {
            Dictionary<string, string> homes = GodotUserFolders.Under(Path.Combine(folder, "home"));
            GodotEngineApi? api = await new GodotEngineApis(() => godot.Executable, () => homes).ApiAsync();
            Assert.True(api is not null, "Godot gave no API of its engine, so no script could be read as it reads it");
            Loader loads = new(godot, folder, homes, "gd", ScriptsLoader);
            Random random = new(Seed);
            await Compare(loads, [.. Enumerable.Range(0, Written).Select(_ => Script(random))], script => Ours(api!, script), "GDScript");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private static async Task A_shaders_first_error_is_godots_own_on_shaders_written_at_random()
    {
        GodotFound godot = Pinned4();
        string folder = PathNames.Canonical(TestFolders.Create("differential-"));
        try
        {
            Loader loads = new(godot, folder, GodotUserFolders.Under(Path.Combine(folder, "home")), "gdshader", ShadersLoader);
            Random random = new(Seed);
            Version version = Version.Parse(Pinned.First(p => godot.Version!.StartsWith(p + ".", StringComparison.Ordinal)));
            await Compare(loads, [.. Enumerable.Range(0, Written).Select(_ => Shader(random))], shader => OursOfShader(shader, version), "shader");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Scenes Godot saves in its binary format, plain, compressed or big-endian at random, naming a few meshes:
    /// Passenger reads each one's uid and the resources it names as Godot's own reading does, and once Passenger moves
    /// those meshes and rewrites the scenes, Godot names them where they went and loads each scene it can. Godot 4.7 cannot
    /// load a big-endian scene it saved itself, so such a scene's rewrite is held to Godot's reading of what it names.</summary>
    private static async Task A_binary_scene_is_read_and_moved_as_godot_reads_it()
    {
        GodotFound godot = Pinned4();
        string folder = PathNames.Canonical(TestFolders.Create("differential-"));
        try
        {
            Dictionary<string, string> homes = GodotUserFolders.Under(Path.Combine(folder, "home"));
            string project = Path.Combine(folder, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "project.godot"), "config_version=5\n\n[application]\n\nconfig/name=\"Differential\"\n");
            Random random = new(Seed);
            string[] meshes = ["QuadMesh", "BoxMesh", "SphereMesh", "PlaneMesh"];
            string[] flags = ["0", "ResourceSaver.FLAG_COMPRESS", "ResourceSaver.FLAG_SAVE_BIG_ENDIAN", "ResourceSaver.FLAG_COMPRESS | ResourceSaver.FLAG_SAVE_BIG_ENDIAN"];
            int[] counts = [.. Enumerable.Range(0, Scenes).Select(_ => random.Next(1, 5))];
            string[] saved = new string[Scenes];
            StringBuilder make = new("extends SceneTree\n\n\nfunc _init() -> void:\n");
            for (int i = 0; i < Scenes; i++)
            {
                make.Append(CultureInfo.InvariantCulture, $"\tDirAccess.make_dir_recursive_absolute(\"res://c{i}\")\n\tvar root{i} := Node3D.new()\n");
                for (int j = 0; j < counts[i]; j++)
                {
                    string mesh = $"res://c{i}/m{j}.tres";
                    make.Append(CultureInfo.InvariantCulture, $"\tResourceSaver.save({meshes[random.Next(meshes.Length)]}.new(), \"{mesh}\")\n"
                        + $"\tvar shown{i}_{j} := MeshInstance3D.new()\n\tshown{i}_{j}.mesh = load(\"{mesh}\")\n\troot{i}.add_child(shown{i}_{j})\n\tshown{i}_{j}.owner = root{i}\n");
                }
                make.Append(CultureInfo.InvariantCulture, $"\tvar packed{i} := PackedScene.new()\n\tpacked{i}.pack(root{i})\n"
                    + $"\tResourceSaver.save(packed{i}, \"res://c{i}/scene.scn\", {saved[i] = flags[random.Next(flags.Length)]})\n"
                    + $"\tvar scene{i} := ResourceUID.create_id()\n\tResourceSaver.set_uid(\"res://c{i}/scene.scn\", scene{i})\n"
                    + $"\tprinterr(\"== {i} \", ResourceUID.id_to_text(scene{i}), \" \", \" \".join(ResourceLoader.get_dependencies(\"res://c{i}/scene.scn\")))\n"
                    + $"\troot{i}.free()\n");
            }
            string made = await RunAsync(godot, project, homes, "make.gd", make.Append("\tprinterr(\"== end\")\n\tquit()\n").ToString());
            List<string> differing = [];
            StringBuilder load = new("extends SceneTree\n\n\nfunc _init() -> void:\n");
            Dictionary<int, string[]> moved = [];
            foreach (Match told in SceneTold().Matches(made))
            {
                int i = int.Parse(told.Groups[1].Value, CultureInfo.InvariantCulture);
                GodotBinaryResource? read = GodotBinaryResource.Read(File.ReadAllBytes(Path.Combine(project, $"c{i}", "scene.scn")), out string? unread);
                string[] godots = [.. told.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Dependency)];
                string[] ours = read is null ? [] : [.. read.Externals.Select(e => $"{e.Path} {e.Uid}")];
                if (read?.Uid != told.Groups[2].Value || !godots.Order(StringComparer.Ordinal).SequenceEqual(ours.Order(StringComparer.Ordinal)))
                {
                    differing.Add($"Scene {i} of seed {Seed}, saved with {saved[i]}: Godot wrote {told.Groups[2].Value} naming {string.Join(", ", godots)}; "
                        + $"Passenger read {read?.Uid ?? unread} naming {string.Join(", ", ours)}");
                    continue;
                }
                GodotBinaryExternal[] to = [.. read!.Externals.Select(e => e with { Path = e.Path.Replace($"res://c{i}/", $"res://c{i}/moved/", StringComparison.Ordinal) })];
                Directory.CreateDirectory(Path.Combine(project, $"c{i}", "moved"));
                for (int j = 0; j < counts[i]; j++)
                {
                    File.Move(Path.Combine(project, $"c{i}", $"m{j}.tres"), Path.Combine(project, $"c{i}", "moved", $"m{j}.tres"));
                }
                File.WriteAllBytes(Path.Combine(project, $"c{i}", "scene.scn"), read.With(null, to));
                moved[i] = [.. to.Select(e => e.Path).Order(StringComparer.Ordinal)];
                load.Append(CultureInfo.InvariantCulture, $"\tprinterr(\"== {i} \", \" \".join(ResourceLoader.get_dependencies(\"res://c{i}/scene.scn\")))\n");
                if (!saved[i].Contains("BIG_ENDIAN", StringComparison.Ordinal))
                {
                    load.Append(CultureInfo.InvariantCulture, $"\tvar shown{i} = ResourceLoader.load(\"res://c{i}/scene.scn\", \"\", ResourceLoader.CACHE_MODE_IGNORE).instantiate()\n"
                        + $"\tprinterr(\"== {i} loaded \", \" \".join(shown{i}.get_children().map(func(c): return c.mesh.resource_path)))\n\tshown{i}.free()\n");
                }
            }
            Assert.True(differing.Count == 0, $"Passenger reads {differing.Count} of Godot's {Scenes} binary scenes otherwise than Godot does, so a move "
                + $"would follow the wrong resources:\n{string.Join("\n", differing)}");
            string loaded = await RunAsync(godot, project, homes, "load.gd", load.Append("\tprinterr(\"== end\")\n\tquit()\n").ToString());
            foreach ((int i, string[] to) in moved)
            {
                string expected = string.Join(" ", to);
                string? named = Regex.Match(loaded, $"^== {i} (res://.*|uid://.*)$", RegexOptions.Multiline) is { Success: true } line
                    ? string.Join(" ", line.Groups[1].Value.Split(' ').Select(Dependency).Select(d => d.Split(' ')[0]).Order(StringComparer.Ordinal)) : null;
                string? shown = Regex.Match(loaded, $"^== {i} loaded (.*)$", RegexOptions.Multiline) is { Success: true } instanced
                    ? string.Join(" ", instanced.Groups[1].Value.Split(' ').Order(StringComparer.Ordinal)) : null;
                if (named != expected || !saved[i].Contains("BIG_ENDIAN", StringComparison.Ordinal) && shown != expected)
                {
                    differing.Add($"Scene {i} of seed {Seed}, saved with {saved[i]}, its meshes moved: Godot names {named ?? "nothing"} and loads "
                        + $"{shown ?? "nothing"} where Passenger wrote {expected}");
                }
            }
            Assert.True(differing.Count == 0, $"Godot reads {differing.Count} of the scenes Passenger rewrote otherwise than Passenger wrote them, so a "
                + $"move leaves a scene naming what is no longer there:\n{string.Join("\n", differing)}");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private const int Scenes = 24;

    /// <summary>A resource Godot names a scene depending on, as its path and uid, the uid empty where it names none.</summary>
    private static string Dependency(string named)
    {
        string[] parts = named.Split("::");
        return $"{parts.First(p => p.StartsWith("res://", StringComparison.Ordinal))} {parts.FirstOrDefault(p => p.StartsWith("uid://", StringComparison.Ordinal))}";
    }

    /// <summary>What a headless Godot printed to its errors running a script written into the project, which ends saying so.</summary>
    private static async Task<string> RunAsync(GodotFound godot, string project, IReadOnlyDictionary<string, string> homes, string name, string script)
    {
        File.WriteAllText(Path.Combine(project, name), script);
        ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(GodotExecutables.InTerminal(godot.Executable),
            ["--headless", "--path", project, "--script", $"res://{name}"], project, homes), limit: TimeSpan.FromMinutes(2).Scaled());
        Assert.True(ran.Error.Contains("== end", StringComparison.Ordinal),
            $"Godot never ran {name} to its end (exit {ran.ExitCode}): {ran.Error[^Math.Min(2000, ran.Error.Length)..]}");
        return ran.Error.ReplaceLineEndings("\n");
    }

    private static GodotFound Pinned4()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { WhyNotScripts: null, Version: { } version } || !Pinned.Any(p => version.StartsWith(p + ".", StringComparison.Ordinal)),
            $"No Godot {string.Join(" or ", Pinned)} was found (GODOT, the PATH, Applications), whose own errors Passenger's are held to");
        return godot!;
    }

    /// <summary>Each text's errors by Godot and by Passenger, every difference cut down to its smallest and said at once.</summary>
    private static async Task Compare(Loader godot, string[] texts, Func<string, HashSet<(int, string)>> ours, string kind)
    {
        IReadOnlyList<HashSet<(int, string)>> godots = await godot.ErrorsAsync(texts);
        List<string> differing = [];
        for (int i = 0; i < texts.Length; i++)
        {
            if (!Same(godots[i], ours(texts[i])))
            {
                (string smallest, HashSet<(int, string)> said) = await SmallestAsync(godot, texts[i], godots[i], ours);
                differing.Add($"{kind} {i} of seed {Seed}, where Godot says {Told(godots[i])} and Passenger {Told(ours(texts[i]))}, cut to:\n{smallest}\n"
                    + $"Godot says: {Told(said)}\nPassenger says: {Told(ours(smallest))}");
            }
        }
        Assert.True(differing.Count == 0, $"Passenger's {kind} errors differ from Godot's on {differing.Count} of {texts.Length}, so a person is told of "
            + "an error Godot never gives or not of one it does:\n\n" + string.Join("\n\n", differing));
    }

    private static bool Same(HashSet<(int Line, string Said)> godot, HashSet<(int Line, string Said)> ours) => Differing(godot, ours).Count == 0;

    /// <summary>What one says that the other does not, by its words alone, so a text cut down keeps the same difference
    /// however its lines moved.</summary>
    private static HashSet<string> Differing(HashSet<(int Line, string Said)> godot, HashSet<(int Line, string Said)> ours)
    {
        HashSet<(int, string)> godots = [.. godot.Where(e => !Kept.Any(k => k.Said.IsMatch(e.Said)))];
        HashSet<(int, string)> passengers = [.. ours.Where(e => !Kept.Any(k => k.Said.IsMatch(e.Item2)))];
        return [.. godots.Except(passengers).Concat(passengers.Except(godots)).Select(e => e.Item2)];
    }

    /// <summary>The text cut down a line at a time while it still differs in the same words, every candidate of a round
    /// loaded by one Godot.</summary>
    private static async Task<(string Text, HashSet<(int, string)> Said)> SmallestAsync(Loader godot, string text, HashSet<(int, string)> godotSaid,
        Func<string, HashSet<(int, string)>> ours)
    {
        string smallest = text;
        HashSet<(int, string)> said = godotSaid;
        HashSet<string> difference = Differing(said, ours(text));
        while (true)
        {
            string[] lines = smallest.Split('\n');
            string[] candidates = [.. Enumerable.Range(1, lines.Length - 1).Where(i => lines[i].Length > 0)
                .Select(i => string.Join('\n', lines.Where((_, k) => k != i)))];
            if (candidates.Length == 0)
            {
                return (smallest, said);
            }
            IReadOnlyList<HashSet<(int, string)>> godots = await godot.ErrorsAsync(candidates);
            int differs = Enumerable.Range(0, candidates.Length).FirstOrDefault(i => Differing(godots[i], ours(candidates[i])).SetEquals(difference), -1);
            if (differs < 0)
            {
                return (smallest, said);
            }
            (smallest, said) = (candidates[differs], godots[differs]);
        }
    }

    private static string Told(IEnumerable<(int Line, string Said)> errors) =>
        string.Join("; ", errors.OrderBy(e => e.Line).Select(e => $"{e.Line}: {e.Said}")) is { Length: > 0 } told ? told : "nothing";

    /// <summary>A script of a few members and statements, some naming what it never declares, giving a value its target
    /// takes none of, calling with too few or many arguments or written in a way Godot's parser refuses.</summary>
    private static string Script(Random random)
    {
        string[] types = ["int", "float", "String", "bool", "Vector2", "Array", "Dictionary", "Node"];
        string[] values = ["1", "2.5", "\"word\"", "true", "Vector2(1, 2)", "[1, 2]", "{\"a\": 1}", "null"];
        List<string> names = [];
        StringBuilder text = new("extends Node\n\n");
        int members = random.Next(1, 4);
        for (int i = 0; i < members; i++)
        {
            int type = random.Next(types.Length);
            string value = values[random.Next(10) < 7 ? type : random.Next(values.Length)];
            bool constant = random.Next(4) == 0;
            text.Append(constant ? $"const M{i} = {value}\n" : $"var m{i}: {types[type]} = {value}\n");
            names.Add(constant ? $"M{i}" : $"m{i}");
        }
        // A mistake Godot's parser finds now and then: a stray indented line, a nameless variable, a function's missing colon.
        if (random.Next(6) == 0)
        {
            text.Append(random.Next(3) switch
            {
                0 => "\tvar stray := 1\n",
                1 => "var = 1\n",
                _ => "\n\nfunc g()\n\tpass\n",
            });
        }
        int parameters = random.Next(0, 3);
        string declared = string.Join(", ", Enumerable.Range(0, parameters).Select(p => $"p{p}: int"));
        text.Append(CultureInfo.InvariantCulture, $"\n\nfunc f({declared}) -> int:\n\treturn {(random.Next(5) == 0 ? "\"no\"" : "1")}\n\n\nfunc _ready() -> void:\n");
        int statements = random.Next(1, 5);
        for (int i = 0; i < statements; i++)
        {
            string used = random.Next(6) == 0 ? "missing" : names[random.Next(names.Count)];
            int given = random.Next(10) < 7 ? parameters : random.Next(4);
            text.Append(random.Next(4) switch
            {
                0 => $"\tvar l{i}: {types[random.Next(types.Length)]} = {used}\n",
                1 => $"\tprint({used} + {values[random.Next(values.Length)]})\n",
                2 => $"\tvar c{i} := f({string.Join(", ", Enumerable.Repeat("1", given))})\n",
                _ => $"\t{used} = {values[random.Next(values.Length)]}\n",
            });
        }
        return text.ToString();
    }

    /// <summary>A shader of a few uniforms and statements in its processor functions, some naming what it never declares or
    /// giving a value its target takes none of.</summary>
    private static string Shader(Random random)
    {
        string[] types = ["float", "vec2", "vec3", "vec4", "int", "bool"];
        string[] values = ["1.0", "vec2(1.0)", "vec3(1.0)", "vec4(1.0)", "1", "true"];
        bool spatial = random.Next(2) == 0;
        StringBuilder text = new(spatial ? "shader_type spatial;\n\n" : "shader_type canvas_item;\n\n");
        List<string> names = [];
        int uniforms = random.Next(1, 4);
        for (int i = 0; i < uniforms; i++)
        {
            int type = random.Next(types.Length);
            string value = values[random.Next(10) < 8 ? type : random.Next(values.Length)];
            text.Append(CultureInfo.InvariantCulture, $"uniform {types[type]} u{i} = {value};\n");
            names.Add($"u{i}");
        }
        text.Append("\nvoid fragment() {\n");
        int statements = random.Next(1, 4);
        for (int i = 0; i < statements; i++)
        {
            string used = random.Next(6) == 0 ? "missing" : names[random.Next(names.Count)];
            string built = spatial ? (random.Next(2) == 0 ? "ALBEDO" : "ALPHA") : (random.Next(2) == 0 ? "COLOR" : "COLOR.a");
            text.Append(random.Next(3) switch
            {
                0 => $"\t{types[random.Next(types.Length)]} l{i} = {used};\n",
                1 => $"\t{built} = {values[random.Next(values.Length)]};\n",
                _ => $"\t{built} = {used};\n",
            });
        }
        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>A script's errors as Passenger reads it against the engine's API, by its line.</summary>
    private static HashSet<(int Line, string Said)> Ours(GodotEngineApi api, string script)
    {
        GDScriptTree tree = GDScriptTree.Parse(script);
        // Godot's loader says only the first of a script's parse errors, where its analyzer's it says whole.
        if (tree.Errors.Count > 0)
        {
            GDScriptError first = tree.Errors.MinBy(e => e.Start);
            return [(script.AsSpan(0, first.Start).Count('\n') + 1, first.Message)];
        }
        GDScriptAnalyzer analyzer = new(new GDScriptProject(api, path => path == "res://s.gd" ? tree : null, new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)));
        return [.. analyzer.Analyze(tree, "res://s.gd").Findings(GDScriptWarningSettings.Of(null)).Where(f => f.IsError)
            .Select(f => (script.AsSpan(0, f.Start).Count('\n') + 1, f.Message))];
    }

    /// <summary>A shader's first error as Passenger checks it for a Godot of <paramref name="godot"/>'s major and minor
    /// version, which is all Godot says as it compiles one.</summary>
    private static HashSet<(int Line, string Said)> OursOfShader(string shader, Version godot)
    {
        GodotShaderChecked check = GodotShaderCheck.Check(new GodotShaderUnit("res://s.gdshader", shader, new Dictionary<string, string>(), []),
            GodotShaderTable.Carried, new GodotShaderCheckOptions { Godot = godot });
        return [.. check.Messages.Where(m => m.Error && !m.Note).Take(1).Select(m => (m.Line, m.Text))];
    }

    /// <summary>The script a headless Godot runs to load each script written, its errors printed after its marker.</summary>
    private const string ScriptsLoader = "\t\tResourceLoader.load(\"res://s%d.gd\" % i, \"\", ResourceLoader.CACHE_MODE_IGNORE)\n";

    /// <summary>The script a headless Godot runs to compile each shader written, its first error printed after its marker.</summary>
    private const string ShadersLoader = "\t\tvar shader := Shader.new()\n\t\tshader.code = FileAccess.get_file_as_string(\"res://s%d.gdshader\" % i)\n"
        + "\t\tshader.get_rid()\n";

    /// <summary>A headless Godot loading the texts written into a project of their own, a run for every batch.</summary>
    private sealed class Loader(GodotFound godot, string folder, IReadOnlyDictionary<string, string> homes, string extension, string load)
    {
        /// <summary>Each text's errors as Godot loads it, by its line.</summary>
        public async Task<IReadOnlyList<HashSet<(int Line, string Said)>>> ErrorsAsync(IReadOnlyList<string> texts)
        {
            string project = Path.Combine(folder, "project");
            TestFolders.Delete(project);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "project.godot"), "config_version=5\n\n[application]\n\nconfig/name=\"Differential\"\n");
            for (int i = 0; i < texts.Count; i++)
            {
                File.WriteAllText(Path.Combine(project, $"s{i}.{extension}"), texts[i]);
            }
            File.WriteAllText(Path.Combine(project, "load_each.gd"), "extends SceneTree\n\n\nfunc _init() -> void:\n"
                + $"\tfor i in {texts.Count}:\n\t\tprinterr(\"== %d\" % i)\n{load}\tprinterr(\"== end\")\n\tquit()\n");
            ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(GodotExecutables.InTerminal(godot.Executable),
                ["--headless", "--path", project, "--script", "res://load_each.gd"], project, homes), limit: TimeSpan.FromMinutes(2).Scaled());
            Assert.True(ran.Error.Contains("== end", StringComparison.Ordinal), $"Godot never loaded every text: {ran.Error[^Math.Min(2000, ran.Error.Length)..]}");
            List<HashSet<(int, string)>> found = [.. texts.Select(_ => new HashSet<(int, string)>())];
            int at = -1;
            string? said = null;
            foreach (string line in ran.Error.ReplaceLineEndings("\n").Split('\n'))
            {
                if (Marker().Match(line) is { Success: true } marker)
                {
                    at = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                else if (Error().Match(line) is { Success: true } error)
                {
                    said = error.Groups[1].Value;
                }
                else if (said is not null && at >= 0 && Place().Match(line) is { Success: true } place)
                {
                    found[at].Add((int.Parse(place.Groups[1].Value, CultureInfo.InvariantCulture), said));
                    said = null;
                }
            }
            return found;
        }
    }

    [GeneratedRegex(@"^== (\d+)$")]
    private static partial Regex Marker();

    [GeneratedRegex(@"^Cannot (?:assign|return) a value of type "".*"" as "".*""\.$")]
    private static partial Regex KeptConstant();

    [GeneratedRegex(@"^== (\d+) (uid://\S+) (.*)$", RegexOptions.Multiline)]
    private static partial Regex SceneTold();

    [GeneratedRegex(@"^(?:SCRIPT ERROR: Parse Error|SHADER ERROR): (.*)$")]
    private static partial Regex Error();

    [GeneratedRegex(@"at: (?:GDScript::reload \(res://s\d+\.gd|\(null\) \():(\d+)\)")]
    private static partial Regex Place();
}
