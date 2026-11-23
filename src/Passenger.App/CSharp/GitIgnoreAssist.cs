using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.CSharp;

/// <summary>A .gitignore as git reads it, every answer git's own (<see cref="Git.IgnoreReadingAsync"/>): completion lists
/// the untracked folders and files under its folder git does not ignore yet, by their paths from it, and a pattern for
/// each kind of file among them (<c>*.log</c>); the hover card says what a line ignores, or keeps, in the working tree,
/// with how many, the tracked files it matches, which git never ignores, and the later line taking back what it
/// ignores.</summary>
internal sealed class GitIgnoreAssist(AssistContext context, CodeCompletion completion) : LanguageAssist
{
    /// <summary>The most paths a card names.</summary>
    private const int MostNamed = 8;

    /// <summary>The characters beyond a name's that narrow a list rather than close it: a path's.</summary>
    private const string PathCharacters = "/.-_*";

    /// <summary>The open folder's repository; null for none.</summary>
    public Func<Git?>? Repository { get; set; }

    /// <summary>The words each item's card says, by the item.</summary>
    private readonly Dictionary<CompletionItem, string> described = [];

    /// <summary>Whether a file is a .gitignore of the repository's, which git reads.</summary>
    internal static bool IsGitIgnore(string path) => Language.ForPath(path) == Language.Ignore && Path.GetFileName(path) == Git.IgnoreFileName;

    private bool Owns(OpenDocument? document) => document is not null && IsGitIgnore(document.Path);

    public override bool Typed(string text)
    {
        if (!Owns(context.Editors.Active))
        {
            return false;
        }
        int caret = context.Caret;
        if (Spot(context.Editor.Document.Snapshot.ToString(), caret) is { } spot && spot.Start == caret - 1 && !char.IsWhiteSpace(text[^1]))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !Owns(shown))
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        if (Spot(asked.ToString(), context.Caret) is not { } spot || Repository?.Invoke() is not { } git || await git.IgnoreReadingAsync(shown.Path) is not { } reading)
        {
            completion.Close();
            return true;
        }
        List<(CompletionItem Item, string Said)> items = [];
        foreach (string path in reading.Untracked.OrderBy(p => p.EndsWith('/') ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
        {
            bool folder = path.EndsWith('/');
            items.Add((Item(path, folder ? CompletionItemKind.Folder : CompletionItemKind.File, folder ? "untracked folder" : "untracked file", items.Count),
                $"{path}: untracked, which git does not ignore yet"));
        }
        foreach (IGrouping<string, string> kind in reading.Untracked.Where(p => !p.EndsWith('/')).GroupBy(p => Path.GetExtension(p).ToLowerInvariant())
            .Where(g => g.Key.Length > 1).OrderByDescending(g => g.Count()))
        {
            items.Add((Item("*" + kind.Key, CompletionItemKind.File, Plural.Of(kind.Count(), "untracked file"), items.Count),
                $"*{kind.Key}: every {kind.Key} file under this folder, {Plural.Of(kind.Count(), "untracked one")} here now"));
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
        completion.Open(new CodeCompletion.Completion([.. items.Select(i => i.Item)], spot.Start, [], false) { Own = this, WordCharacters = PathCharacters });
        return true;

        static CompletionItem Item(string label, CompletionItemKind kind, string detail, int rank) => new() { Label = label, Kind = kind, Detail = detail, SortText = $"{rank:D5}" };
    }

    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int end = Spot(text.ToString(), context.Caret) is { } spot && spot.Start == start ? spot.End : context.Caret;
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), item.Label)], SelectionSet.Single(start + item.Label.Length));
    }

    public override string? Describe(CompletionItem item) => described.GetValueOrDefault(item);

    /// <summary>What a line ignores or keeps in the working tree, as git answers, as the file stands on disk.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!Owns(document))
        {
            return false;
        }
        TextSnapshot text = document.Text.Snapshot;
        int line = text.GetPosition(Math.Min(offset, text.Length)).Line;
        string written = text.GetText(text.GetLine(line).Start, text.GetLine(line).Length).Trim();
        if (written.Length == 0 || written.StartsWith('#') || Repository?.Invoke() is not { } git)
        {
            return true;
        }
        IgnoreReading? reading = document.IsModified ? null : await git.IgnoreReadingAsync(document.Path);
        cancel.ThrowIfCancellationRequested();
        card.AddCode(written, Language.Ignore);
        card.AddText(reading is null ? "Git reads the file as saved: save it to see what this line ignores" : string.Join('\n', Said(written, reading.Of(line + 1))));
        return true;
    }

    /// <summary>What a line does in the working tree, as git answers, line by line of the card.</summary>
    internal static IReadOnlyList<string> Said(string written, IgnoreLineAnswer? answer)
    {
        bool keeps = written.StartsWith('!');
        List<string> said = [];
        if (answer is { Decides.Count: > 0 } decided)
        {
            said.Add($"{(keeps ? "Keeps" : "Ignores")} {Plural.Of(decided.Decides.Count, "path")} here: {Named(decided.Decides)}");
        }
        else if (answer?.UndoneBy is { } undone)
        {
            said.Add($"Ignores nothing: line {undone} takes back everything it ignores");
        }
        else
        {
            said.Add(keeps ? "Keeps nothing in the working tree" : "Ignores nothing in the working tree");
        }
        if (answer is { Tracked.Count: > 0 } tracked)
        {
            said.Add($"Matches {Plural.Of(tracked.Tracked.Count, "tracked file")}, which git never ignores: {Named(tracked.Tracked)}");
        }
        return said;

        static string Named(IReadOnlyList<string> paths) =>
            Plural.Listed([.. paths.Take(MostNamed), .. paths.Count > MostNamed ? [$"{paths.Count - MostNamed} more"] : Array.Empty<string>()]);
    }

    /// <summary>Where a path or pattern put at an offset goes: the line's pattern, after any <c>!</c> and leading <c>/</c>;
    /// null on a comment.</summary>
    private static (int Start, int End)? Spot(string text, int offset)
    {
        int start = TextNavigation.LineStart(text, offset);
        int end = TextNavigation.LineEnd(text, offset);
        while (start < end && text[start] is ' ' or '\t')
        {
            start++;
        }
        if (start < end && text[start] == '#')
        {
            return null;
        }
        while (start < offset && text[start] is '!' or '/')
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return offset < start ? null : (start, Math.Max(end, offset));
    }
}
