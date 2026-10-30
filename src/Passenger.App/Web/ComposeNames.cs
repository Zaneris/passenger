using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>What is typed at a place of a compose file: a sequence's item, a mapping's key, a key's value on its line, or an
/// item of a flow sequence on its key's line.</summary>
public enum ComposeSpotKind
{
    Item,
    Key,
    Value,
    Flow,
}

/// <summary>A place of a compose file where a name is typed, read by its indentation as it is typed (half a line too): the
/// keys from the top to the block holding it ("-" for a sequence's item that holds it), what is typed there, the key whose
/// value it is, the name typed so far and where it begins.</summary>
public sealed record ComposeSpot(IReadOnlyList<string> Path, ComposeSpotKind Kind, string? Key, string Typed, int Start);

/// <summary>A name a compose file offers where one is typed, and what it is (a service, a volume…), with the other file it is
/// declared in for an extends of that file's.</summary>
public sealed record ComposeName(string Name, string Kind, string? File = null);

/// <summary>The names completion offers in a compose file from the file's own model, read from the YAML server's outline:
/// the file's services under a service's depends_on (as a list or a mapping), links, volumes_from, network_mode's
/// <c>service:</c> and extends (the services of the file an extends names, when it names one); its declared volumes in a
/// service's volumes (a short entry's source, a long one's), networks under its networks, and secrets and configs under its
/// secrets and configs. Never the service itself, nor a name its list already holds on another line.</summary>
public static partial class ComposeNames
{
    /// <summary>The place being typed at an offset, or null where no name of the file's is typed (a comment, a value a key
    /// on another line holds, half a flow collection Passenger cannot read).</summary>
    public static ComposeSpot? Spot(TextSnapshot text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = text.GetLineFromOffset(offset);
        int lineStart = text.GetLineStart(line);
        string before = text.GetText(lineStart, offset - lineStart);
        if (Commented(before))
        {
            return null;
        }
        int indent = Spaces(before);
        string rest = before[indent..];
        if (rest.StartsWith("- ", StringComparison.Ordinal))
        {
            int item = indent + 1;
            while (item < before.Length && before[item] == ' ')
            {
                item++;
            }
            string inner = before[item..];
            if (KeyValue().Match(inner) is { Success: true } pair)
            {
                (string value, int at) = Unquoted(pair.Groups["value"].Value, item + pair.Groups["value"].Index);
                return Parents(text, line, indent, dash: true) is { } held
                    ? new ComposeSpot([.. held, "-"], ComposeSpotKind.Value, Key(pair), value, lineStart + at) : null;
            }
            if (inner.StartsWith('[') || inner.StartsWith('{'))
            {
                return null;
            }
            (string typed, int start) = Unquoted(inner, item);
            return Parents(text, line, indent, dash: true) is { } path ? new ComposeSpot(path, ComposeSpotKind.Item, null, typed, lineStart + start) : null;
        }
        if (KeyValue().Match(rest) is { Success: true } keyed)
        {
            string value = keyed.Groups["value"].Value;
            int valueAt = indent + keyed.Groups["value"].Index;
            if (value.StartsWith('['))
            {
                if (value.Contains(']', StringComparison.Ordinal) || value.Contains('{', StringComparison.Ordinal))
                {
                    return null;
                }
                int from = value.LastIndexOfAny(['[', ',']) + 1;
                while (from < value.Length && value[from] == ' ')
                {
                    from++;
                }
                (string item, int at) = Unquoted(value[from..], valueAt + from);
                return Parents(text, line, indent, dash: false) is { } flowed
                    ? new ComposeSpot([.. flowed, Key(keyed)], ComposeSpotKind.Flow, null, item, lineStart + at) : null;
            }
            (string typed, int start) = Unquoted(value, valueAt);
            return Parents(text, line, indent, dash: false) is { } path ? new ComposeSpot(path, ComposeSpotKind.Value, Key(keyed), typed, lineStart + start) : null;
        }
        return rest.Contains(':', StringComparison.Ordinal) || rest.Contains(' ', StringComparison.Ordinal) || rest.StartsWith('-')
            ? null
            : Parents(text, line, indent, dash: false) is { } keys ? new ComposeSpot(keys, ComposeSpotKind.Key, null, rest, lineStart + indent) : null;
    }

