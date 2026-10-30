using System.Text.RegularExpressions;
using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Containers;

/// <summary>What Compose says a service of a compose file names that the file does not declare: a service it depends on
/// (by depends_on, links, volumes_from or network_mode's <c>service:</c>, each of which Compose reads as depending on it),
/// or a volume, network, secret or config it uses. <see cref="Kind"/> is service, volume, network, secret or config.</summary>
public sealed record ComposeFault(string Kind, string Service, string Name)
{
    /// <summary>The top-level key that declares a name of this kind (services, volumes…).</summary>
    public string Key => Kind + "s";
}

/// <summary>Where a service names something in a compose file's text: what of the service names it (depends_on, a
/// volume's list…), the name's stretch, and the entry that holds it (a list's item, a mapping's key), or for a flow
/// sequence on the key's line, the item's stretch between its brackets and commas.</summary>
internal sealed record ComposeReference(string Under, TextRange Name, ComposeEntry Entry, FlowItem? Flow);

/// <summary>An item of a flow sequence (<c>[db, cache]</c>): its name's stretch, unquoted, and the stretch between the
/// bracket or comma before it and the comma or bracket after it.</summary>
internal sealed record FlowItem(string Name, TextRange At, TextRange Between);

/// <summary>Passenger's own fixes for what Compose finds a compose file's services name that it does not declare, read from
/// the file's text as typed (<see cref="ComposeText"/>), so each is worked out again on the text as it stands when it is
/// chosen (<see cref="EditorFix.Again"/>) and refused when the text no longer has what it fixes: Declare X at the top level
/// for a volume, network, secret or config (under its key, made when the file has none, in the file's indentation; a
/// secret or config read from a file beside it), Remove X from depends_on, and Change to Y where the file declares a name
/// near it (by edit distance). Each is one edit, so one undo step. What Passenger cannot be sure of (a flow mapping, a
/// key whose value is on its line) it offers no fix for.</summary>
public static partial class ComposeFixes
{
    /// <summary>The fault a message of Compose's says, or null for any other.</summary>
    public static ComposeFault? Read(string said) =>
        Undefined().Match(said) is { Success: true } used ? new ComposeFault(used.Groups["kind"].Value, used.Groups["service"].Value, used.Groups["name"].Value)
        : DependsOn().Match(said) is { Success: true } depends ? new ComposeFault("service", depends.Groups["service"].Value, depends.Groups["name"].Value)
        : null;

    /// <summary>Where a fault is in the text: the first place its service names it; null when the text has none (a name an
    /// override or an include gives).</summary>
    public static TextRange? Place(string text, ComposeFault fault) =>
        References(ComposeText.Of(text), text, fault).FirstOrDefault()?.Name;

    /// <summary>The fixes for a fault on the text as it stands: none when the text no longer has it (its name declared, or
    /// no longer named by its service).</summary>
    public static IReadOnlyList<EditorFix> For(TextSnapshot snapshot, ComposeFault fault)
    {
        string text = snapshot.ToString();
        ComposeText read = ComposeText.Of(text);
        if (read.At([fault.Key, fault.Name]) is not null)
        {
            return [];
        }
        IReadOnlyList<ComposeReference> references = References(read, text, fault);
        if (references.Count == 0)
        {
            return [];
        }
        List<EditorFix> fixes = [];
        if (fault.Kind != "service" && Declare(read, text, fault) is { } declare)
        {
            fixes.Add(declare);
        }
        if (references.FirstOrDefault(r => r.Under == "depends_on") is { } depended && Remove(read, text, depended) is { } removal)
        {
            fixes.Add(new EditorFix($"Remove {fault.Name} from depends_on", $"the file has no service {fault.Name}", removal, string.Empty));
        }
        ComposeReference first = references[0];
        foreach (string near in Near(read, fault))
        {
            fixes.Add(new EditorFix($"Change to {near}", $"a {fault.Kind} the file declares", first.Name, near));
        }
        return [.. fixes.Select(f => f with { Again = now => For(now, fault).FirstOrDefault(again => again.Title == f.Title) })];
    }

