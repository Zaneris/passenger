using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>A program's or a build's console: the code editor, read-only, following the end while the output grows, with
/// the run toolbar down its left side (rerun, stop, clear). Output comes through its <see cref="Sink"/> from any thread;
/// standard error is drawn in the error colour, and what a person types goes to the program's <see cref="Input"/> a line
/// at a time, in the input colour. It keeps a cycle buffer of text, the oldest lines dropped past it. Its file locations
/// and web addresses are links (<see cref="Links"/>).</summary>
public sealed class OutputView : DockPanel
{
    /// <summary>The cycle buffer a console keeps by default: 1024 KB of text.</summary>
    public const int DefaultLimitKb = 1024;

    private readonly Button rerun;
    private readonly Button stop;
    private readonly Button hotReload;
    private readonly ConsoleColours colours = new();
    private int limit = DefaultLimitKb * 1024;
    private ConsoleInput? input;

    /// <summary>Where the line being typed begins, at the end when none is: output goes in before it.</summary>
    private int typedFrom;

    /// <summary>The console's own edits under way, which are never a Clear.</summary>
    private bool editing;

    /// <summary>Its editor, made the first time the console is shown or asked for (<see cref="Editor"/>): a console a run
    /// writes to while its window is hidden keeps its text and colours alone until then, as a window's start makes none.</summary>
    private CodeEditor? editor;

    /// <param name="toolbar">Whether it has the run toolbar: none for a dialog's console, which its own buttons run.</param>
    public OutputView(bool toolbar = true)
    {
        Document = new TextDocument();
        Sink = new ConsoleSink(Flush, limit);
        colours.Links = Links;
        rerun = ToolButtons.Of(IconKeys.Rerun, "Rerun");
        stop = ToolButtons.Of(IconKeys.Stop, "Stop");
        hotReload = ToolButtons.Of(IconKeys.HotReload, "Apply Hot Reload Changes");
        ((PathIcon)hotReload.Content!).Bind(PathIcon.ForegroundProperty, hotReload.GetResourceObservable(TokenKeys.HotReload));
        hotReload.IsVisible = false;
        Button clear = ToolButtons.Of(IconKeys.Clear, "Clear");
        stop.IsEnabled = false;
        rerun.Click += (_, _) => RerunClicked?.Invoke(this, EventArgs.Empty);
        stop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        hotReload.Click += (_, _) => HotReloadClicked?.Invoke(this, EventArgs.Empty);
        clear.Click += (_, _) => Clear();
        Border tools = ToolBars.Side(rerun, stop, hotReload, clear, ToolBars.Gap(), Overlays);
        if (toolbar)
        {
            Children.Add(tools);
        }
        Document.Changed += (_, change) =>
        {
            if (!editing)
            {
                // Another's change ends the line being typed; one that takes text away is a Clear, colours and all.
                if (change.Edits.Any(e => e.Length > 0))
                {
                    colours.Clear();
                }
                typedFrom = Document.Snapshot.Length;
            }
            editor?.ScrollToEnd();
        };
    }