    /// <summary>The top-level key whose names a place takes (services, volumes…) and where the name typed there begins
    /// (past network_mode's <c>service:</c>); null where it takes none of the file's names.</summary>
    public static (string Declares, int Start)? Takes(ComposeSpot spot)
    {
        string[] keys = [.. spot.Path];
        if (keys is not ["services", _, ..])
        {
            return null;
        }
        string[] under = keys[2..];
        string typed = spot.Typed;
        int start = spot.Start;
        if (spot.Kind == ComposeSpotKind.Value && spot.Key == "network_mode")
        {
            if (!typed.StartsWith(ComposeFiles.ServicePrefix, StringComparison.Ordinal))
            {
                return null;
            }
            typed = typed[ComposeFiles.ServicePrefix.Length..];
            start += ComposeFiles.ServicePrefix.Length;
        }
        if (!NameSoFar().IsMatch(typed))
        {
            return null;
        }
        bool listed = spot.Kind is ComposeSpotKind.Item or ComposeSpotKind.Flow;
        string? declares = (under, spot.Kind, spot.Key) switch
        {
            (["depends_on"], ComposeSpotKind.Item or ComposeSpotKind.Key or ComposeSpotKind.Flow, _) => "services",
            (["links"] or ["volumes_from"], _, _) when listed => "services",
            ([], ComposeSpotKind.Value, "network_mode" or "extends") => "services",
            (["extends"], ComposeSpotKind.Value, "service") => "services",
            (["volumes"], _, _) when listed => "volumes",
            (["networks"], ComposeSpotKind.Item or ComposeSpotKind.Key or ComposeSpotKind.Flow, _) => "networks",
            (["secrets"] or ["configs"], _, _) when listed => under[0],
            (["volumes" or "secrets" or "configs", "-"], ComposeSpotKind.Value, "source") => under[0],
            _ => null,
        };
        return declares is null ? null : (declares, start);
    }