    /// <summary>Set a variable in a service's environment, as the file writes it there: a <c>NAME: "value"</c> after the
    /// mapping's last entry, a <c>- "NAME=value"</c> after the list's, or an environment made under the service after its last
    /// key; an empty flow collection (<c>{}</c>, <c>[]</c>) made the block it goes in. A value of null is the person's to
    /// fill in, the caret left where it goes. None where the text sets it already, has no such service, or writes its
    /// environment in a form Passenger cannot be sure of (a flow collection with entries); worked out again as it is chosen.</summary>
    public static IReadOnlyList<EditorFix> SetVariable(TextSnapshot snapshot, string service, string variable, string? value, string detail)
    {
        string text = snapshot.ToString();
        ComposeText read = ComposeText.Of(text);
        string[] path = ["services", service];
        if (read.At(path) is not { } holder || holder.ValueStart >= 0)
        {
            return [];
        }
        string unit = new(' ', Indent(read, text));
        string br = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string title = $"Set {variable} in {service}'s environment";
        string mapped = $"{variable}: {DoubleQuoted(value ?? string.Empty)}";
        // An empty value's caret between its quotes, where the person types it.
        int mappedCaret = value is null ? mapped.Length - 1 : mapped.Length;
        string listed = $"- {DoubleQuoted($"{variable}={value}")}";
        int listedCaret = value is null ? listed.Length - 1 : listed.Length;
        EditorFix? fix = null;
        if (read.At([.. path, "environment"]) is { } environment)
        {
            IReadOnlyList<ComposeEntry> entries = [.. Children(read, environment.Path)];
            if (entries.Any(e => IsIndex(e.Path[^1])
                ? e.Scalar?.Split('=')[0].Trim().Equals(variable, StringComparison.OrdinalIgnoreCase) == true
                : e.Path[^1].Equals(variable, StringComparison.OrdinalIgnoreCase)))
            {
                return [];
            }
            string inner = new string(' ', Column(text, environment.KeyStart)) + unit;
            if (environment.ValueStart >= 0 || (entries.Count == 0 && FlowValue(text, environment) is { Length: > 0 }))
            {
                // On the key's line: an empty flow mapping or sequence becomes the block the entry goes in.
                string flow = FlowValue(text, environment) ?? string.Empty;
                int open = text.IndexOf(flow, environment.KeyEnd, StringComparison.Ordinal);
                string? block = flow switch
                {
                    "{}" => $"{br}{inner}{mapped}",
                    "[]" => $"{br}{inner}{listed}",
                    _ => null,
                };
                fix = block is null || open < 0 ? null
                    : new EditorFix(title, detail, TextRange.FromBounds(environment.KeyEnd + 1, open + flow.Length), block)
                    {
                        Caret = flow == "{}" ? block.Length - mapped.Length + mappedCaret : block.Length - listed.Length + listedCaret,
                    };
            }
            else if (entries.Count == 0)
            {
                string entry = $"{inner}{mapped}";
                fix = new EditorFix(title, detail, new TextRange(AfterLine(text, environment.KeyEnd), 0), entry + br) { Caret = entry.Length - mapped.Length + mappedCaret };
            }
            else
            {
                bool list = IsIndex(entries[0].Path[^1]);
                string indent = new(' ', Column(text, entries[0].KeyStart));
                int at = Lines(read, text, environment).End;
                string before = at == text.Length && !text.EndsWith('\n') ? br : string.Empty;
                string entry = $"{before}{indent}{(list ? listed : mapped)}";
                fix = new EditorFix(title, detail, new TextRange(at, 0), entry + br)
                {
                    Caret = list ? entry.Length - listed.Length + listedCaret : entry.Length - mapped.Length + mappedCaret,
                };
            }
        }
        else
        {
            IReadOnlyList<ComposeEntry> keys = [.. Children(read, path)];
            string indent = keys.Count > 0 ? new string(' ', Column(text, keys[0].KeyStart)) : new string(' ', Column(text, holder.KeyStart)) + unit;
            int at = Lines(read, text, holder).End;
            string before = at == text.Length && !text.EndsWith('\n') ? br : string.Empty;
            string entry = $"{before}{indent}environment:{br}{indent}{unit}{mapped}";
            fix = new EditorFix(title, detail, new TextRange(at, 0), entry + br) { Caret = entry.Length - mapped.Length + mappedCaret };
        }
        return fix is null ? [] : [fix with { Again = now => SetVariable(now, service, variable, value, detail).FirstOrDefault() }];
    }