    /// <summary>Whether its editor has been made, as the console was shown or asked for.</summary>
    public bool HasEditor => editor is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Editor;
    }

    /// <summary>Makes the editor over the text so far, its caret at the end, which it follows as the output grows.</summary>
    private CodeEditor MakeEditor()
    {
        CodeEditor made = new()
        {
            Document = Document,
            IsReadOnly = true,
            LineCommentPrefix = null,
            ShowLineNumbers = false,
            ShowErrorStripe = false,
            FontSize = 12.5,
            Highlighter = colours,
        };
        editor = made;
        made.Links = Links.On;
        made.LinkClicked += (_, link) => LinkClicked?.Invoke(this, (LinkTarget)link.Target);
        made.ContextMenuRequested += (_, at) => ShowMenu(at);
        // Ahead of the editor's own handling, which a read-only editor gives nothing to.
        made.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        made.AddHandler(TextInputEvent, OnText, RoutingStrategies.Tunnel);
        Children.Add(made);
        made.Selections = SelectionSet.Single(typedFrom);
        made.ScrollToEnd();
        return made;
    }

    /// <summary>A link in the output was clicked: a file's place to open, or an address for the browser.</summary>
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>The output's file locations and web addresses, found as it is drawn; its folders are where a relative path
    /// is looked for.</summary>
    public ConsoleLinks Links { get; } = new();

    public event EventHandler? RerunClicked;
    public event EventHandler? StopClicked;

    /// <summary>Avalonia's debug overlays in the app the console runs, shown while one with the inspector in it runs.</summary>
    public OverlaysButton Overlays { get; } = new() { MenuPlacement = PlacementMode.RightEdgeAlignedTop };

    /// <summary>Apply Hot Reload Changes pressed: shown while the program runs under dotnet watch.</summary>
    public event EventHandler? HotReloadClicked;

    /// <summary>Has the toolbar's tips name the keys of the window's actions its buttons do, as every tool button's do
    /// (<see cref="ActionTips"/>): the Run window's. The Build window's Rerun is Rebuild, no action's keys, so it has none.</summary>
    public void NameKeys(string rerunAction, string stopAction, string hotReloadAction)
    {
        ActionTips.Set(rerun, rerunAction, "Rerun");
        ActionTips.Set(stop, stopAction, "Stop");
        ActionTips.Set(hotReload, hotReloadAction, "Apply Hot Reload Changes");
    }

    /// <summary>Shows Apply Hot Reload Changes while the program runs with Hot Reload.</summary>
    public void ShowHotReload(bool on) => hotReload.IsVisible = on;

    /// <summary>Whether Apply Hot Reload Changes shows.</summary>
    public bool ShowsHotReload => hotReload.IsVisible;

    /// <summary>A program's text as it reached the console, its colour codes gone, a batch at a time.</summary>
    public event EventHandler<string>? Written;

    public TextDocument Document { get; }

    /// <summary>The console's editor, made the first time it is shown or asked for.</summary>
    public CodeEditor Editor => editor ?? MakeEditor();

    /// <summary>Where programs write to this console, from any thread.</summary>
    public ConsoleSink Sink { get; }

    /// <summary>How much text the console keeps, in characters; past it the oldest lines go.</summary>
    public int Limit
    {
        get => limit;
        set
        {
            limit = Math.Max(1, value);
            Sink.Limit = limit;
            Trim();
        }
    }

    /// <summary>Where the console's typing goes: the running program's input, or null when nothing will read it. The
    /// program's end, or the end of its input (Ctrl+D), sets it back to null.</summary>
    public ConsoleInput? Input
    {
        get => input;
        set
        {
            UiThread.Check();
            input = value;
            if (value is null)
            {
                // What was typed and never sent stays as text, no longer a line being typed.
                typedFrom = Document.Snapshot.Length;
            }
        }
    }

    /// <summary>The console's menu: Copy the selection, Select All, and Clear All.</summary>
    private void ShowMenu(Point? at)
    {
        Keymap keymap = AppActions.Keymap();
        MenuItem Item(string header, string? action, Action run)
        {
            MenuItem item = new() { Header = header };
            if (action is not null && keymap.ShortcutsFor(action).FirstOrDefault(s => s.Second is null) is { } keys)
            {
                item.InputGesture = new KeyGesture(keys.First.Key, keys.First.Modifiers);
            }
            item.Click += (_, _) => run();
            return item;
        }
        MenuItem copy = Item("Copy", EditorActions.Copy, () => Editor.TryExecute(EditorActions.Copy));
        copy.IsEnabled = !Editor.Selections.Primary.IsEmpty;
        ContextMenu menu = new()
        {
            PlacementTarget = Editor,
            Items =
            {
                copy,
                Item("Select All", EditorActions.SelectAll, () => Editor.TryExecute(EditorActions.SelectAll)),
                new Separator(),
                Item("Clear All", null, Clear),
            },
        };
        if (at is null)
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementRect = new Rect(Editor.PointOf(Editor.Selections.Primary.Caret), new Size(1, Editor.LineHeight));
        }
        Menu = menu;
        menu.Closed += (_, _) => Menu = null;
        menu.Open(Editor);
    }

    /// <summary>The console's menu while it is open.</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>Adds the IDE's own words (a command line, how a run ended, why it could not start) after everything
    /// written before them, at once; in the error colour when they say what went wrong.</summary>
    public void Append(string text, bool error = false)
    {
        UiThread.Check();
        Flush();
        Insert(text, [new ConsoleRun(0, text.Length, error ? ConsoleStream.Error : ConsoleStream.System)]);
    }

    /// <summary>Empties the console: its text, what is still on its way, and the line being typed.</summary>
    public void Clear()
    {
        UiThread.Check();
        Sink.Drop();
        Document.Clear();
        Links.Forget();
    }

    /// <summary>Takes in what waits in the sink now, rather than at the next frame: before the IDE says a program
    /// ended or stopped, or reads what it wrote.</summary>
    public void Flush()
    {
        UiThread.Check();
        if (Sink.Take() is not { } batch)
        {
            return;
        }
        Insert(batch.Text, batch.Runs);
        Written?.Invoke(this, batch.Runs.Count == 1 && batch.Runs[0].Stream != ConsoleStream.System
            ? batch.Text
            : string.Concat(batch.Runs.Where(r => r.Stream != ConsoleStream.System).Select(r => batch.Text.Substring(r.Start, r.Length))));
    }

    /// <summary>Whether Rerun stays there while what it reruns runs (the Run window's stops the program and runs it again);
    /// the Build window's waits for its build.</summary>
    public bool RerunsWhileRunning { get; init; }

    public void SetRunning(bool running)
    {
        stop.IsEnabled = running;
        rerun.IsEnabled = !running || RerunsWhileRunning;
    }

    /// <summary>Puts text in before the line being typed (at the end when there is none), its error runs coloured, and
    /// keeps the console within its limit.</summary>
    private void Insert(string text, IReadOnlyList<ConsoleRun> runs)
    {
        if (text.Length == 0)
        {
            return;
        }
        int at = typedFrom;
        // With no editor yet there is no caret to keep: the one made later starts at the end.
        Selection caret = editor?.Selections.Primary ?? new Selection(at, at);
        bool following = editor is not null && !editor.Selections.HasMultiple && caret.IsEmpty && caret.Caret >= at;
        int typedCaret = caret.Caret - at;
        Edit(at, 0, text);
        colours.Insert(at, text.Length, runs.Where(r => r.Stream == ConsoleStream.Error).Select(r => (at + r.Start, r.Length)));
        typedFrom += text.Length;
        Trim();
        if (following)
        {
            // A caret at the end, or in the line being typed, stays where it was in that line.
            editor!.Selections = SelectionSet.Single(typedFrom + typedCaret);
        }
    }

    /// <summary>Drops the oldest lines while the text is past the limit, from a line's start where one is near enough.</summary>
    private void Trim()
    {
        TextSnapshot text = Document.Snapshot;
        int over = text.Length - limit;
        if (over <= 0)
        {
            return;
        }
        int line = text.GetLineFromOffset(over);
        int cut = text.GetLineStart(line) == over || line + 1 >= text.LineCount ? over : text.GetLineStart(line + 1);
        cut = Math.Min(cut, typedFrom);
        if (cut <= 0)
        {
            return;
        }
        Edit(0, cut, string.Empty);
        colours.Trim(cut);
        typedFrom -= cut;
    }

    private void Edit(int start, int length, string text)
    {
        editing = true;
        try
        {
            if (start == Document.Snapshot.Length && length == 0)
            {
                Document.Append(text);
            }
            else
            {
                Document.Rewrite(start, length, text);
            }
        }
        finally
        {
            editing = false;
        }
    }

    private void OnText(object? sender, TextInputEventArgs e)
    {
        if (input is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        Type(e.Text);
        e.Handled = true;
    }

    /// <summary>The keys a console with a program's input takes: Enter sends the line, Backspace takes back what was typed,
    /// Ctrl+D (Rider's Send EOF, on every platform) ends the input, and Paste types the clipboard.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (input is null)
        {
            return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            SendLine();
        }
        else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
        {
            Backspace();
        }
        else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
        {
            EndInput();
        }
        else if (AppActions.Keymap().ActionFor(new Shortcut(Keystroke.From(e))) == EditorActions.Paste)
        {
            IdeErrors.Observe(PasteAsync());
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    /// <summary>Text typed at the end of the line being typed, the caret after it.</summary>
    private void Type(string typed)
    {
        string text = new([.. typed.Where(c => c >= ' ' && c != '\u007f')]);
        if (text.Length == 0)
        {
            return;
        }
        int end = Document.Snapshot.Length;
        Edit(end, 0, text);
        colours.Insert(end, text.Length, [(end, text.Length)], TokenKind.UserInput);
        Editor.Selections = SelectionSet.Single(Document.Snapshot.Length);
    }

    private void SendLine()
    {
        TextSnapshot text = Document.Snapshot;
        string line = text.GetText(typedFrom, text.Length - typedFrom);
        Edit(text.Length, 0, "\n");
        typedFrom = Document.Snapshot.Length;
        Editor.Selections = SelectionSet.Single(typedFrom);
        input?.Send(line + "\n");
    }

    private void Backspace()
    {
        int end = Document.Snapshot.Length;
        if (end == typedFrom)
        {
            return;
        }
        int length = end - typedFrom >= 2 && char.IsLowSurrogate(Document.Snapshot[end - 1]) && char.IsHighSurrogate(Document.Snapshot[end - 2]) ? 2 : 1;
        Edit(end - length, length, string.Empty);
        colours.Remove(end - length);
        Editor.Selections = SelectionSet.Single(Document.Snapshot.Length);
    }

    /// <summary>Send EOF: what was typed goes without a line break, "^D" is shown, and the program's input ends.</summary>
    private void EndInput()
    {
        if (input is not { } ending)
        {
            return;
        }
        TextSnapshot text = Document.Snapshot;
        if (text.Length > typedFrom)
        {
            ending.Send(text.GetText(typedFrom, text.Length - typedFrom));
        }
        typedFrom = text.Length;
        Input = null;
        Append("^D\n");
        ending.Close();
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } pasted)
        {
            return;
        }
        string[] lines = pasted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length && input is not null; i++)
        {
            Type(lines[i]);
            if (i < lines.Length - 1)
            {
                SendLine();
            }
        }
    }

    /// <summary>The console's coloured stretches (standard error, what was typed), in order and apart. They are kept in
    /// offsets from the start of all the console has held since it was last cleared, so dropping its oldest lines moves
    /// none of them, and a line's colours are found by halving.</summary>
    private sealed class ConsoleColours : IHighlighter
    {
        private readonly List<SyntaxSpan> spans = [];
        private ConsoleLinks? links;

        /// <summary>How much text has been dropped from the start since the console was cleared.</summary>
        private int dropped;

        public event EventHandler? Changed;

        /// <summary>The console's links, drawn over the streams' colours (a stack trace's frames in standard error too).</summary>
        public ConsoleLinks? Links
        {
            set
            {
                links = value;
                if (value is not null)
                {
                    value.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>Text put in at an offset: the stretches from there on move after it, and those given are coloured.</summary>
        public void Insert(int at, int length, IEnumerable<(int Start, int Length)> coloured, TokenKind kind = TokenKind.ErrorOutput)
        {
            int index = First(at + dropped);
            for (int i = index; i < spans.Count; i++)
            {
                spans[i] = spans[i] with { Start = spans[i].Start + length };
            }
            foreach ((int start, int count) in coloured)
            {
                SyntaxSpan span = new(start + dropped, count, kind);
                if (index > 0 && spans[index - 1].Kind == kind && spans[index - 1].End == span.Start)
                {
                    spans[index - 1] = spans[index - 1] with { Length = spans[index - 1].Length + count };
                }
                else
                {
                    spans.Insert(index++, span);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Text from an offset to the end was taken away (a character typed, taken back).</summary>
        public void Remove(int from)
        {
            int at = from + dropped;
            for (int i = spans.Count - 1; i >= 0 && spans[i].End > at; i--)
            {
                if (spans[i].Start >= at)
                {
                    spans.RemoveAt(i);
                }
                else
                {
                    spans[i] = spans[i] with { Length = at - spans[i].Start };
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The oldest text, to an offset, was dropped.</summary>
        public void Trim(int length)
        {
            dropped += length;
            int gone = First(dropped);
            spans.RemoveRange(0, gone);
            if (spans.Count > 0 && spans[0].Start < dropped)
            {
                spans[0] = new SyntaxSpan(dropped, spans[0].End - dropped, spans[0].Kind);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            spans.Clear();
            dropped = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
        {
            int end = start + length + dropped;
            List<SyntaxSpan> found = [];
            for (int i = First(start + dropped); i < spans.Count && spans[i].Start < end; i++)
            {
                found.Add(spans[i] with { Start = spans[i].Start - dropped });
            }
            IReadOnlyList<SyntaxSpan> linked = links?.GetSpans(text, start, length) ?? [];
            return linked.Count == 0 ? found : Under(found, linked);
        }

        /// <summary>The streams' colours with the links laid over them: a stretch a link covers is the link's alone.</summary>
        private static List<SyntaxSpan> Under(List<SyntaxSpan> coloured, IReadOnlyList<SyntaxSpan> linked)
        {
            List<SyntaxSpan> merged = [.. linked];
            foreach (SyntaxSpan span in coloured)
            {
                int from = span.Start;
                foreach (SyntaxSpan link in linked)
                {
                    if (link.End <= from)
                    {
                        continue;
                    }
                    if (link.Start >= span.End)
                    {
                        break;
                    }
                    if (link.Start > from)
                    {
                        merged.Add(span with { Start = from, Length = link.Start - from });
                    }
                    from = link.End;
                }
                if (from < span.End)
                {
                    merged.Add(span with { Start = from, Length = span.End - from });
                }
            }
            merged.Sort((a, b) => a.Start.CompareTo(b.Start));
            return merged;
        }

        /// <summary>The first stretch reaching past an offset, found by halving: they are in order and apart.</summary>
        private int First(int offset)
        {
            int low = 0;
            int high = spans.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (spans[middle].End <= offset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }
}
