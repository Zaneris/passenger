using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Scrolling a C# file of 50,000 lines costs a frame what scrolling one of 500 does. With the server's colours,
/// tens of thousands of inlay hints and thousands of warnings, breakpoints and bookmarks in the gutter, soft wrap off and
/// on, a wheel's step and a step a screen long (a fast fling's, or a scrollbar drag's) allocate on the UI thread, format
/// and colour only what comes into view, make no shape for the squiggles and marks they move, and the error stripe is drawn
/// as the rectangles its track has room for, never one for each line with a problem. The files are the project's from the start, so one warm server compiles them once.
/// Allocations counted, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class ScrollingTests : IClassFixture<ScrollingTests.Project>
{
    private readonly Project project;

    public ScrollingTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void A_scroll_frame_through_fifty_thousand_lines_costs_what_one_through_five_hundred_does()
    {
        MainWindow window = project.Open(Project.Small);
        CodeEditor editor = window.EditorArea.CodeEditor;
        foreach (string file in new[] { Project.Big, Project.Small })
        {
            Shown(window, file);
            int methods = Project.Methods(file);
            Ui.WaitFor(() => editor.ProblemCount.Warnings == methods * Project.WarningsEach && editor.InlayHints.Count == methods * Project.HintsEach,
                () => $"{file}'s {methods * Project.WarningsEach} warnings and {methods * Project.HintsEach} hints, where it shows {editor.ProblemCount.Warnings} and {editor.InlayHints.Count}",
                TimeSpan.FromMinutes(4));
            // The gutter's marks on the lines the scrolls go through.
            TextSnapshot text = editor.Document.Snapshot;
            for (int line = 3; line < 400; line += 7)
            {
                editor.Selections = SelectionSet.Single(text.GetLineStart(line) + 8);
                Ui.Run(window, AppActions.ToggleBreakpoint);
                if (line % 3 == 0)
                {
                    Ui.Run(window, AppActions.ToggleBookmark);
                }
            }
            Assert.True(editor.LineMarks.Count > 50 && editor.Bookmarks.Count > 15, $"{file} shows {editor.LineMarks.Count} breakpoints and {editor.Bookmarks.Count} bookmarks");
        }
        foreach (bool wrapping in new[] { false, true })
        {
            Scrolls small = new(window, project.PathOf(Project.Small), wrapping);
            Scrolls large = new(window, project.PathOf(Project.Big), wrapping);
            // Taken in turns, so what the process takes on meanwhile (fonts it meets, code compiled again) weighs on both alike.
            for (int round = 0; round < 2; round++)
            {
                small.Round();
                large.Round();
            }
            string how = wrapping ? "soft-wrapped" : "not wrapped";
            foreach (string scroll in Scrolls.Kinds)
            {
                (long Bytes, long Formatted, long Coloured, long Shapes) few = small.Cost(scroll);
                (long Bytes, long Formatted, long Coloured, long Shapes) many = large.Cost(scroll);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{how}, {scroll}: a frame through 500 lines allocated {few.Bytes / 1024} KB on the UI thread, formatting {few.Formatted} characters, colouring {few.Coloured} and making {few.Shapes} shapes; "
                    + $"through 50,000, {many.Bytes / 1024} KB, {many.Formatted}, {many.Coloured} and {many.Shapes}");
                Assert.True(many.Bytes <= few.Bytes + (16 * 1024),
                    $"{how}, {scroll}: a frame through 50,000 lines allocated {many.Bytes / 1024} KB on the UI thread, against {few.Bytes / 1024} KB through 500: a frame costs the file's size, not what comes into view");
                Assert.True(many.Formatted <= few.Formatted + 64 && many.Coloured <= few.Coloured + 64,
                    $"{how}, {scroll}: a frame through 50,000 lines formatted {many.Formatted} characters and coloured {many.Coloured}, against {few.Formatted} and {few.Coloured} through 500: lines drawn already were made again");
                Assert.True(many.Shapes == 0 && few.Shapes == 0,
                    $"{how}, {scroll}: a frame made {many.Shapes} shapes through 50,000 lines and {few.Shapes} through 500, where the squiggles and the gutter's marks it moved were drawn already");
            }
            Assert.True(large.Rectangles > 0 && large.Rectangles <= editor.Bounds.Height / 2,
                $"{how}, the error stripe drew {Project.Methods(Project.Big) * Project.WarningsEach} warnings as {large.Rectangles} rectangles down a track {editor.Bounds.Height:0} pixels tall, which the compositor draws again in every frame that scrolls");
        }
        window.Close();
    }

    /// <summary>A window's file scrolled from its top as a person scrolls it, soft-wrapped or not: what a frame of each kind of
    /// scroll costs (bytes, characters formatted and coloured, shapes made), the middle frame of each round's, the least of
    /// the rounds.</summary>
    private sealed class Scrolls(MainWindow window, string path, bool wrapping)
    {
        /// <summary>A wheel's step (two lines and a half), and a step of about a screen.</summary>
        public static readonly string[] Kinds = ["a wheel's step", "a step a screen long"];

        private readonly Dictionary<string, (long Bytes, long Formatted, long Coloured, long Shapes)> least = Kinds.ToDictionary(k => k, _ => (long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue));

        /// <summary>The rectangles the stripe drew for the file's marks, as the last round left it.</summary>
        public int Rectangles { get; private set; }

        public (long Bytes, long Formatted, long Coloured, long Shapes) Cost(string kind) => least[kind];

        public void Round()
        {
            Show(window, path);
            OpenDocument document = window.EditorArea.Active!;
            if (document.SoftWrap != wrapping)
            {
                Ui.Run(window, AppActions.SoftWrap);
            }
            CodeEditor editor = window.EditorArea.CodeEditor;
            Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(over, new Vector(0, 100_000));
            Ui.Settle();
            Assert.Equal(0, editor.LinesShown.First);
            double screen = Math.Floor(editor.Bounds.Height / 50);
            Measure(editor, over, Kinds[0], 1, 40);
            Measure(editor, over, Kinds[1], screen, 6);
            Rectangles = editor.StripeRectangles;
        }

        /// <summary>Steps down the file and back up, each a wheel's delta of <paramref name="delta"/>, the frame after each drawn.</summary>
        private void Measure(CodeEditor editor, Point over, string kind, double delta, int steps)
        {
            Ui.Settle();
            List<(long Bytes, long Formatted, long Coloured, long Shapes)> frames = [];
            for (int i = 0; i < 2 * steps; i++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long formatted = editor.CharactersFormatted;
                long coloured = editor.CharactersColoured;
                long shapes = editor.ShapesMade;
                window.MouseWheel(over, new Vector(0, i < steps ? -delta : delta));
                Ui.Draw();
                frames.Add((GC.GetAllocatedBytesForCurrentThread() - bytes, editor.CharactersFormatted - formatted, editor.CharactersColoured - coloured, editor.ShapesMade - shapes));
            }
            Assert.True(editor.LinesShown.First == 0, $"Scrolled down {Path.GetFileName(path)} and back up, the view shows line {editor.LinesShown.First + 1} at its top");
            (long Bytes, long Formatted, long Coloured, long Shapes) middle = (Middle(frames.Select(f => f.Bytes)), Middle(frames.Select(f => f.Formatted)), Middle(frames.Select(f => f.Coloured)), Middle(frames.Select(f => f.Shapes)));
            (long Bytes, long Formatted, long Coloured, long Shapes) so = least[kind];
            least[kind] = (Math.Min(so.Bytes, middle.Bytes), Math.Min(so.Formatted, middle.Formatted), Math.Min(so.Coloured, middle.Coloured), Math.Min(so.Shapes, middle.Shapes));
        }

        private static long Middle(IEnumerable<long> values)
        {
            long[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }
    }

    /// <summary>A project file shown in the window's editor, opened or brought to the front.</summary>
    private void Shown(MainWindow window, string file) => Show(window, project.PathOf(file));

    private static void Show(MainWindow window, string path)
    {
        window.OpenFile(path);
        Ui.Settle();
        Assert.Equal(Path.GetFileName(path), window.EditorArea.Active?.Name);
    }

    /// <summary>A console project with two files of the same methods, one of 50,000 lines and one of 500, restored once,
    /// and the warm server they share on it.</summary>
    public sealed class Project : SharedProject
    {
        public const string Big = "Big.cs";
        public const string Small = "Small.cs";

        /// <summary>A method's lines: a doc comment, a line too long for the window, which soft wrap breaks, a type and two
        /// parameters' hints on each of eight lines, a local never read on each of eight more, and every tenth method an error.</summary>
        public const int HintsEach = 24;

        public const int WarningsEach = 8;

        public Project()
            : base("scrolling-")
        {
            File.WriteAllText(PathOf("Sample.csproj"), TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
            File.WriteAllText(PathOf(Big), Of("Big", Methods(Big)));
            File.WriteAllText(PathOf(Small), Of("Small", Methods(Small)));
            // The server's first load makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }

        /// <summary>How many methods a file has: 50,000 lines or 500, at 49 lines a method.</summary>
        public static int Methods(string file) => file == Big ? 50_000 / 49 : 500 / 49;

        private static string Of(string name, int methods)
        {
            StringBuilder text = new($"using System;\nusing System.Collections.Generic;\n\nnamespace Scrolled;\n\npublic static class {name}\n{{\n    public static int Of(int width, int height) => width * height;\n\n");
            for (int method = 0; method < methods; method++)
            {
                text.Append(CultureInfo.InvariantCulture, $"    /// <summary>Method {method} adds things up.</summary>\n    public static int Method{method}(int seed)\n    {{\n        int total = seed;\n");
                text.Append("        // ").Append('-', 12).Append(string.Concat(Enumerable.Repeat(" a note long enough to run past the window's edge", 4))).Append('\n');
                for (int j = 0; j < 40; j++)
                {
                    string line = (j % 5) switch
                    {
                        0 => string.Create(CultureInfo.InvariantCulture, $"        var value{j} = Of(3, {j}) + total;"),
                        1 => string.Create(CultureInfo.InvariantCulture, $"        int unused{j} = {j};"),
                        2 => string.Create(CultureInfo.InvariantCulture, $"        total += \"text {method}:{j}\".Length + value{j - 2};"),
                        3 => string.Create(CultureInfo.InvariantCulture, $"        if (total > {j * 100}) {{ total -= Of(width: {j}, height: 2); }}"),
                        _ => string.Create(CultureInfo.InvariantCulture, $"        List<string> names{j} = new() {{ \"a{j}\", \"b\" }};"),
                    };
                    text.Append(line).Append('\n');
                }
                text.Append(method % 10 == 7 ? string.Create(CultureInfo.InvariantCulture, $"        string wrong{method} = {method};\n") : "        total++;\n");
                text.Append("        return total;\n    }\n\n");
            }
            return text.Append("}\n").ToString();
        }
    }
}
