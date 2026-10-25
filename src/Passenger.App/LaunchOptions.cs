using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The command line: <c>passenger [PATH] [--file F]... [--caret L:C] [--theme T] [--look L] [--scheme S]</c>,
/// PATH a folder, a file, or a solution or project opened as the workspace; a screenshot with the steps before it; or
/// the Model Context Protocol server: <c>--mcp [PATH]</c> (an IDE with no window, on standard input and output),
/// <c>--mcp-attach [PID] [FOLDER]</c> (the agent's server, joining the window over the folder at its first call), and
/// <c>--no-mcp</c> (a window that never lets agents in; a screenshot's lets in only its own <c>--agent</c>);
/// <c>--headless</c> is a person's window with nobody at it. A flag it does not know, a value it cannot read or out of
/// range, or one the mode does not take is fatal, never ignored.</summary>
public sealed record LaunchOptions(string? Folder, IReadOnlyList<string> Files, TextPosition? Caret, ThemeChoice? Theme, ScreenshotOptions? Screenshot)
{
    /// <summary>The flags that may be given more than once: each is one more file or one more step.</summary>
    private static readonly HashSet<string> Repeatable = ["--file", "--type", "--key", "--click", "--click-name", "--double-click", "--right-click", "--drag", "--drag-hold", "--hover", "--wait", "--open", "--agent"];

    /// <summary>What only a screenshot takes: its window, its picture and the steps before it.</summary>
    private static readonly HashSet<string> ScreenshotOnly = ["--size", "--scale", "--crop", "--window-buttons", "--type", "--key", "--click", "--click-name", "--double-click", "--right-click", "--drag", "--drag-hold", "--hover", "--wait", "--open", "--agent"];

    /// <summary>A screenshot's longest side in pixels: a frame 16384 pixels square is already a gigabyte, and the headless
    /// window draws none at 24000.</summary>
    private const double LargestSide = 16384;

    public McpMode Mcp { get; init; }

    /// <summary><c>--headless</c>: a person's window on the headless platform, which <c>--mcp-attach</c> starts in
    /// place of the installed app under <see cref="Mcp.McpProxy.StartVariable"/>.</summary>
    public bool Headless { get; init; }

    /// <summary>The process <c>--mcp-attach</c> names, or null for any Passenger's windows.</summary>
    public int? AttachTo { get; init; }

    /// <summary>Whether the window may listen for agents on its socket once its Enable MCP Server setting is on
    /// (<c>--no-mcp</c> says never).</summary>
    public bool ListenForAgents { get; init; } = true;

    /// <summary>The solution (.sln, .slnx) or C# project (.csproj) PATH names, the workspace over <see cref="Folder"/>, its
    /// own folder.</summary>
    public string? Workspace { get; init; }

    /// <summary>The theme <c>--look</c> names over the one kept, or null for that one.</summary>
    public Look? Look { get; init; }

    /// <summary>The code's colours <c>--scheme</c> names over the ones kept, or null for those.</summary>
    public ColourScheme? Scheme { get; init; }

    public static LaunchOptions Empty { get; } = new(null, [], null, null, null);

