using System.Diagnostics;
using System.Text.Json;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.GodotSweep;

/// <summary>Parses every .gd under a folder with Passenger's GDScript parser and writes, as JSON on standard output, each
/// file's errors (lines and columns from 1, columns in UTF-16 units as the editor counts them), each of a few seeded edits'
/// parse compared with a whole parse of the edited text, and on the largest script how long a whole parse and a key's
/// parse take, what each allocates and what a tree holds.</summary>
public static class Program
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { ".godot", ".git", ".import", "node_modules" };

    public static int Main(string[] args)
    {
        if (args is ["--types", var project, var api] && Directory.Exists(project) && File.Exists(api))
        {
            Console.WriteLine(JsonSerializer.Serialize(Types(Path.GetFullPath(project), GodotEngineApi.Read(File.ReadAllBytes(api)))));
            return 0;
        }
        if (args is ["--warnings", var tests, var engine] && Directory.Exists(tests) && File.Exists(engine))
        {
            Console.WriteLine(JsonSerializer.Serialize(Warnings(Path.GetFullPath(tests), GodotEngineApi.Read(File.ReadAllBytes(engine)))));
            return 0;
        }
        if (args is ["--format-time", var timed] && File.Exists(timed))
        {
            string script = File.ReadAllText(timed).ReplaceLineEndings("\n");
            double least = double.MaxValue;
            double unwrapped = double.MaxValue;
            double parse = double.MaxValue;
            for (int round = 0; round < 20; round++)
            {
                Stopwatch clock = Stopwatch.StartNew();
                GDScriptFormatter.Format(script, new GDScriptFormatOptions());
                least = Math.Min(least, clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                GDScriptFormatter.Format(script, new GDScriptFormatOptions() with { LineLength = 0 });
                unwrapped = Math.Min(unwrapped, clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                GDScriptTree.Parse(script);
                parse = Math.Min(parse, clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{Path.GetFileName(timed)}: format {least:F1} ms ({unwrapped:F1} ms not wrapping), one parse {parse:F1} ms");
            return 0;
        }
        if (args is ["--format-one", var one] && File.Exists(one))
        {
            GDScriptFormatted laid = GDScriptFormatter.Format(File.ReadAllText(one).ReplaceLineEndings("\n"), new GDScriptFormatOptions());
            Console.Error.WriteLine(laid.Refused ?? "formatted");
            Console.Write(laid.Attempted ?? laid.Text);
            return 0;
        }
        if (args is ["--format", var formatted] && Directory.Exists(formatted))
        {
            Console.WriteLine(JsonSerializer.Serialize(Formats(Path.GetFullPath(formatted))));
            return 0;
        }
        if (args.Length != 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Passenger.GodotSweep: takes one argument, a folder whose .gd files to parse");
            return 1;
        }
        List<string> scripts = [];
        Walk(Path.GetFullPath(args[0]), scripts);
        List<object> files = [];
        List<object> mismatches = [];
        Stopwatch total = new();
        (string Path, string Text)? largest = null;
        foreach (string path in scripts)
        {
            string text = File.ReadAllText(path);
            total.Start();
            GDScriptTree tree = GDScriptTree.Parse(text);
            total.Stop();
            TextSnapshot snapshot = TextSnapshot.From(text);
            files.Add(new
            {
                path,
                errors = tree.Errors.Select(e => snapshot.GetPosition(Math.Min(e.Start, text.Length)) is var at
                    ? new { line = at.Line + 1, column = at.Column + 1, message = e.Message } : null),
            });
            mismatches.AddRange(Edits(path, text, tree));
            if (largest is null || text.Length > largest.Value.Text.Length)
            {
                largest = (path, text);
            }
        }
        object? speed = largest is { } big ? Speed(big.Path, big.Text) : null;
        Console.WriteLine(JsonSerializer.Serialize(new { files, totalMilliseconds = Math.Round(total.Elapsed.TotalMilliseconds, 1), editsMismatched = mismatches, largest = speed }));
        return 0;
    }

    /// <summary>Each script's members of a Godot project as Passenger's analyzer types them, written as Godot's server's
    /// symbols write them (their detail), by res:// path.</summary>
    private static object Types(string folder, GodotEngineApi api)
    {
        List<string> scripts = [];
        Walk(folder, scripts);
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal);
        Dictionary<string, string> classNames = new(StringComparer.Ordinal);
        foreach (string file in scripts)
        {
            string res = "res://" + Path.GetRelativePath(folder, file).Replace('\\', '/');
            GDScriptTree tree = GDScriptTree.Parse(File.ReadAllText(file));
            trees[res] = tree;
            if (tree.Class.Identifier is { } named)
            {
                classNames[named.Name] = res;
            }
        }
        Dictionary<string, string> autoloads = new(StringComparer.Ordinal);
        string godot = Path.Combine(folder, "project.godot");
        bool inAutoloads = false;
        foreach (string line in File.Exists(godot) ? File.ReadAllLines(godot) : [])
        {
            inAutoloads = line.StartsWith('[') ? line == "[autoload]" : inAutoloads;
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (inAutoloads && equals > 0 && line[(equals + 1)..].Trim('"', '*') is { } loaded && loaded.EndsWith(".gd", StringComparison.Ordinal))
            {
                autoloads[line[..equals]] = loaded;
            }
        }
        Dictionary<string, string> uids = new(StringComparer.Ordinal);
        Dictionary<string, string> classes = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.Contains("/.godot/", StringComparison.Ordinal)))
        {
            string res = "res://" + Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (file.EndsWith(".uid", StringComparison.Ordinal))
            {
                uids[File.ReadAllText(file).Trim()] = res[..^4];
            }
            else if (file.EndsWith(".import", StringComparison.Ordinal) || file.EndsWith(".tres", StringComparison.Ordinal) || file.EndsWith(".tscn", StringComparison.Ordinal))
            {
                string head = string.Join('\n', File.ReadLines(file).Take(12));
                string owner = file.EndsWith(".import", StringComparison.Ordinal) ? res[..^7] : res;
                if (System.Text.RegularExpressions.Regex.Match(head, "uid=\"(uid://[^\"]+)\"") is { Success: true } uid)
                {
                    uids[uid.Groups[1].Value] = owner;
                }
                if (System.Text.RegularExpressions.Regex.Match(head, "script_class=\"([^\"]+)\"") is { Success: true } scripted)
                {
                    classes[owner] = scripted.Groups[1].Value;
                }
                else if (!file.EndsWith(".tscn", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Match(head, "(?m)^\\[?(?:gd_resource )?type=\"([^\"]+)\"") is { Success: true } typed)
                {
                    classes[owner] = typed.Groups[1].Value;
                }
            }
        }
        GDScriptProject project = new(api, path => trees.GetValueOrDefault(path), classNames, autoloads, classes.GetValueOrDefault, uids.GetValueOrDefault);
        GDScriptAnalyzer analyzer = new(project);
        return trees.Select(t => (t.Key, Analysis: analyzer.Analyze(t.Value, t.Key))).Select(t => new
        {
            path = t.Key,
            members = t.Analysis.Tree!.Members.Select(m => new { name = Name(m.Node), detail = t.Analysis.Detail(m.Node) }).Where(m => m.detail is not null),
        });
    }

    /// <summary>Each script of a folder of Godot's own test scripts with Godot's findings in it as Passenger's analyzer gives
    /// them, every warning on as Godot's test runner sets them, the folder's scripts one project: lines from 1.</summary>
    private static object Warnings(string folder, GodotEngineApi api)
    {
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal);
        Dictionary<string, string> classNames = new(StringComparer.Ordinal);
        string godot = Path.Combine(folder, "project.godot");
        List<string> files = [];
        if (File.Exists(godot))
        {
            Walk(folder, files);
        }
        else
        {
            files.AddRange(Directory.EnumerateFiles(folder, "*.gd").Order(StringComparer.Ordinal));
        }
        GDScriptWarningSettings? settings = File.Exists(godot) ? GDScriptWarningSettings.Of(File.ReadAllText(godot)) : null;
        foreach (string file in files)
        {
            string res = "res://" + Path.GetRelativePath(folder, file).Replace('\\', '/');
            GDScriptTree tree = GDScriptTree.Parse(File.ReadAllText(file));
            trees[res] = tree;
            if (tree.Class.Identifier is { } named)
            {
                classNames.TryAdd(named.Name, res);
            }
        }
        GDScriptProject project = new(api, path => trees.GetValueOrDefault(path), classNames, new Dictionary<string, string>());
        GDScriptAnalyzer analyzer = new(project);
        return trees.Select(t =>
        {
            TextSnapshot text = TextSnapshot.From(t.Value.Text);
            return new
            {
                path = t.Key,
                found = analyzer.Analyze(t.Value, t.Key).Findings(settings ?? AsTested(t.Value.Text))
                    .Select(f => new { line = text.GetLineFromOffset(Math.Min(f.Start, text.Length)) + 1, code = f.Code, message = f.Message }),
            };
        });
    }

    /// <summary>Every warning on, as Godot's test runner sets them, but the two of declarations' types, which a script turns
    /// on by a comment (<c># enable UNTYPED_DECLARATION</c>).</summary>
    private static GDScriptWarningSettings AsTested(string text) => GDScriptWarningSettings.Every with
    {
        Levels = GDScriptWarnings.Names.ToDictionary(n => n, n => n is "UNTYPED_DECLARATION" or "INFERRED_DECLARATION" && !text.Contains($"# enable {n}", StringComparison.Ordinal)
            ? GDScriptWarnLevel.Ignore : GDScriptWarnLevel.Warn, StringComparer.Ordinal),
    };

    /// <summary>Each script under a folder laid out by Passenger's formatter (tabs, as Godot writes them): whether it changed,
    /// why it was refused, whether a second pass changes it again, how long the first took, and its lines past the guide's
    /// length before and after.</summary>
    private static object Formats(string folder)
    {
        List<string> scripts = [];
        Walk(folder, scripts);
        return scripts.Select(path =>
        {
            string text = File.ReadAllText(path).ReplaceLineEndings("\n");
            Stopwatch clock = Stopwatch.StartNew();
            GDScriptFormatted once = GDScriptFormatter.Format(text, new GDScriptFormatOptions());
            double took = clock.Elapsed.TotalMilliseconds;
            GDScriptFormatted twice = GDScriptFormatter.Format(once.Text, new GDScriptFormatOptions());
            return new
            {
                path,
                lines = text.Count(c => c == '\n'),
                changed = once.Text != text,
                refused = once.Refused,
                stable = twice.Text == once.Text,
                again = twice.Refused,
                milliseconds = Math.Round(took, 2),
                longBefore = Long(text),
                longAfter = Long(once.Text),
            };
        });
    }

    /// <summary>A script's lines wider than the guide's 100 columns, a tab four.</summary>
    private static int Long(string text) =>
        text.Split('\n').Count(line => line.Sum(c => c == '\t' ? 4 : 1) > GDScriptFormatSettings.GuideLineLength);

    private static void Walk(string folder, List<string> into)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.gd").Order(StringComparer.Ordinal))
        {
            into.Add(file);
        }
        foreach (string sub in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            if (!Skipped.Contains(Path.GetFileName(sub)))
            {
                Walk(sub, into);
            }
        }
    }

    /// <summary>A few edits a person makes, each at a place seeded by the file's length: a letter typed, a character
    /// deleted, a line broken and indented; each edit's parse is compared with a whole parse of the text it makes.</summary>
    private static IEnumerable<object> Edits(string path, string text, GDScriptTree tree)
    {
        if (text.Length < 2)
        {
            yield break;
        }
        Random random = new(text.Length);
        for (int i = 0; i < 6; i++)
        {
            int at = random.Next(text.Length);
            TextEdit edit = (i % 3) switch
            {
                0 => new TextEdit(at, 0, "a"),
                1 => new TextEdit(at, 1, string.Empty),
                _ => new TextEdit(at, 0, "\n\t"),
            };
            string edited = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.End));
            GDScriptTree incremental = tree.Reparse(edited, edit);
            GDScriptTree whole = GDScriptTree.Parse(edited);
            if (Shape(incremental) != Shape(whole))
            {
                yield return new { path, edit = new { edit.Start, edit.Length, edit.Text }, incremental = Shape(incremental), whole = Shape(whole) };
            }
        }
    }

    /// <summary>What a tree says, to compare two: each member's kind, name and place, and each error.</summary>
    private static string Shape(GDScriptTree tree) =>
        string.Join("\n", tree.Members.Select(m => $"{m.Node.GetType().Name} {Name(m.Node)} {m.Start}-{m.End}").Concat(tree.Errors.Select(e => $"! {e.Start}-{e.End} {e.Message}")));

    private static string? Name(GDScriptNode node) => node switch
    {
        GDScriptFunction f => f.Identifier?.Name,
        GDScriptVariable v => v.Identifier?.Name,
        GDScriptConstant c => c.Identifier?.Name,
        GDScriptSignal s => s.Identifier?.Name,
        GDScriptEnum e => e.Identifier?.Name,
        GDScriptClass c => c.Identifier?.Name,
        GDScriptAnnotation a => a.Name,
        _ => null,
    };

    /// <summary>On one script: a whole parse's time and allocation (the median of runs), a key typed in its middle parsed
    /// as an edit (time, allocation, how many of its runs it parsed again), and the bytes a tree holds.</summary>
    private static object Speed(string path, string text)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        for (int i = 0; i < 5; i++)
        {
            tree = GDScriptTree.Parse(text);
        }
        List<double> wholes = [];
        long wholeBytes = 0;
        for (int i = 0; i < 21; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long began = Stopwatch.GetTimestamp();
            tree = GDScriptTree.Parse(text);
            wholes.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            wholeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        int middle = text.IndexOf('\n', text.Length / 2) is int line and >= 0 ? line : text.Length / 2;
        TextEdit type = new(middle, 0, "a");
        TextEdit erase = new(middle, 1, string.Empty);
        string typed = string.Concat(text.AsSpan(0, middle), "a", text.AsSpan(middle));
        List<double> keys = [];
        long keyBytes = 0;
        int runsParsed = 0;
        for (int i = 0; i < 41; i++)
        {
            bool typing = i % 2 == 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long began = Stopwatch.GetTimestamp();
            GDScriptTree next = tree.Reparse(typing ? typed : text, typing ? type : erase);
            keys.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            keyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            runsParsed = next.RunsParsed;
            tree = next;
        }
        long held = Held(text);
        return new
        {
            path,
            lines = text.Count(c => c == '\n') + 1,
            characters = text.Length,
            runs = tree.Runs,
            wholeParseMilliseconds = Math.Round(Median(wholes), 3),
            wholeParseAllocatedBytes = wholeBytes,
            keyParseMilliseconds = Math.Round(Median(keys), 3),
            keyParseAllocatedBytes = keyBytes,
            keyRunsParsed = runsParsed,
            treeHeldBytes = held,
        };
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static long Held(string text)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        GDScriptTree tree = GDScriptTree.Parse(text);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(tree);
        return after - before;
    }
}