    /// <summary>A text as a double-quoted YAML scalar, which reads it whole whatever it holds (a <c> #</c>, a <c>: </c>, a
    /// leading <c>*</c>): its backslashes, quotes and control characters escaped.</summary>
    private static string DoubleQuoted(string text)
    {
        System.Text.StringBuilder quoted = new("\"");
        foreach (char c in text)
        {
            quoted.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>What a key's line holds after it, its comment aside: a flow collection or scalar written there; null for none.</summary>
    private static string? FlowValue(string text, ComposeEntry key)
    {
        int end = text.IndexOf('\n', key.KeyEnd) is int next and >= 0 ? next : text.Length;
        string after = text[Math.Min(key.KeyEnd + 1, end)..end].TrimEnd('\r');
        int comment = after.IndexOf(" #", StringComparison.Ordinal);
        string value = (comment >= 0 ? after[..comment] : after).Trim();
        return value.Length == 0 ? null : value;
    }

    private static int Column(string text, int offset) => offset - LineStart(text, offset);

    /// <summary>Every place the fault's service names its name, in the order written.</summary>
    internal static IReadOnlyList<ComposeReference> References(ComposeText read, string text, ComposeFault fault)
    {
        List<ComposeReference> found = [];
        string[] unders = fault.Kind == "service" ? ["depends_on", "links", "volumes_from", "network_mode"] : [fault.Key];
        foreach (string under in unders)
        {
            string[] path = ["services", fault.Service, under];
            if (read.At(path) is not { } holder)
            {
                continue;
            }
            if (under == "network_mode")
            {
                if (holder.Scalar == ComposeFiles.ServicePrefix + fault.Name)
                {
                    found.Add(new ComposeReference(under, ScalarPart(text, holder, ComposeFiles.ServicePrefix.Length, fault.Name.Length), holder, null));
                }
                continue;
            }
            if (holder.ValueStart < 0 && FlowItems(text, holder) is { } items)
            {
                found.AddRange(items.Where(i => Named(fault.Kind, under, i.Name) == fault.Name)
                    .Select(i => new ComposeReference(under, new TextRange(i.At.Start, fault.Name.Length), holder, i)));
                continue;
            }
            foreach (ComposeEntry entry in Children(read, path))
            {
                string last = entry.Path[^1];
                if (!IsIndex(last))
                {
                    // A mapping's key names it: depends_on's and networks' long forms.
                    if (under is "depends_on" or "networks" && last == fault.Name)
                    {
                        found.Add(new ComposeReference(under, KeyName(text, entry), entry, null));
                    }
                    continue;
                }
                if (entry.Scalar is { } scalar)
                {
                    if (Named(fault.Kind, under, scalar) == fault.Name)
                    {
                        found.Add(new ComposeReference(under, ScalarPart(text, entry, 0, fault.Name.Length), entry, null));
                    }
                }
                else if (fault.Kind is "volume" or "secret" or "config" && read.At([.. entry.Path, "source"]) is { Scalar: { } source } named && source == fault.Name
                    && (fault.Kind != "volume" || read.At([.. entry.Path, "type"])?.Scalar is null or "volume"))
                {
                    found.Add(new ComposeReference(under, ScalarPart(text, named, 0, source.Length), entry, null));
                }
            }
        }
        return found;
    }

    /// <summary>The name a short entry under a service's key gives: the service a link or volumes_from names before its
    /// colon (not a <c>container:</c>), a short volume's source when it is a named volume (not a path or a variable), and
    /// the whole entry elsewhere; null where it names none.</summary>
    internal static string? Named(string kind, string under, string entry)
    {
        switch (under)
        {
            case "links":
                return entry.Split(':')[0];
            case "volumes_from":
                return entry.StartsWith("container:", StringComparison.Ordinal) ? null : entry.Split(':')[0];
            case "volumes" when kind == "volume":
                string source = entry.Split(':')[0];
                return source.Length == 0 || source[0] is '.' or '/' or '~' or '$' || source.Contains('\\') || (source.Length > 1 && source[1] == ':') || !entry.Contains(':')
                    ? null : source;
            default:
                return entry;
        }
    }

    /// <summary>The entries one level under a path: a sequence's items, a mapping's keys.</summary>
    internal static IEnumerable<ComposeEntry> Children(ComposeText read, IReadOnlyList<string> path) =>
        read.Entries.Where(e => e.Path.Count == path.Count + 1 && e.Path.Take(path.Count).SequenceEqual(path, StringComparer.Ordinal));

    private static bool IsIndex(string step) => step.Length > 0 && step.All(char.IsAsciiDigit);

    /// <summary>A stretch of a scalar as written, past its opening quote.</summary>
    internal static TextRange ScalarPart(string text, ComposeEntry entry, int skip, int length) =>
        new(entry.ValueStart + (text[entry.ValueStart] is '"' or '\'' ? 1 : 0) + skip, length);

    /// <summary>A key's name as written, without its quotes.</summary>
    internal static TextRange KeyName(string text, ComposeEntry entry) =>
        text[entry.KeyStart] is '"' or '\'' ? TextRange.FromBounds(entry.KeyStart + 1, entry.KeyEnd - 1) : TextRange.FromBounds(entry.KeyStart, entry.KeyEnd);

    /// <summary>The items of a flow sequence written on a key's line after it; null when the line holds none (a flow
    /// mapping, a scalar) or one Passenger cannot be sure of (nested, unclosed).</summary>
    internal static IReadOnlyList<FlowItem>? FlowItems(string text, ComposeEntry key)
    {
        int end = text.IndexOf('\n', key.KeyEnd) is int next and >= 0 ? next : text.Length;
        int open = key.KeyEnd + 1;
        while (open < end && text[open] == ' ')
        {
            open++;
        }
        if (open >= end || text[open] != '[')
        {
            return null;
        }
        List<FlowItem> items = [];
        int from = open + 1;
        char quote = '\0';
        for (int i = from; i < end; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                return null;
            }
            else if (c is ',' or ']')
            {
                if (Item(text, from, i) is { } item)
                {
                    items.Add(item);
                }
                if (c == ']')
                {
                    return items;
                }
                from = i + 1;
            }
        }
        return null;
    }

    private static FlowItem? Item(string text, int from, int to)
    {
        int start = from;
        int stop = to;
        while (start < stop && text[start] == ' ')
        {
            start++;
        }
        while (stop > start && text[stop - 1] == ' ')
        {
            stop--;
        }
        if (start == stop)
        {
            return null;
        }
        if (text[start] is '"' or '\'' && stop - start >= 2 && text[stop - 1] == text[start])
        {
            start++;
            stop--;
        }
        return new FlowItem(text[start..stop], TextRange.FromBounds(start, stop), TextRange.FromBounds(from, to));
    }

    /// <summary>The stretch Remove takes away: a flow item with its comma (the line, when it is the only one), else the
    /// entry's lines with what is nested under it (depends_on's whole block, when it is the only one there).</summary>
    private static TextRange? Remove(ComposeText read, string text, ComposeReference reference)
    {
        if (reference.Flow is { } flow)
        {
            IReadOnlyList<FlowItem> items = FlowItems(text, reference.Entry) ?? [];
            int at = items.ToList().FindIndex(i => i.At == flow.At);
            if (at < 0)
            {
                return null;
            }
            if (items.Count == 1)
            {
                return Lines(read, text, reference.Entry);
            }
            // The first takes the comma after it and the space before the next; any other the comma and space before it.
            return at == 0 ? TextRange.FromBounds(items[0].Between.Start, items[1].At.Start - (Quoted(text, items[1]) ? 1 : 0))
                : TextRange.FromBounds(items[at - 1].At.End + (Quoted(text, items[at - 1]) ? 1 : 0), flow.Between.End);
        }
        IReadOnlyList<ComposeEntry> siblings = [.. Children(read, reference.Entry.Path.Take(reference.Entry.Path.Count - 1).ToList())];
        return siblings.Count == 1 && read.At([.. reference.Entry.Path.Take(reference.Entry.Path.Count - 1)]) is { } holder
            ? Lines(read, text, holder) : Lines(read, text, reference.Entry);
    }

    private static bool Quoted(string text, FlowItem item) => item.At.Start > 0 && text[item.At.Start - 1] is '"' or '\'';

    /// <summary>An entry's whole lines with the lines nested under it, their line breaks included.</summary>
    private static TextRange Lines(ComposeText read, string text, ComposeEntry entry)
    {
        int last = read.Entries.Where(e => e.Path.Count >= entry.Path.Count && e.Path.Take(entry.Path.Count).SequenceEqual(entry.Path, StringComparer.Ordinal))
            .Max(e => Math.Max(e.KeyEnd, e.ValueEnd));
        return TextRange.FromBounds(LineStart(text, entry.KeyStart), AfterLine(text, last));
    }

    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>Where the line holding an offset ends, past its line break; the text's end on its last line.</summary>
    private static int AfterLine(string text, int offset) => text.IndexOf('\n', Math.Min(offset, text.Length)) is int at and >= 0 ? at + 1 : text.Length;

    /// <summary>Declare at the top level: the name under its top-level key (after its last entry, or the key made at the
    /// file's end), as the file indents; a secret or config read from a file of its name beside it. Null where the key's
    /// value is on its line (a flow mapping), but for an empty one.</summary>
    private static EditorFix? Declare(ComposeText read, string text, ComposeFault fault)
    {
        string unit = new(' ', Indent(read, text));
        string br = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string file = $"./{fault.Name}.txt";
        string body = fault.Kind is "secret" or "config" ? $"{br}{unit}{unit}file: {file}" : string.Empty;
        string title = $"Declare {fault.Name} at the top level";
        string detail = fault.Kind is "secret" or "config" ? $"under {fault.Key}, read from {file}" : $"under {fault.Key}";
        if (read.At([fault.Key]) is not { } top)
        {
            string lead = text.Length == 0 || text.EndsWith('\n') ? string.Empty : br;
            string made = $"{lead}{fault.Key}:{br}{unit}{fault.Name}:{body}{br}";
            return new EditorFix(title, detail, new TextRange(text.Length, 0), made) { Caret = made.Length - br.Length };
        }
        int lineEnd = text.IndexOf('\n', top.KeyEnd) is int next and >= 0 ? next : text.Length;
        string after = text[(top.KeyEnd + 1)..lineEnd].TrimEnd('\r');
        int comment = after.IndexOf(" #", StringComparison.Ordinal);
        string value = (comment >= 0 ? after[..comment] : after).Trim();
        if (value == "{}")
        {
            // An empty flow mapping becomes the block the name goes in.
            int brace = text.IndexOf("{}", top.KeyEnd, StringComparison.Ordinal);
            string block = $"{br}{unit}{fault.Name}:{body}";
            return new EditorFix(title, detail, TextRange.FromBounds(top.KeyEnd + 1, brace + 2), block) { Caret = block.Length };
        }
        if (value.Length > 0 && !value.StartsWith('&'))
        {
            return null;
        }
        IReadOnlyList<ComposeEntry> declared = [.. Children(read, [fault.Key])];
        string indent = declared.Count > 0 ? new string(' ', declared[0].KeyStart - LineStart(text, declared[0].KeyStart)) : unit;
        int at = declared.Count > 0 ? Lines(read, text, top).End : AfterLine(text, top.KeyEnd);
        string before = at == text.Length && text.Length > 0 && !text.EndsWith('\n') ? br : string.Empty;
        string entry = $"{before}{indent}{fault.Name}:{body}";
        return new EditorFix(title, detail, new TextRange(at, 0), entry + br) { Caret = entry.Length };
    }

    /// <summary>The file's indentation step: how far its first nested key stands in, two spaces when it has none.</summary>
    private static int Indent(ComposeText read, string text) =>
        read.Entries.FirstOrDefault(e => e.Path.Count == 2) is { } nested && nested.KeyStart - LineStart(text, nested.KeyStart) is int column and > 0 ? column : 2;

    /// <summary>The names the file declares of the fault's kind that are near its name, letter case aside (an edit or two
    /// away, one for a short name), nearest first, three at most: never the service itself.</summary>
    private static IEnumerable<string> Near(ComposeText read, ComposeFault fault) =>
        Spelling.Near(fault.Name, Children(read, [fault.Key]).Select(e => e.Path[^1]).Where(name => !(fault.Kind == "service" && name == fault.Service)));

    [GeneratedRegex(@"^service ""(?<service>[^""]+)"" refers to undefined (?<kind>volume|network|secret|config) (?<name>[^\s:]+)")]
    private static partial Regex Undefined();

    [GeneratedRegex(@"^service ""(?<service>[^""]+)"" depends on undefined service ""(?<name>[^""]+)""")]
    private static partial Regex DependsOn();
}
