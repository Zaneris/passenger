namespace Passenger.Workspace;

/// <summary>Where a caret is in JSON as typed, half a line too (a configuration file's key or value being written): the
/// names of the objects around it from the root (an array's item by its index), whether it is where a property's name goes
/// or a value, the stretch of what is typed there (a string's quotes in, as far as written), the property whose value it is,
/// and the names the object holding it has already, before the caret and after.</summary>
public sealed record JsonSpot(IReadOnlyList<string> Path, bool AtName, int Start, int End, string? Property, IReadOnlyList<string> Names)
{
    /// <summary>The text typed at the spot from its start to the caret, a string's opening quote left out.</summary>
    public string Typed(string text, int caret) => text[Start..Math.Min(caret, text.Length)].TrimStart('"');

    /// <summary>The spot at a caret, read through the text from its start, passing over comments and strings as .NET's
    /// configuration reads them; null outside every object, in a comment, or where neither a name nor a value goes.</summary>
    public static JsonSpot? At(string text, int caret)
    {
        List<Frame> frames = [];
        JsonSpot? spot = null;
        int holding = -1;
        int at = 0;
        while (at <= text.Length)
        {
            if (at == caret && spot is null)
            {
                spot = Here(frames, caret, caret, caret, text);
                holding = spot is null ? -2 : frames.Count;
            }
            if (at >= text.Length)
            {
                break;
            }
            Frame? top = frames.Count > 0 ? frames[^1] : null;
            char c = text[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }
            if (c == '/' && at + 1 < text.Length && text[at + 1] is '/' or '*')
            {
                int end = text[at + 1] == '/' ? text.IndexOfAny(['\r', '\n'], at) : text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : text[at + 1] == '/' ? end : end + 2;
                if (caret > at && caret < end)
                {
                    return null;
                }
                at = end;
                continue;
            }
            if (c == '"')
            {
                int end = at + 1;
                while (end < text.Length && text[end] != '"' && text[end] is not ('\r' or '\n'))
                {
                    end += text[end] == '\\' ? 2 : 1;
                }
                int close = Math.Min(text.Length, end < text.Length && text[end] == '"' ? end + 1 : end);
                string value = Unescaped(text[(at + 1)..Math.Min(end, text.Length)]);
                if (spot is null && caret > at && caret <= close && (caret < close || close == end))
                {
                    spot = Here(frames, at, close, caret, text);
                    holding = spot is null ? -2 : frames.Count;
                }
                if (top is { IsObject: true, State: State.Name })
                {
                    top.Name = value;
                    top.Names.Add(value);
                    top.State = State.Colon;
                }
                else if (top is not null)
                {
                    top.State = State.After;
                }
                at = Math.Max(close, at + 1);
                continue;
            }
            switch (c)
            {
                case '{' or '[':
                    string? name = top is null ? null : top.IsObject ? top.Name : top.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (top is not null)
                    {
                        top.State = State.After;
                    }
                    frames.Add(new Frame(c == '{', name) { State = c == '{' ? State.Name : State.Value });
                    break;
                case '}' or ']':
                    if (frames.Count > 0)
                    {
                        frames.RemoveAt(frames.Count - 1);
                    }
                    if (spot is not null && frames.Count < holding)
                    {
                        return spot;
                    }
                    break;
                case ':' when top is { IsObject: true }:
                    top.State = State.Value;
                    break;
                case ',' when top is not null:
                    top.State = top.IsObject ? State.Name : State.Value;
                    if (!top.IsObject)
                    {
                        top.Index++;
                    }
                    break;
                default:
                    int literal = at;
                    while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not (',' or '}' or ']' or ':' or '"' or '/'))
                    {
                        at++;
                    }
                    if (spot is null && caret > literal && caret <= at)
                    {
                        spot = Here(frames, literal, at, caret, text);
                        holding = spot is null ? -2 : frames.Count;
                    }
                    // A literal's end that means nothing here is passed over, or the walk would stand on it for good.
                    if (at == literal)
                    {
                        at++;
                        continue;
                    }
                    if (top is not null)
                    {
                        top.State = State.After;
                    }
                    continue;
            }
            at++;
        }
        return spot;
    }

    private static JsonSpot? Here(List<Frame> frames, int start, int end, int caret, string text)
    {
        if (frames.Count == 0 || frames[^1] is not { } top)
        {
            return null;
        }
        IReadOnlyList<string> path = [.. frames.Skip(1).Select(f => f.Named ?? string.Empty)];
        return top.State switch
        {
            State.Name or State.Colon when top.IsObject && (top.State == State.Name || start < caret) =>
                new JsonSpot(path, true, start, end, null, top.Names),
            State.Value when top.IsObject => new JsonSpot(path, false, start, end, top.Name, top.Names),
            State.Value => new JsonSpot([.. path, top.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)], false, start, end, null, []),
            State.After when start < caret => top.IsObject ? new JsonSpot(path, false, start, end, top.Name, top.Names)
                : new JsonSpot([.. path, top.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)], false, start, end, null, []),
            _ => null,
        };
    }

    private static string Unescaped(string written)
    {
        if (!written.Contains('\\', StringComparison.Ordinal))
        {
            return written;
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + written + "\"") ?? written;
        }
        catch (System.Text.Json.JsonException)
        {
            return written;
        }
    }

    private enum State
    {
        Name,
        Colon,
        Value,
        After,
    }

    /// <summary>An object or array open at a place: what its holder calls it, the name it is at, the names it has.</summary>
    private sealed class Frame(bool isObject, string? named)
    {
        public bool IsObject { get; } = isObject;

        public string? Named { get; } = named;

        public State State { get; set; }

        public string? Name { get; set; }

        public int Index { get; set; }

        public List<string> Names { get; } = [];
    }
}
