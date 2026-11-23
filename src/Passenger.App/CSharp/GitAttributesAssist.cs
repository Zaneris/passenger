using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.CSharp;

/// <summary>A .gitattributes as git reads it (<see cref="GitAttributeNames"/>): completion lists the attributes git and
/// GitHub's Linguist read, with the file's own macros, after a pattern, and an attribute's values after its <c>=</c> (a
/// diff, merge or filter driver git has built in or its configuration declares); the hover card says what an attribute
/// or a value does.</summary>
internal sealed class GitAttributesAssist(AssistContext context, CodeCompletion completion) : LanguageAssist
{
    /// <summary>The characters beyond a name's that narrow a list rather than close it.</summary>
    private const string NameCharacters = "-_.";

    /// <summary>The open folder's repository, whose configuration declares its drivers; null for none.</summary>
    public Func<Git?>? Repository { get; set; }

    /// <summary>The words each item's card says, by the item.</summary>
    private readonly Dictionary<CompletionItem, string> described = [];

    private static bool IsGitAttributes(OpenDocument? document) => document?.Language == Language.GitAttributes;

    public override bool Typed(string text)
    {
        if (!IsGitAttributes(context.Editors.Active))
        {
            return false;
        }
        int caret = context.Caret;
        TextSnapshot snapshot = context.Editor.Document.Snapshot;
        char last = text[^1];
        bool opens = Spot(snapshot.ToString(), caret) is { } spot && (last == '=' || spot.Start == caret - 1 && !char.IsWhiteSpace(last) && last is not ('-' or '!'));
        if (opens)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !IsGitAttributes(shown))
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        string text = asked.ToString();
        int caret = context.Caret;
        if (Spot(text, caret) is not { } spot)
        {
            completion.Close();
            return true;
        }
        List<(CompletionItem Item, string Said)> items = [];
        if (spot.Attribute is { } named)
        {
            IReadOnlySet<(string Kind, string Name)>? declared = Repository?.Invoke() is { } git ? await git.DriversAsync() : null;
            foreach ((string value, string said) in ValuesOf(named, declared))
            {
                items.Add((Item(value, CompletionItemKind.EnumMember, null, items.Count), said));
            }
        }
        else
        {
            foreach (GitAttribute attribute in GitAttributeNames.All)
            {
                items.Add((Item(attribute.Name, CompletionItemKind.Property, attribute.From, items.Count), attribute.Said));
            }
            foreach (string macro in MacrosIn(text))
            {
                items.Add((Item(macro, CompletionItemKind.Property, "this file's macro", items.Count), $"A macro this file defines ([attr]{macro})"));
            }
        }
        if (cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        if (items.Count == 0)
        {
            completion.Close();
            return true;
        }
        described.Clear();
        foreach ((CompletionItem item, string said) in items)
        {
            described[item] = said;
        }
        completion.Open(new CodeCompletion.Completion([.. items.Select(i => i.Item)], spot.Start, [], false) { Own = this, WordCharacters = NameCharacters });
        return true;

        static CompletionItem Item(string label, CompletionItemKind kind, string? detail, int rank) => new() { Label = label, Kind = kind, Detail = detail, SortText = $"{rank:D4}" };
    }

    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int end = Spot(text.ToString(), context.Caret) is { } spot && spot.Start == start ? spot.End : context.Caret;
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), item.Label)], SelectionSet.Single(start + item.Label.Length));
    }

    public override string? Describe(CompletionItem item) => described.GetValueOrDefault(item);

    /// <summary>What an attribute does and the values it takes, or what a value of it does.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!IsGitAttributes(document))
        {
            return false;
        }
        string text = document.Text.Snapshot.ToString();
        int start = TextNavigation.LineStart(text, offset);
        if (GitAttributeNames.Read(text, start, TextNavigation.LineEnd(text, offset)) is not { } line
            || line.Attributes.Cast<GitAttributeNames.Written?>().FirstOrDefault(a => a!.Value.Start <= offset && offset <= Math.Max(a.Value.End, a.Value.ValueEnd)) is not { } written
            || written.End <= written.Start)
        {
            return true;
        }
        string name = text[written.Start..written.End];
        card.AddCode(text[start..TextNavigation.LineEnd(text, offset)].Trim(), Language.GitAttributes);
        if (written.Valued && offset >= written.ValueStart)
        {
            string value = text[written.ValueStart..written.ValueEnd];
            IReadOnlySet<(string Kind, string Name)>? declared = Repository?.Invoke() is { } git ? await git.DriversAsync() : null;
            cancel.ThrowIfCancellationRequested();
            card.AddText(GitAttributeNames.Problem(name, value, declared)
                ?? ValuesOf(name, declared).FirstOrDefault(v => v.Value == value).Said ?? GitAttributeNames.Of(name)?.Said ?? "An attribute neither git nor Linguist reads");
            return true;
        }
        if (GitAttributeNames.Of(name) is { } attribute)
        {
            string said = attribute.Said + (attribute.Values.Count > 0 && attribute.Driver is null ? $"\nTakes {Plural.Either([.. attribute.Values.Select(v => v.Value)])}" : string.Empty)
                + $"\nRead by {attribute.From}" + (written.Mark switch { '-' => "\nUnset here (-)", '!' => "\nLeft unspecified here (!)", _ => string.Empty });
            card.AddText(said);
        }
        else
        {
            card.AddText(MacrosIn(text).Contains(name, StringComparer.Ordinal) ? $"A macro this file defines ([attr]{name})" : "An attribute neither git nor Linguist reads");
        }
        return true;
    }

    /// <summary>The values an attribute takes, with what each does: its own, and for a driver's attribute the drivers git's
    /// configuration declares.</summary>
    private static IEnumerable<(string Value, string Said)> ValuesOf(string name, IReadOnlySet<(string Kind, string Name)>? declared)
    {
        if (GitAttributeNames.Of(name) is not { } attribute)
        {
            return [];
        }
        IEnumerable<(string, string)> own = attribute.Values.Select(v => (v.Value, attribute.Driver is null ? v.Said : $"Git's own {attribute.Name} driver: {v.Said}"));
        return attribute.Driver is { } kind && declared is not null
            ? own.Concat(declared.Where(d => d.Kind == kind).Select(d => (d.Name, $"A {kind} driver git's configuration declares ([{kind} \"{d.Name}\"])")).Order())
            : own;
    }

    /// <summary>The macros a .gitattributes defines, by their <c>[attr]</c> lines.</summary>
    private static IEnumerable<string> MacrosIn(string text)
    {
        for (int at = 0; at < text.Length; at = TextNavigation.NextLineStart(text, at))
        {
            if (GitAttributeNames.Read(text, at, TextNavigation.LineEnd(text, at)) is { Macro: true } line && line.PatternEnd > line.PatternStart)
            {
                yield return text[line.PatternStart..line.PatternEnd];
            }
        }
    }

    /// <summary>Where an item put at an offset goes: an attribute's name (from after its <c>-</c> or <c>!</c>) or, after
    /// its <c>=</c>, its value, with the attribute's name; null on the pattern, a comment or a blank line.</summary>
    private static (int Start, int End, string? Attribute)? Spot(string text, int offset)
    {
        int lineEnd = TextNavigation.LineEnd(text, offset);
        if (GitAttributeNames.Read(text, TextNavigation.LineStart(text, offset), lineEnd) is not { } line || offset <= line.PatternEnd)
        {
            return null;
        }
        if (line.Attributes.Cast<GitAttributeNames.Written?>().FirstOrDefault(a => a!.Value.Start - (a.Value.Mark == '\0' ? 0 : 1) <= offset && offset <= Math.Max(a.Value.End, a.Value.ValueEnd)) is { } written)
        {
            return written.Valued && offset >= written.ValueStart
                ? (written.ValueStart, written.ValueEnd, text[written.Start..written.End])
                : (written.Start, written.End, null);
        }
        return char.IsWhiteSpace(text[offset - 1]) ? (offset, offset, null) : null;
    }
}
