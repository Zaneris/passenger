using Passenger.App.Workspace;
using Passenger.Text;

namespace Passenger.App.Godot;

/// <summary>One time an error or warning came: when (since the game's start), what it said, and the stack it was raised on.</summary>
public sealed record GodotOccurrence(long Milliseconds, string Said, bool Warning, IReadOnlyList<GodotFrame> Frames);

/// <summary>The errors a game's run raised at one line of a file, pinned there: how many came, the last of them, and each one
/// kept (the newest <see cref="GodotErrorPins.Kept"/>, the earlier ones only counted). Its line is carried by the file's
/// text while it is open.</summary>
public sealed class GodotPin(string path, int line, string lineText)
{
    private readonly List<GodotOccurrence> occurrences = [];

    public string Path { get; } = path;

    /// <summary>The line (from zero), as its anchor last read it.</summary>
    public int Line => At.Line;

    /// <summary>The line's text as the first error found it, which a file opened later is held to.</summary>
    public string LineText { get; } = lineText;

    internal AnchoredLine At { get; } = new(line);

    public int Count { get; private set; }

    /// <summary>Whether every one that came was a warning.</summary>
    public bool Warnings { get; private set; } = true;

    public GodotOccurrence Last => occurrences[^1];

    /// <summary>The occurrences kept, oldest first.</summary>
    public IReadOnlyList<GodotOccurrence> Occurrences => occurrences;

    /// <summary>How many came before those kept.</summary>
    public int Earlier => Count - occurrences.Count;

    internal void Add(GodotOccurrence occurrence)
    {
        Count++;
        Warnings &= occurrence.Warning;
        occurrences.Add(occurrence);
        if (occurrences.Count > GodotErrorPins.Kept)
        {
            occurrences.RemoveAt(0);
        }
    }
}

/// <summary>Each error and warning the last Godot game run raised, pinned to its line among the project's files (as
/// Problems places it, <see cref="GodotError.PlaceIn"/>) for as long as that line stands: the pins of a file come off when
/// an edit changes their line, the next game's start takes them all, and a person takes one or all by hand. A file opened
/// after its line changed on disk shows none there. A UI-thread object of the session.</summary>
public sealed class GodotErrorPins
{
    /// <summary>The most occurrences a pin keeps, the newest.</summary>
    public const int Kept = 1000;

    private readonly List<GodotPin> pins = [];

    /// <summary>A file's pins changed: came, counted, moved or went.</summary>
    public event EventHandler<string>? Changed;

    public IReadOnlyList<GodotPin> All => pins;

    public IReadOnlyList<GodotPin> In(string path) => [.. pins.Where(p => Passenger.Core.PathNames.Same(p.Path, path))];

    /// <summary>A game launched: the last run's pins go, and this one's errors pin as they come.</summary>
    public void Follow(GodotGame game, Func<string, TextSnapshot?> textOf, Func<string, TextDocument?> openText)
    {
        UiThread.Check();
        Clear();
        game.Raised += (_, error) =>
        {
            (string? path, int line) = error.PlaceIn(game.Folder, game.PathOf);
            if (path is null || line < 1)
            {
                return;
            }
            GodotPin? pin = pins.FirstOrDefault(p => p.Line == line - 1 && Passenger.Core.PathNames.Same(p.Path, path));
            if (pin is null)
            {
                // Read once a pin is made: an error raised every frame is only counted after.
                if (textOf(path) is not { } text || line > text.LineCount)
                {
                    return;
                }
                LineSpan span = text.GetLine(line - 1);
                pin = new GodotPin(path, line - 1, text.GetText(span.Start, span.Length));
                if (openText(path) is { } open)
                {
                    pin.At.Attach(open);
                }
                pins.Add(pin);
            }
            pin.Add(new GodotOccurrence(error.Milliseconds, error.Said, error.Warning, error.Frames));
            Told(path);
        };
    }

    /// <summary>Files whose pins were counted since they were last told of.</summary>
    private readonly HashSet<string> counted = new(Passenger.Core.PathNames.Comparer);

    /// <summary>A file's pins counted: told once for all an error raised every frame counted meanwhile.</summary>
    private void Told(string path)
    {
        if (counted.Add(path) && counted.Count == 1)
        {
            UiThread.Post(() =>
            {
                List<string> told = [.. counted];
                counted.Clear();
                foreach (string file in told)
                {
                    Changed?.Invoke(this, file);
                }
            });
        }
    }

    /// <summary>A file opened: its pins carried by its text from now on, those whose line no longer reads as it did let go.</summary>
    public void Attach(string path, TextDocument text)
    {
        bool went = false;
        foreach (GodotPin pin in In(path))
        {
            TextSnapshot now = text.Snapshot;
            LineSpan span = pin.Line < now.LineCount ? now.GetLine(pin.Line) : default;
            if (pin.Line >= now.LineCount || now.GetText(span.Start, span.Length) != pin.LineText)
            {
                pins.Remove(pin);
                went = true;
                continue;
            }
            pin.At.Attach(text);
        }
        if (went)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>A file closed: its pins stay where they were last read.</summary>
    public void Detach(string path)
    {
        foreach (GodotPin pin in In(path))
        {
            pin.At.Detach();
        }
    }

    /// <summary>An edit of an open file: the pins on a line whose text it changed come off, the rest follow their lines.</summary>
    public void Edited(string path, DocumentChangedEventArgs change)
    {
        bool moved = false;
        foreach (GodotPin pin in In(path))
        {
            string? was = LineOf(change.Before, pin.Line);
            bool followed = pin.At.Follow();
            if (LineOf(change.After, pin.Line) != was)
            {
                pins.Remove(pin);
                pin.At.Detach();
                moved = true;
                continue;
            }
            moved |= followed;
        }
        if (moved)
        {
            Changed?.Invoke(this, path);
        }

        static string? LineOf(TextSnapshot text, int line)
        {
            if (line >= text.LineCount)
            {
                return null;
            }
            LineSpan span = text.GetLine(line);
            return text.GetText(span.Start, span.Length);
        }
    }

    /// <summary>A pin taken off by hand.</summary>
    public void Remove(GodotPin pin)
    {
        if (pins.Remove(pin))
        {
            pin.At.Detach();
            Changed?.Invoke(this, pin.Path);
        }
    }

    /// <summary>Every pin taken off.</summary>
    public void Clear()
    {
        List<string> paths = [.. pins.Select(p => p.Path).Distinct(Passenger.Core.PathNames.Comparer)];
        foreach (GodotPin pin in pins)
        {
            pin.At.Detach();
        }
        pins.Clear();
        foreach (string path in paths)
        {
            Changed?.Invoke(this, path);
        }
    }
}