    /// <summary>The options the arguments ask for, the files among them full paths that exist; anything malformed,
    /// out of range or not for the chosen mode is a <see cref="FormatException"/> whose message names it.</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        // Every flag and path as given, in order, so the first one the mode does not take is the one named.
        List<string> given = [];
        HashSet<string> once = [];
        string? path = null;
        List<string> named = [];
        TextPosition? caret = null;
        ThemeChoice? theme = null;
        global::Passenger.App.Look? look = null;
        ColourScheme? scheme = null;
        string? shot = null;
        LogicalSize size = new(1600, 1000);
        double scale = 2;
        Rect? crop = null;
        string? cropText = null;
        bool windowButtons = false;
        List<ScriptStep> steps = [];
        List<(string Flag, string Text, Point At)> points = [];
        McpMode mcp = McpMode.None;
        int? attachTo = null;
        bool listen = true;
        bool headless = false;
        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i];
            given.Add(flag);
            if (!flag.StartsWith('-'))
            {
                if (path is not null)
                {
                    throw new FormatException($"'{flag}' would be a second folder or file after '{path}'; Passenger opens one, and more files with --file");
                }
                path = flag;
                continue;
            }
            if (!Repeatable.Contains(flag) && !once.Add(flag))
            {
                throw new FormatException($"{flag} is given twice");
            }
            switch (flag)
            {
                case "--mcp":
                    mcp = McpMode.Stdio;
                    break;
                case "--mcp-attach":
                    mcp = McpMode.Attach;
                    if (i + 1 < args.Count && args[i + 1].Length > 0 && args[i + 1].All(char.IsAsciiDigit))
                    {
                        i++;
                        attachTo = int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
                            ? pid
                            : throw new FormatException($"--mcp-attach takes the process id of a running Passenger, not '{args[i]}'");
                    }
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--no-mcp":
                    listen = false;
                    break;
                case "--file":
                    named.Add(Value(args, ref i, flag));
                    break;
                case "--caret":
                    caret = ParseCaret(Value(args, ref i, flag));
                    break;
                case "--theme":
                    theme = ParseTheme(Value(args, ref i, flag));
                    break;
                case "--look":
                    string lookText = Value(args, ref i, flag);
                    global::Passenger.App.Look[] looks = Enum.GetValues<global::Passenger.App.Look>();
                    look = looks.Cast<global::Passenger.App.Look?>().FirstOrDefault(l => l.ToString()!.ToLowerInvariant() == lookText)
                        ?? throw new FormatException($"--look takes {string.Join(", ", looks.Select(l => l.ToString().ToLowerInvariant()))}, not '{lookText}'");
                    break;
                case "--scheme":
                    string schemeText = Value(args, ref i, flag);
                    scheme = schemeText switch
                    {
                        "theme" => ColourScheme.Theme,
                        "rider" => ColourScheme.Rider,
                        _ => throw new FormatException($"--scheme takes theme or rider, not '{schemeText}'"),
                    };
                    break;
                case "--screenshot":
                    shot = Value(args, ref i, flag);
                    break;
                case "--size":
                    string sizeText = Value(args, ref i, flag);
                    double[] wh = Numbers(flag, sizeText, 'x', 2, "WIDTHxHEIGHT");
                    if (wh[0] < MainWindow.SmallestWidth || wh[1] < MainWindow.SmallestHeight)
                    {
                        throw new FormatException(FormattableString.Invariant($"--size is at least the window's smallest, {MainWindow.SmallestWidth}x{MainWindow.SmallestHeight}, not '{sizeText}'"));
                    }
                    size = new LogicalSize(wh[0], wh[1]);
                    break;
                case "--scale":
                    scale = Number(flag, Value(args, ref i, flag), "a number", 0.5, 8);
                    break;
                case "--crop":
                    cropText = Value(args, ref i, flag);
                    double[] box = Numbers(flag, cropText, ',', 4, "X,Y,WIDTH,HEIGHT");
                    if (box[2] == 0 || box[3] == 0)
                    {
                        throw new FormatException($"--crop keeps nothing of the window with a width or height of 0: '{cropText}'");
                    }
                    crop = new Rect(box[0], box[1], box[2], box[3]);
                    break;
                case "--window-buttons":
                    windowButtons = true;
                    break;
                case "--type":
                    steps.Add(new ScriptStep.Type(Value(args, ref i, flag)));
                    break;
                case "--key":
                    steps.Add(new ScriptStep.Key(ParseKey(Value(args, ref i, flag))));
                    break;
                case "--wait":
                    steps.Add(new ScriptStep.Wait(TimeSpan.FromMilliseconds(Number(flag, Value(args, ref i, flag), "milliseconds", 1, 600_000))));
                    break;
                case "--open":
                    string opened = Value(args, ref i, flag);
                    // As File, Open takes it: a solution, project or folder as the workspace, any other file in a tab.
                    steps.Add(new ScriptStep.Open(Directory.Exists(opened) || File.Exists(opened)
                        ? Path.GetFullPath(opened)
                        : throw new FormatException($"--open takes a file or folder that is there, not '{opened}'")));
                    break;
                case "--agent":
                    string agentText = Value(args, ref i, flag);
                    steps.Add(new ScriptStep.Agent(agentText, AgentCommand(agentText)));
                    break;
                case "--hover":
                    string hoverText = Value(args, ref i, flag);
                    double[] at = Numbers(flag, hoverText, ',', 2, "X,Y");
                    points.Add((flag, hoverText, new Point(at[0], at[1])));
                    steps.Add(new ScriptStep.Hover(new Point(at[0], at[1])));
                    break;
                case "--click" or "--double-click" or "--right-click":
                    string clickText = Value(args, ref i, flag);
                    double[] xy = Numbers(flag, clickText, ',', 2, "X,Y");
                    points.Add((flag, clickText, new Point(xy[0], xy[1])));
                    steps.Add(new ScriptStep.Click(new Point(xy[0], xy[1]), flag == "--double-click" ? 2 : 1)
                    {
                        Button = flag == "--right-click" ? MouseButton.Right : MouseButton.Left,
                    });
                    break;
                case "--click-name":
                    string controlName = Value(args, ref i, flag);
                    steps.Add(controlName.Length > 0 && controlName.All(c => char.IsLetterOrDigit(c) || c == '_')
                        ? new ScriptStep.ClickNamed(controlName)
                        : throw new FormatException($"--click-name takes a control's x:Name, not '{controlName}'"));
                    break;
                case "--drag" or "--drag-hold":
                    string dragText = Value(args, ref i, flag);
                    double[] line = Numbers(flag, dragText, ',', 4, "X1,Y1,X2,Y2");
                    points.Add((flag, dragText, new Point(line[0], line[1])));
                    points.Add((flag, dragText, new Point(line[2], line[3])));
                    steps.Add(new ScriptStep.Drag(new Point(line[0], line[1]), new Point(line[2], line[3]), Release: flag == "--drag"));
                    break;
                default:
                    throw new FormatException($"unknown flag '{flag}'");
            }
        }

        CheckMode(given, mcp, shot is not null, listen);
        if (headless && (mcp != McpMode.None || shot is not null || !listen))
        {
            throw new FormatException("--headless is a person's window with nobody at it, a test run's: it takes a path, files and a caret, not --mcp, --mcp-attach, --screenshot or --no-mcp");
        }
        (string? folder, string? workspace, List<string> files) = Opened(path, named);
        if (mcp == McpMode.Attach && files.Count > 0)
        {
            throw new FormatException($"--mcp-attach finds the window by its folder, and '{path}' is a file");
        }
        if (caret is not null && files.Count == 0)
        {
            throw new FormatException("--caret needs a file to put the caret in, the PATH or a --file");
        }
        ScreenshotOptions? screenshot = shot is null ? null : Checked(new ScreenshotOptions(shot, size, scale, steps, crop) { WindowButtons = windowButtons }, cropText, points);
        return new LaunchOptions(folder, files, caret, theme, screenshot) { Workspace = workspace, Mcp = mcp, AttachTo = attachTo, ListenForAgents = listen, Headless = headless, Look = look, Scheme = scheme };
    }

    /// <summary>Refuses the first flag the chosen mode does not take: a window takes all but a screenshot's, a screenshot
    /// all but <c>--no-mcp</c>, <c>--mcp</c> a path, files, a caret and a theme (its look and scheme too), and
    /// <c>--mcp-attach</c> only a process id and a folder.</summary>
    private static void CheckMode(List<string> given, McpMode mcp, bool screenshot, bool listen)
    {
        if (given.Contains("--mcp") && given.Contains("--mcp-attach"))
        {
            throw new FormatException("--mcp starts a Passenger of its own and --mcp-attach joins a running one; give one of them");
        }
        if (mcp == McpMode.Attach && given.FirstOrDefault(g => g != "--mcp-attach" && g.StartsWith('-')) is { } extra)
        {
            throw new FormatException($"--mcp-attach joins a running Passenger's window and takes only its process id and a folder, not {extra}");
        }
        if (mcp == McpMode.Stdio && screenshot)
        {
            throw new FormatException("--mcp does not take --screenshot; its agent takes one with the screenshot tool");
        }
        if (!listen && (mcp == McpMode.Stdio || screenshot))
        {
            throw new FormatException(screenshot
                ? "--no-mcp stops a window listening for agents, and --screenshot's lets in only its own --agent"
                : "--no-mcp stops a window listening for agents, and --mcp's never does");
        }
        if (!screenshot && given.FirstOrDefault(ScreenshotOnly.Contains) is { } step)
        {
            throw new FormatException($"{step} is for a --screenshot, and none is asked for");
        }
    }

    /// <summary>The folder, workspace and files that the path and the <c>--file</c>s name, as full paths, each refused
    /// unless it exists as what it is named for. A solution or a C# project is the workspace over its folder, not a
    /// file to edit.</summary>
    private static (string? Folder, string? Workspace, List<string> Files) Opened(string? path, List<string> named)
    {
        string? folder = null;
        string? workspace = null;
        List<string> files = [];
        if (path is not null)
        {
            if (Directory.Exists(path))
            {
                folder = Path.GetFullPath(path);
            }
            else if (File.Exists(path))
            {
                string full = Path.GetFullPath(path);
                folder = Path.GetDirectoryName(full);
                if (WorkspaceModel.Opens(full))
                {
                    workspace = full;
                }
                else
                {
                    files.Add(full);
                }
            }
            else
            {
                throw new FormatException($"'{path}' is neither a folder nor a file");
            }
        }
        foreach (string file in named)
        {
            // The window opens a relative path as its folder's, so that is where it is looked for.
            string full = Path.GetFullPath(file, folder ?? Environment.CurrentDirectory);
            if (!File.Exists(full))
            {
                throw new FormatException(Directory.Exists(full)
                    ? $"--file '{file}' is a folder; a folder to open is the PATH"
                    : $"--file '{file}' does not exist ({full})");
            }
            files.Add(full);
        }
        return (folder, workspace, files);
    }

    /// <summary>The screenshot, refused unless its PNG has a folder to go in, its frame is one the headless window draws,
    /// and its crop and every point its steps press or move to lie in the window.</summary>
    private static ScreenshotOptions Checked(ScreenshotOptions shot, string? cropText, List<(string Flag, string Text, Point At)> points)
    {
        string full = Path.GetFullPath(shot.Path);
        if (Directory.Exists(full))
        {
            throw new FormatException($"--screenshot takes the PNG file to write, and '{shot.Path}' is a folder");
        }
        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            throw new FormatException($"--screenshot '{shot.Path}' would be written to a folder that does not exist");
        }
        (double width, double height) = shot.Size;
        if (width * shot.Scale > LargestSide || height * shot.Scale > LargestSide)
        {
            throw new FormatException(FormattableString.Invariant($"--size {width}x{height} at --scale {shot.Scale} is {width * shot.Scale}x{height * shot.Scale} pixels, more than {LargestSide} a side"));
        }
        if (shot.Crop is { } crop && (crop.Right > width || crop.Bottom > height))
        {
            throw new FormatException(FormattableString.Invariant($"--crop {cropText} reaches past the {width}x{height} window"));
        }
        foreach ((string flag, string text, Point at) in points)
        {
            if (at.X >= width || at.Y >= height)
            {
                throw new FormatException(FormattableString.Invariant($"{flag} {text} is outside the {width}x{height} window"));
            }
        }
        return shot;
    }

    /// <summary>An agent's command line, split as a run configuration's arguments are, its program a file (from the working
    /// directory) or one on the PATH, given by its full path.</summary>
    private static IReadOnlyList<string> AgentCommand(string text)
    {
        IReadOnlyList<string> words = Run.CommandLine.Split(text);
        if (words.Count == 0)
        {
            throw new FormatException($"--agent takes the agent's command line, not '{text}'");
        }
        string program = words[0];
        string? found = program.Contains('/') || program.Contains(Path.DirectorySeparatorChar)
            ? (File.Exists(program) ? Path.GetFullPath(program) : null)
            : Core.Programs.OnPath(program) ?? (OperatingSystem.IsWindows() ? Core.Programs.OnPath(program + ".exe") : null);
        return found is null
            ? throw new FormatException($"--agent's program '{program}' is neither a file nor on the PATH")
            : [found, .. words.Skip(1)];
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count || args[i + 1].Length == 0)
        {
            throw new FormatException($"{flag} needs a value");
        }
        i++;
        return args[i];
    }

    /// <summary>One number from the least to the most. "NaN" and "Infinity" parse as numbers, and digits past a double's
    /// range as infinity, so a number is also checked to be finite.</summary>
    private static double Number(string flag, string text, string what, double least, double most)
    {
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value < least || value > most)
        {
            throw new FormatException(FormattableString.Invariant($"{flag} takes {what} from {least} to {most}, not '{text}'"));
        }
        return value;
    }

    /// <summary>Several finite numbers of zero or more split by the separator, as the shape says; whether they fit the
    /// window is for the caller, once the window's size is known.</summary>
    private static double[] Numbers(string flag, string text, char separator, int count, string shape)
    {
        string[] parts = text.Split(separator);
        double[] values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            {
                throw new FormatException($"{flag} takes {shape}, each a number of 0 or more, not '{text}'");
            }
        }
        if (values.Length != count)
        {
            throw new FormatException($"{flag} takes {shape}, not '{text}'");
        }
        return values;
    }

    /// <summary>"12:5" is line 12, column 5, as the status bar shows them (from one).</summary>
    private static TextPosition ParseCaret(string text)
    {
        string[] parts = text.Split(':');
        int column = 1;
        if (parts.Length > 2 || !Counted(parts[0], out int line) || (parts.Length == 2 && !Counted(parts[1], out column)))
        {
            throw new FormatException($"--caret takes LINE or LINE:COLUMN from 1, not '{text}'");
        }
        return new TextPosition(line - 1, column - 1);

        static bool Counted(string part, out int value) =>
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;
    }

    /// <summary>"cmd+shift+L", or "meta shift L" as keymaps write it.</summary>
    private static Keystroke ParseKey(string text)
    {
        try
        {
            return Keystroke.Parse(text.Replace('+', ' '));
        }
        catch (FormatException error)
        {
            throw new FormatException($"--key takes a keystroke such as cmd+shift+L, not '{text}': {error.Message}");
        }
    }

    private static ThemeChoice ParseTheme(string text) => text switch
    {
        "dark" => ThemeChoice.Dark,
        "light" => ThemeChoice.Light,
        "system" => ThemeChoice.System,
        _ => throw new FormatException($"--theme takes dark, light or system, not '{text}'"),
    };
}

