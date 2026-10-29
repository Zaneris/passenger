using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Which everyday edits Hot Reload applies to a program debugged on .NET 10, each made in the editor while the
/// program runs (or, for a method on the stack, while it is paused in it) and applied with Apply Hot Reload Changes: the
/// program then prints what the edited code prints, or, for an edit the runtime cannot take, Hot Reload names it at its line
/// and applies nothing. The edits come in chains, each chain one debug session, every edit applied on top of the ones
/// before it; the chains share one project, restored once, its build and its language server carried from chain to chain
/// while the files on disk stay as written. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class HotReloadEditsTests : IClassFixture<HotReloadEditsTests.Ticks>
{
    private readonly Ticks project;

    public HotReloadEditsTests(Ticks project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>A static class, a class with an instance, a lambda, an async method, a record and a generic method, each
    /// printing a line every tick.</summary>
    private const string Demo = """
        static class Demo
        {
            static readonly Counter counter = new();

            public static void Tick(int i)
            {
                Console.WriteLine($"tick {i}");
                Console.WriteLine($"counter {counter.Next()}");
                Func<int, string> format = x => $"lambda {x}";
                Console.WriteLine(format(i));
                Console.WriteLine(FetchAsync(i).GetAwaiter().GetResult());
                Console.WriteLine(new Pet("Rex").Describe());
                Console.WriteLine(Echo($"generic {i}"));
            }

            static async Task<string> FetchAsync(int i)
            {
                await Task.Yield();
                return $"async {i}";
            }

            static T Echo<T>(T value) => value;
        }

        sealed class Counter
        {
            public int Next() => 1;
        }

        record Pet(string Name)
        {
            public string Describe() => $"pet {Name}";
        }

        """;

    /// <summary>One kind of edit: the text replaced and what replaces it, in order, and what the program prints once it
    /// applies; null for an edit the runtime cannot take while the program runs.</summary>
    public sealed record Edit(string Kind, IReadOnlyList<(string Old, string New)> Changes, string? Prints);

    /// <summary>The edits, a few chains of them, each chain one debug session: every edit is applied on top of those before
    /// it, as a person goes on editing a program they debug, and a chain ends with one the runtime cannot take, after which
    /// nothing more applies in that session.</summary>
    public static readonly Dictionary<string, IReadOnlyList<Edit>> Chains = new()
    {
        ["bodies of methods, lambdas, async methods, records and generics"] =
        [
            new("a method body", [("$\"tick {i}\"", "$\"tick edited {i}\"")], "tick edited"),
            new("a lambda's body", [("x => $\"lambda {x}\"", "x => $\"lambda edited {x}\"")], "lambda edited"),
            new("a lambda capturing a new variable", [("x => $\"lambda edited {x}\"", "x => $\"lambda captured {x} {i}\"")], "lambda captured"),
            new("an async method's body", [("$\"async {i}\"", "$\"async edited {i}\"")], "async edited"),
            new("an await added to an async method", [("        return $\"async edited {i}\";", "        await Task.Yield();\n        return $\"async awaited twice {i}\";")], "async awaited twice"),
            new("a record's method", [("$\"pet {Name}\"", "$\"pet edited {Name}\"")], "pet edited"),
            new("a property added to a record", [("    public string Describe() => $\"pet edited {Name}\";", "    public int Age { get; init; } = 3;\n\n    public string Describe() => $\"pet {Name} aged {Age}\";")], "aged 3"),
            new("a generic method's body", [("static T Echo<T>(T value) => value;", "static T Echo<T>(T value) { Console.WriteLine(\"generic edited\"); return value; }")], "generic edited"),
            // Program.cs still calls Tick(int), whose body the update would leave throwing: only a restart runs the edit.
            new("a parameter added with a default its callers leave out", [("    public static void Tick(int i)", "    public static void Tick(int i, int times = 1)")], null),
        ],
        ["members, fields, classes and signatures added"] =
        [
            new("a method added", [("$\"tick {i}\"", "Word(i)"), ("    public static void Tick(int i)", "    static string Word(int i) => $\"added method {i}\";\n\n    public static void Tick(int i)")], "added method"),
            new("a static field added", [("    static readonly Counter counter = new();", "    static readonly Counter counter = new();\n    static int ticks;"), ("Word(i)", "$\"added static field {++ticks}\"")], "added static field 1"),
            new("a class added", [("sealed class Counter", "static class Added\n{\n    public static string Say(int i) => $\"added class {i}\";\n}\n\nsealed class Counter"), ("$\"added static field {++ticks}\"", "Added.Say(i)")], "added class"),
            new("an instance field added", [("    public int Next() => 1;", "    int calls;\n\n    public int Next() => 100 + ++calls;")], "counter 101"),
            new("a parameter added with its callers", [("public int Next() => 100 + ++calls;", "public int Next(int by) => by;"), ("counter.Next()", "counter.Next(7)")], "counter 7"),
            new("a method's signature", [("Added.Say(i)", "Word(i, \"signature\")"), ("    public static void Tick(int i)", "    static string Word(int i, string what) => $\"{what} {i}\";\n\n    public static void Tick(int i)")], "signature"),
            // The field the earlier edit added is the running program's now; its type is what this edit changes.
            new("a field's type", [("    static int ticks;", "    static long ticks;")], null),
        ],
        ["return types changed with their callers and without"] =
        [
            new("a return type changed with its callers", [("public string Describe() => $\"pet {Name}\";", "public object Describe() => $\"pet {Name} as object\";"),
                ("new Pet(\"Rex\").Describe()", "new Pet(\"Rex\").Describe().ToString()")], "as object"),
            // Tick still calls Next() as returning an int, whose body the update would leave throwing: only a restart runs the edit.
            new("a method's return type its callers leave as they are", [("public int Next() => 1;", "public long Next() => 2;")], null),
        ],
    };

    public static TheoryData<string> Kinds => [.. Chains.Keys];

    [AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void Each_edit_to_a_running_program_applies_as_the_runtime_allows(string chain)
    {
        MainWindow window = Open();
        try
        {
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Output(window).Contains("generic 2", StringComparison.Ordinal), $"the program to tick ({Output(window)})");
            window.OpenFile(DemoPath);
            List<string> applied = [];
            foreach (Edit edit in Chains[chain])
            {
                foreach ((string old, string typed) in edit.Changes)
                {
                    Replace(window, old, typed);
                }
                string after = applied.Count == 0 ? string.Empty : $" after {string.Join(", ", applied)}";
                int before = Output(window).Length;
                HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), $"Hot Reload to apply {edit.Kind}", TimeSpan.FromMinutes(2));
                TestContext.Current.TestOutputHelper?.WriteLine($"{edit.Kind}: {outcome.Result}: {outcome.Said}");
                if (edit.Prints is null)
                {
                    Assert.True(outcome.Result == HotReloadResult.RestartNeeded, $"Changing {edit.Kind}{after} should need a restart: {outcome.Said}");
                    Assert.Contains(outcome.Problems, p => p.Path == DemoPath && p.Code?.StartsWith("ENC", StringComparison.Ordinal) == true);
                    Ui.WaitFor(() => Output(window)[before..].Contains("generic", StringComparison.Ordinal), $"the program to tick on as it was ({Output(window)[before..]})");
                    Assert.True(window.Debugger.Session?.State == DebugState.Running, $"The program stopped after an edit that was not applied: {Output(window)[before..]}");
                    Assert.Same(edit, Chains[chain][^1]);
                    return;
                }
                Assert.True(outcome.Result == HotReloadResult.Applied, $"Changing {edit.Kind}{after} should apply: {outcome.Said}");
                Ui.WaitFor(() => Output(window)[before..].Contains(edit.Prints, StringComparison.Ordinal), $"the program to print '{edit.Prints}' once {edit.Kind} applied ({Output(window)[before..]})");
                applied.Add(edit.Kind);
                if (edit.Kind == "a method body")
                {
                    // The edits Roslyn's rules allowed are those the program's own runtime says it can take.
                    Assert.Equal(DebugState.Running, window.Debugger.Session!.State);
                    Dap.HotReloadStateResponse state = Ui.Await(window.Debugger.Session.HotReloadStateAsync(), "what the program's runtime can take");
                    TestContext.Current.TestOutputHelper?.WriteLine($"The runtime's capabilities: {string.Join(' ', state.Capabilities ?? [])}");
                    Assert.True(state.Capabilities is { } capabilities && capabilities.Contains("Baseline") && capabilities.Contains("AddMethodToExistingType"),
                        $"The runtime should say which edits it takes: {state.CapabilitiesUnknown}");
                    Assert.Contains(state.Modules, m => m.Name == "Ticks.dll" && m.Editable);
                    Assert.Contains(state.Modules, m => m.Name == "System.Private.CoreLib.dll" && !m.Editable);
                }
            }
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An edit to the method the program is paused in runs from the paused statement, applied with Apply Hot
    /// Reload Changes, or with no Apply at all by the Resume that lets the program go on.</summary>
    [AvaloniaFact]
    public void An_edit_to_a_method_paused_on_the_stack_runs_from_its_statement()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(DemoPath);
            window.EditorArea.CodeEditor.GoTo(new TextPosition(6, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the program to stop in Tick");
            Ui.Settle();
            Replace(window, "$\"tick {i}\"", "$\"tick edited {i}\"");
            HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), "Hot Reload to apply the edit", TimeSpan.FromMinutes(2));
            TestContext.Current.TestOutputHelper?.WriteLine($"a method on the stack: {outcome.Result}: {outcome.Said}");
            Assert.True(outcome.Result == HotReloadResult.Applied, $"Editing the method the program is paused in should apply: {outcome.Said}");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Output(window).Contains("tick edited 2", StringComparison.Ordinal), $"the program to tick with the edit ({Output(window)})");
            Assert.Contains("tick edited 1", Output(window), StringComparison.Ordinal);

            window.EditorArea.CodeEditor.GoTo(new TextPosition(6, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the program to stop in Tick again");
            Ui.Settle();
            string? tick = null;
            Ui.WaitFor(() => (tick = window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == "i")?.Value) is not null, "the paused tick's number in Variables");
            Replace(window, "$\"tick edited {i}\"", "$\"tick resumed {i}\"");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Output(window).Contains($"tick resumed {tick}", StringComparison.Ordinal), $"the paused tick to print with the edit Resume applied ({Output(window)})");
            Assert.True(window.Debugger.LastApplied != outcome && window.Debugger.LastApplied?.Result == HotReloadResult.Applied,
                $"Resuming should apply the edit: {window.Debugger.LastApplied?.Said}");
            Assert.DoesNotContain($"tick edited {tick}", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A frame waiting on a call goes on in the new code once the call returns: paused in FetchAsync, which Tick
    /// waits on, a return type changed with its caller on Tick's next line applies, and that tick prints what the new code
    /// prints rather than calling the member the update deleted.</summary>
    [AvaloniaFact]
    public void A_frame_waiting_on_a_call_goes_on_in_the_new_code_once_the_call_returns()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(DemoPath);
            TextPosition fetched = new(18, 8);
            window.EditorArea.CodeEditor.GoTo(fetched);
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the program to stop in FetchAsync");
            Ui.Settle();
            Replace(window, "public string Describe() => $\"pet {Name}\";", "public object Describe() => $\"pet {Name} as object\";");
            Replace(window, "new Pet(\"Rex\").Describe()", "new Pet(\"Rex\").Describe().ToString()");
            HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), "Hot Reload to apply the edit", TimeSpan.FromMinutes(2));
            TestContext.Current.TestOutputHelper?.WriteLine($"a frame waiting on a call: {outcome.Result}: {outcome.Said}");
            Assert.True(outcome.Result == HotReloadResult.Applied, $"Changing a return type with its callers should apply: {outcome.Said}");
            int before = Output(window).Length;
            window.EditorArea.CodeEditor.GoTo(fetched);
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Output(window)[before..].Contains("generic", StringComparison.Ordinal) || window.Debugger.Session?.State != DebugState.Running,
                $"the paused tick to go on ({Output(window)[before..]})");
            Assert.True(window.Debugger.Session?.State == DebugState.Running && Output(window)[before..].Contains("pet Rex as object", StringComparison.Ordinal),
                $"The tick waiting on FetchAsync should print what the new code prints, not call the deleted Describe: {window.Debugger.Session?.State} {window.Debugger.Stop?.Reason} {window.Debugger.Stop?.Exception} ({Output(window)[before..]})");
        }
        finally
        {
            End(window);
        }
    }

    private string DemoPath => Path.Combine(PathNames.Canonical(project.Folder), "Demo.cs");

    private static string Output(MainWindow window) => window.Debugger.Output().ToString();

    private static void Replace(MainWindow window, string old, string typed)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        int at = editor.Document.Snapshot.ToString().IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The file should have '{old}' to replace: {editor.Document.Snapshot}");
        editor.Focus();
        editor.Select(new TextRange(at, old.Length));
        window.KeyTextInput(typed);
        Ui.Settle();
        // A line break typed is indented as the editor indents one, so the text is compared without its spaces.
        Assert.Contains(Squeezed(typed), Squeezed(editor.Document.Snapshot.ToString()), StringComparison.Ordinal);
    }

    private static string Squeezed(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>A window on the project, on the server the last row's left, which never saves what its row typed: what is
    /// on disk stays what the build was made from.</summary>
    private MainWindow Open() => project.Open(null);

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        }
        window.Close();
    }

    /// <summary>A program that ticks for ever, each tick printing a line from each kind of code an edit is made to.</summary>
    public sealed class Ticks : SharedProject
    {
        public Ticks()
            : base("hotreload-edits-")
        {
            File.WriteAllText(PathOf("Ticks.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            File.WriteAllText(PathOf("Program.cs"), "for (int i = 1; ; i++)\n{\n    Demo.Tick(i);\n    Thread.Sleep(100);\n}\n");
            File.WriteAllText(PathOf("Demo.cs"), Demo);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
