using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Passenger.App.Debugging;
using Passenger.App.Testing;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>A run icon on a line (from zero), right of the line numbers, beside the tests' own: something a run
/// configuration runs (a compose file's service, a Dockerfile's image) or a route sends; a click on it asks for its menu.</summary>
public readonly record struct RunMark(int Line);

/// <summary>A mark on a line (from zero) of a Godot script's code leading into its scenes: a class's to the scenes it runs
/// in, a handler's to the connections calling it, each a lead in order; a click on it asks for its menu of them.</summary>
public sealed record SceneMark(int Line, IReadOnlyList<SceneLead> Leads);

/// <summary>Where a scene mark leads: its words, the scene's file, and the line (from zero) to go to.</summary>
public sealed record SceneLead(string Words, string File, int Line);

/// <summary>A colour a line (from zero) writes or names, shown as a swatch right of the line numbers; a click on it opens
/// its picker. <see cref="Data"/> is what the language knows it by.</summary>
public readonly record struct ColourMark(int Line, Color Colour, object? Data = null);

/// <summary>The gutter's column right of the line numbers: one icon a line, the execution point's arrow before a test's run
/// icon before a run configuration's before a colour's swatch before a scene mark's, and the breakpoints' dots over the
/// numbers, the number giving way, each read from the file shown for the lines on screen. A press within
/// <see cref="Reach"/> of its left edge, as its icons reach into the change bars beside it, is its on a line with an icon:
/// the arrow's drags the arrow to Set Next Statement, the rest are <see cref="Pressed"/>'s.</summary>
public sealed class MarkColumn : IGutterColumn
{
    /// <summary>Its strip, between the numbers and the change bars.</summary>
    public const double StripWidth = 14;

    /// <summary>How far right of its left edge a press is on its icons.</summary>
    public const double Reach = StripWidth + ChangeColumn.StripWidth;

    private readonly DebugMarks debug = new();
    private readonly Dictionary<int, TestMarkKind> tests = [];
    private readonly HashSet<int> runs = [];
    private readonly Dictionary<int, Color> colours = [];
    private readonly HashSet<int> scenes = [];
    private readonly FollowedDocument file;

    /// <summary>The line the dragged arrow is over, while it is dragged.</summary>
    private int arrowAt = -1;

    private int arrowFrom = -1;

    public MarkColumn() => file = FollowedDocument.OfProperties(OnFileChanged);

    public event EventHandler? Changed;

    /// <summary>The arrow was dragged to another line (from zero), to Set Next Statement there. With no handler it stays.</summary>
    public event EventHandler<int>? ExecutionPointDragged;

    /// <summary>A press on an icon but the arrow: whether it did something; one that did not is the gutter's.</summary>
    public Func<GutterClick, bool>? Pressed { get; set; }

    /// <summary>The debugger's marks: breakpoints and the execution point.</summary>
    public IReadOnlyList<LineMark> LineMarks => file.Document?.LineMarks ?? [];

    public IReadOnlyList<TestMark> TestMarks => file.Document?.TestMarks ?? [];

    public IReadOnlyList<RunMark> RunMarks => file.Document?.GutterRunMarks ?? [];

    public IReadOnlyList<ColourMark> ColourMarks => file.Document?.ColourMarks ?? [];

    public IReadOnlyList<SceneMark> SceneMarks => file.Document?.SceneMarks ?? [];