/// <summary>A screenshot of the main window: its logical size, pixels per logical unit, the steps to
/// take first and the part of the window to keep (logical units), or all of it.</summary>
public sealed record ScreenshotOptions(string Path, LogicalSize Size, double Scale, IReadOnlyList<ScriptStep> Steps, Rect? Crop)
{
    /// <summary>Whether macOS's window buttons, which the headless capture has none of, are drawn in as a person sees
    /// them (<c>--window-buttons</c>); where Windows and Linux are pictured the bar's own are there already.</summary>
    public bool WindowButtons { get; init; }
}

public readonly record struct LogicalSize(double Width, double Height);

/// <summary>One thing a scripted screenshot does before it captures, through the window's real input
/// so it exercises exactly what a person's keys and clicks would.</summary>
public abstract record ScriptStep
{
    public sealed record Type(string Text) : ScriptStep;

    public sealed record Key(Keystroke Stroke) : ScriptStep;

    /// <summary>A click, or a double-click, of the left button, or of the right for a control's menu.</summary>
    public sealed record Click(Point At, int Count) : ScriptStep
    {
        public MouseButton Button { get; init; } = MouseButton.Left;
    }

    /// <summary>A click at the centre of the control of an x:Name shown in the window, found by name rather than by a
    /// point read off a picture.</summary>
    public sealed record ClickNamed(string Name) : ScriptStep;