    /// <summary>The names offered at a place of the file at <paramref name="path"/>: what the YAML server's outline of it
    /// declares, or for an extends of another file, that file's services (<paramref name="texts"/> reads it).</summary>
    public static IReadOnlyList<ComposeName> At(string path, TextSnapshot text, IReadOnlyList<DocumentSymbol> outline, ComposeSpot spot, Func<string, TextSnapshot?> texts)
    {
        if (Takes(spot) is not (string declares, _) || spot.Path is not ["services", var service, ..])
        {
            return [];
        }
        string[] under = [.. spot.Path.Skip(2)];
        bool listed = spot.Kind is ComposeSpotKind.Item or ComposeSpotKind.Flow;
        string kind = declares[..^1];
        ComposeText read = ComposeText.Of(text.ToString());
        if (under is ["extends"] && read.At(["services", service, "extends", "file"])?.Scalar is { Length: > 0 } other)
        {
            string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, other));
            return texts(full) is { } elsewhere ? [.. ServicesIn(elsewhere).Select(s => new ComposeName(s, kind, other))] : [];
        }
        HashSet<string> taken = (under.Length == 1 && listed) || spot.Kind == ComposeSpotKind.Key || spot.Key == "source"
            ? Taken(read, text, ["services", service, under[0]], kind, spot.Start) : [];
        if (declares == "services")
        {
            taken.Add(service);
        }
        return [.. (outline.FirstOrDefault(s => s.Name == declares)?.Children ?? [])
            .Select(s => s.Name).Where(name => name.Length > 0 && !taken.Contains(name)).Distinct()
            .Select(name => new ComposeName(name, kind))];
    }

    /// <summary>The names a service's list (or mapping) already holds on its other entries, the one typed at
    /// <paramref name="typing"/> aside.</summary>
    private static HashSet<string> Taken(ComposeText read, TextSnapshot text, string[] list, string kind, int typing)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        string written = text.ToString();
        int line = text.GetLineFromOffset(Math.Min(typing, text.Length));
        if (read.At(list) is { ValueStart: < 0 } holder && ComposeFixes.FlowItems(written, holder) is { } items)
        {
            foreach (FlowItem item in items.Where(i => typing < i.Between.Start || typing > i.Between.End))
            {
                if (ComposeFixes.Named(kind, list[^1], item.Name) is { } name)
                {
                    taken.Add(name);
                }
            }
            return taken;
        }
        foreach (ComposeEntry entry in ComposeFixes.Children(read, list))
        {
            int first = text.GetLineFromOffset(Math.Min(entry.KeyStart, text.Length));
            int last = read.Entries.Where(e => e.Path.Count >= entry.Path.Count && e.Path.Take(entry.Path.Count).SequenceEqual(entry.Path, StringComparer.Ordinal))
                .Max(e => text.GetLineFromOffset(Math.Min(Math.Max(e.KeyStart, e.ValueStart), text.Length)));
            if (first <= line && line <= last)
            {
                continue;
            }
            string? name = entry.Path[^1].All(char.IsAsciiDigit)
                ? entry.Scalar is { } scalar ? ComposeFixes.Named(kind, list[^1], scalar) : read.At([.. entry.Path, "source"])?.Scalar
                : entry.Path[^1];
            if (name is not null)
            {
                taken.Add(name);
            }
        }
        return taken;
    }

    /// <summary>The services another compose file declares, read as its keys are indented: the keys under its top's services.</summary>
    private static IEnumerable<string> ServicesIn(TextSnapshot text)
    {
        ComposeText read = ComposeText.Of(text.ToString());
        return ComposeFixes.Children(read, ["services"]).Select(e => e.Path[^1]);
    }

    /// <summary>The keys from the top that hold a line's entry at <paramref name="column"/> (a sequence's item when
    /// <paramref name="dash"/>), read up the lines above it by their indentation; null where the lines above hold no such
    /// block (a key whose value is on its line).</summary>
    private static List<string>? Parents(TextSnapshot text, int line, int column, bool dash)
    {
        List<string> path = [];
        int need = column;
        bool needDash = dash;
        for (int i = line - 1; i >= 0 && (need > 0 || needDash); i--)
        {
            string written = text.GetLineText(i);
            int indent = Spaces(written);
            string content = written[indent..].TrimEnd();
            if (content.Length == 0 || content.StartsWith('#') || indent > need)
            {
                continue;
            }
            bool isDash = content == "-" || content.StartsWith("- ", StringComparison.Ordinal);
            if (indent == need)
            {
                // A sequence written at its key's own column has that key for its parent; anything else there is a sibling.
                if (needDash && !isDash)
                {
                    if (KeyOnly(content) is not { } holder)
                    {
                        return null;
                    }
                    path.Insert(0, holder);
                    needDash = false;
                }
                continue;
            }
            if (isDash)
            {
                int inner = indent + 1;
                while (inner < written.Length && written[inner] == ' ')
                {
                    inner++;
                }
                if (inner == need && !needDash)
                {
                    // The keys of a sequence's item that is a mapping, its first on the dash's line.
                    path.Insert(0, "-");
                }
                else if (inner < need && KeyOnly(written[inner..].TrimEnd()) is { } itemKey)
                {
                    path.Insert(0, itemKey);
                    path.Insert(0, "-");
                }
                else
                {
                    return null;
                }
                need = indent;
                needDash = true;
                continue;
            }
            if (KeyOnly(content) is not { } key)
            {
                return null;
            }
            path.Insert(0, key);
            need = indent;
            needDash = false;
        }
        return need == 0 && !needDash ? path : null;
    }

    private static int Spaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }
        return count;
    }

    /// <summary>Whether a line before the caret has begun a comment: a '#' at its start or after a space, outside quotes.</summary>
    private static bool Commented(string before)
    {
        char quote = '\0';
        for (int i = 0; i < before.Length; i++)
        {
            char c = before[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || before[i - 1] is ' ' or '\t'))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A value typed, past its opening quote, and where it begins.</summary>
    private static (string Typed, int At) Unquoted(string value, int at) =>
        value.Length > 0 && value[0] is '"' or '\'' ? (value[1..], at + 1) : (value, at);

    private static string Key(Match pair) => pair.Groups["key"].Value.Trim('"', '\'');

    /// <summary>A line's key when its value is not on its line (a block below it), else null.</summary>
    private static string? KeyOnly(string content) =>
        KeyAlone().Match(content) is { Success: true } alone ? alone.Groups["key"].Value.Trim('"', '\'') : null;

    /// <summary>A key and what is typed after its colon on the line.</summary>
    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_.\-]+|""[^""]*""|'[^']*'):(?:\s+|$)(?<value>.*)$")]
    private static partial Regex KeyValue();

    /// <summary>A key alone on its line, but for an anchor or a comment after it.</summary>
    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_.\-]+|""[^""]*""|'[^']*')\s*:(?:\s+&\S+)?\s*(?:#.*)?$")]
    private static partial Regex KeyAlone();

    /// <summary>What a name may be so far: the characters Compose's names are made of.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]*$")]
    private static partial Regex NameSoFar();
}