    /// <summary>Shows a file's marks, followed as it changes them; none for no file.</summary>
    public void Show(OpenDocument? document)
    {
        if (file.Show(document))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OpenDocument.LineMarks) or nameof(OpenDocument.TestMarks) or nameof(OpenDocument.RunMarks)
            or nameof(OpenDocument.RouteMarks) or nameof(OpenDocument.ColourMarks) or nameof(OpenDocument.SceneMarks))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public double Width(GutterFont font) => StripWidth;

    public void Draw(GutterFrame frame)
    {
        debug.Marks = LineMarks;
        debug.Find(frame.First, frame.Last);
        tests.Clear();
        LineOrdered<TestMark> testMarks = LineOrdered<TestMark>.Of(TestMarks, static m => m.Line);
        for (int i = testMarks.From(frame.First); i < testMarks.Count && testMarks.LineOf(i) <= frame.Last; i++)
        {
            tests[testMarks[i].Line] = testMarks[i].Kind;
        }
        runs.Clear();
        LineOrdered<RunMark> runMarks = LineOrdered<RunMark>.Of(RunMarks, static m => m.Line);
        for (int i = runMarks.From(frame.First); i < runMarks.Count && runMarks.LineOf(i) <= frame.Last; i++)
        {
            runs.Add(runMarks[i].Line);
        }
        colours.Clear();
        LineOrdered<ColourMark> colourMarks = LineOrdered<ColourMark>.Of(ColourMarks, static m => m.Line);
        for (int i = colourMarks.From(frame.First); i < colourMarks.Count && colourMarks.LineOf(i) <= frame.Last; i++)
        {
            colours.TryAdd(colourMarks[i].Line, colourMarks[i].Colour);
        }
        scenes.Clear();
        LineOrdered<SceneMark> sceneMarks = LineOrdered<SceneMark>.Of(SceneMarks, static m => m.Line);
        for (int i = sceneMarks.From(frame.First); i < sceneMarks.Count && sceneMarks.LineOf(i) <= frame.Last; i++)
        {
            scenes.Add(sceneMarks[i].Line);
        }
        double left = frame.Left;
        foreach (GutterLine shown in frame.Lines)
        {
            int line = shown.Line;
            double top = shown.Top;
            if (debug.IsExecuting(line))
            {
                DebugMarks.DrawExecutionArrow(frame, left, top);
            }
            else if (line == arrowAt)
            {
                // Where the dragged arrow would go on from, drawn faint until it is let go.
                using (frame.Context.PushOpacity(0.5))
                {
                    DebugMarks.DrawExecutionArrow(frame, left, top);
                }
            }
            else if (tests.TryGetValue(line, out TestMarkKind test))
            {
                Testing.TestMarks.Draw(frame, test, left, top);
            }
            else if (runs.Contains(line))
            {
                // A run configuration's service or image: the run icon a test not yet run has.
                Testing.TestMarks.Draw(frame, TestMarkKind.NotRun, left, top);
            }
            else if (colours.TryGetValue(line, out Color colour))
            {
                DrawSwatch(frame, colour, left, top);
            }
            else if (scenes.Contains(line))
            {
                DrawScene(frame, left, top);
            }
            if (debug.TryBreakpoint(line, out LineMark mark))
            {
                // Over the number, which gives way.
                frame.Cover(line);
                DebugMarks.DrawBreakpoint(frame, mark, left - frame.CharWidth, top + frame.LineHeight / 2);
            }
        }
    }

    public void Tint(LineTints tints)
    {
        debug.Marks = LineMarks;
        debug.Tint(tints);
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (press.Click.Context || press.X < 0 || press.X >= Reach)
        {
            return GutterAnswer.Passed;
        }
        debug.Marks = LineMarks;
        if (ExecutionPointDragged is not null && debug.Executes(press.Click.Line))
        {
            arrowFrom = arrowAt = press.Click.Line;
            return GutterAnswer.Dragging;
        }
        return Pressed?.Invoke(press.Click) == true ? GutterAnswer.Taken : GutterAnswer.Passed;
    }

    public void Drag(int line) => arrowAt = line;

    public bool Drop(GutterPress press)
    {
        int from = arrowFrom;
        int to = arrowAt;
        arrowAt = arrowFrom = -1;
        if (to != from)
        {
            ExecutionPointDragged?.Invoke(this, to);
            return true;
        }
        return press.X >= 0 && press.X < Reach && Pressed?.Invoke(press.Click) == true;
    }

    /// <summary>A scene mark's icon from the column's left edge: a node and the child under it, joined as a scene's tree
    /// draws them.</summary>
    private static void DrawScene(GutterFrame frame, double left, double top)
    {
        double size = Math.Min(frame.LineHeight - 6, 11);
        double x = left + 5;
        double y = top + ((frame.LineHeight - size) / 2);
        double box = size * 0.42;
        GutterShape key = new(nameof(SceneMark), size);
        if (!frame.TryShape(key, out StreamGeometry? tree))
        {
            tree = frame.AddShape(key);
            using StreamGeometryContext path = tree.Open();
            Square(path, 0, 0, box);
            Square(path, size - box, size - box, box);
        }
        IBrush brush = frame.Brush(TokenKeys.Icon);
        frame.DrawShape(tree, brush, null, x, y);
        IPen joint = frame.Pen(TokenKeys.Icon, 1.2);
        double down = x + (box / 2);
        frame.Context.DrawLine(joint, new Point(down, y + box), new Point(down, y + size - (box / 2)));
        frame.Context.DrawLine(joint, new Point(down, y + size - (box / 2)), new Point(x + size - box, y + size - (box / 2)));

        static void Square(StreamGeometryContext path, double x, double y, double side)
        {
            path.BeginFigure(new Point(x, y), true);
            path.LineTo(new Point(x + side, y));
            path.LineTo(new Point(x + side, y + side));
            path.LineTo(new Point(x, y + side));
            path.EndFigure(true);
        }
    }

    /// <summary>A colour's swatch from the column's left edge: a small square of it, ringed so a colour near the gutter's own
    /// still shows.</summary>
    private static void DrawSwatch(GutterFrame frame, Color colour, double left, double top)
    {
        double size = Math.Min(frame.LineHeight - 6, 12);
        Rect square = new(left + 5, top + ((frame.LineHeight - size) / 2), size, size);
        frame.Context.DrawRectangle(frame.Brush(colour), frame.Pen(TokenKeys.Editor_LineNumber, 1), square, 2, 2);
    }
}
