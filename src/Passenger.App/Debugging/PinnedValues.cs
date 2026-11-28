using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>An expression pinned to a line (from zero) of a file, carried with the file's text while it is open: its value
/// is read again at each stop with a frame in the file and shown after the line.</summary>
public sealed class PinnedValue(string path, int line, string expression)
{
    public string Path { get; internal set; } = path;

    public string Expression { get; } = expression;

    public int Line => At.Line;

    internal AnchoredLine At { get; } = new(line);
}

/// <summary>An expression pinned as kept: its file, its line from zero and the expression.</summary>
public sealed record KeptPin(string Path, int Line, string Expression);

/// <summary>A folder's pinned values: Pin to Line keeps an expression after a line of a file, read again at each stop. A pin
/// in an open file moves with its line, by an anchor in its text; one expression is pinned to a line once.</summary>
public sealed class PinnedValues
{
    private readonly List<PinnedValue> all = [];

    /// <summary>The text of each file open, whose pins it carries.</summary>
    private readonly Dictionary<string, TextDocument> open = new(StringComparer.Ordinal);

    /// <summary>A file's pins were set or taken away.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>A file's pins moved with its text.</summary>
    public event EventHandler<string>? Moved;

    /// <summary>Every pin, in the order they were set.</summary>
    public IReadOnlyList<PinnedValue> All => all;

    public IReadOnlyList<PinnedValue> In(string path) => [.. all.Where(p => p.Path == path)];

    /// <summary>The pin of an expression on a line, if it is pinned there.</summary>
    public PinnedValue? At(string path, int line, string expression) =>
        all.FirstOrDefault(p => p.Path == path && p.Line == line && p.Expression == expression);

    /// <summary>Every pin as the folder keeps it, in the order they were set.</summary>
    public List<KeptPin> Capture() => [.. all.Select(p => new KeptPin(p.Path, p.Line, p.Expression))];

    /// <summary>Sets the pins a folder kept after those there are; one pinned already keeps its own.</summary>
    public void Restore(IEnumerable<KeptPin> kept)
    {
        HashSet<string> paths = [];
        foreach (KeptPin pin in kept.Where(k => At(k.Path, k.Line, k.Expression) is null))
        {
            all.Add(Made(pin.Path, pin.Line, pin.Expression));
            paths.Add(pin.Path);
        }
        foreach (string path in paths)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Takes every pin away, as another folder opens: each folder's are its own.</summary>
    public void Clear()
    {
        List<string> paths = [.. all.Select(p => p.Path).Distinct(StringComparer.Ordinal)];
        foreach (PinnedValue pin in all)
        {
            pin.At.Detach();
        }
        all.Clear();
        foreach (string path in paths)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Pins an expression to a line, or takes away the pin of it there: whether it is pinned now.</summary>
    public bool Toggle(string path, int line, string expression)
    {
        if (At(path, line, expression) is { } there)
        {
            Remove(there);
            return false;
        }
        all.Add(Made(path, line, expression));
        Changed?.Invoke(this, path);
        return true;
    }

    /// <summary>Pins an expression to a line, where it is not pinned there already.</summary>
    public void Pin(string path, int line, string expression)
    {
        if (At(path, line, expression) is null)
        {
            Toggle(path, line, expression);
        }
    }

    public void Remove(PinnedValue pin)
    {
        if (all.Remove(pin))
        {
            pin.At.Detach();
            Changed?.Invoke(this, pin.Path);
        }
    }

    /// <summary>A file opened: its pins move with its text until it closes.</summary>
    public void Attach(string path, TextDocument text)
    {
        open[path] = text;
        foreach (PinnedValue pin in all.Where(p => p.Path == path))
        {
            pin.At.Attach(text);
        }
    }

    /// <summary>A file closed: its pins stay on the lines they were carried to.</summary>
    public void Detach(string path)
    {
        open.Remove(path);
        foreach (PinnedValue pin in all.Where(p => p.Path == path))
        {
            pin.At.Detach();
        }
    }

    /// <summary>Carries the pins of a file, or of every file in a folder, to where it moved.</summary>
    public void Move(string from, string to)
    {
        HashSet<string> changed = [];
        foreach (PinnedValue pin in all.Where(p => PathNames.Within(p.Path, from)))
        {
            changed.Add(pin.Path);
            pin.Path = PathNames.Moved(pin.Path, from, to);
            changed.Add(pin.Path);
        }
        foreach (string path in open.Keys.Where(p => PathNames.Within(p, from)).ToList())
        {
            open.Remove(path, out TextDocument? text);
            open[PathNames.Moved(path, from, to)] = text!;
        }
        foreach (string path in changed)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Reads the lines of a file's pins after an edit to it, their anchors having carried them; an expression
    /// pinned twice to one line (its lines joined) is pinned once.</summary>
    public void Follow(string path)
    {
        bool moved = false;
        foreach (PinnedValue pin in all.Where(p => p.Path == path))
        {
            moved |= pin.At.Follow();
        }
        if (!moved)
        {
            return;
        }
        List<PinnedValue> twice = [.. all.Where(p => p.Path == path).GroupBy(p => (p.Line, p.Expression)).SelectMany(g => g.Skip(1))];
        foreach (PinnedValue extra in twice)
        {
            all.Remove(extra);
            extra.At.Detach();
        }
        if (twice.Count > 0)
        {
            Changed?.Invoke(this, path);
        }
        Moved?.Invoke(this, path);
    }

    private PinnedValue Made(string path, int line, string expression)
    {
        PinnedValue made = new(path, line, expression);
        if (open.TryGetValue(path, out TextDocument? text))
        {
            made.At.Attach(text);
        }
        return made;
    }
}