    /// <summary>The left button pressed at one point and moved to another, let go there unless the capture is to show
    /// the drag itself (a tool window over its drop place).</summary>
    public sealed record Drag(Point From, Point To, bool Release) : ScriptStep;

    /// <summary>The pointer moved to a point and left to rest there, long enough for a hover card.</summary>
    public sealed record Hover(Point At) : ScriptStep;

    /// <summary>Time for what the screenshot does not wait on by itself: a program running.</summary>
    public sealed record Wait(TimeSpan For) : ScriptStep;

    /// <summary>An agent run in a tab of the Terminal window and waited for, the picture refused unless it ends with exit
    /// code 0: its command line as given and split, the program by its full path. It joins the window as any agent does,
    /// through <c>$PASSENGER --mcp-attach $PASSENGER_PID</c>, and uses every tool unasked, since nobody is at a capture to
    /// answer.</summary>
    public sealed record Agent(string Text, IReadOnlyList<string> Command) : ScriptStep;

    /// <summary>A folder, solution or project opened as File, Open opens the one chosen: with one open already, Open
    /// Solution asks where (Settings' default), and a window of its own opens on New Window.</summary>
    public sealed record Open(string Path) : ScriptStep;
}

/// <summary>How the command line asks for the Model Context Protocol server.</summary>
public enum McpMode
{
    None,

    /// <summary>An IDE with no window, its tools on standard input and output.</summary>
    Stdio,

    /// <summary>Standard input and output joined to a running IDE's socket.</summary>
    Attach,
}

/// <summary>A theme forced from the command line over the app's own (<c>App.axaml</c>).</summary>
public enum ThemeChoice
{
    Dark,
    Light,
    System,
}
