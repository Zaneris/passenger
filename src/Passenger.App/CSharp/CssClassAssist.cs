using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The classes a page's stylesheets define, where a Razor or HTML file writes a class (a class attribute, a
/// component's Class or CssClass, an explicit expression's strings: <see cref="MarkupTags.ClassAt"/>): completion lists
/// them ahead of the file's server's own items, in one list, each naming its stylesheet; the hover card shows a class's
/// rules; Go to Declaration goes to each rule (<see cref="PageStylesheets"/>). Anywhere else, and for a class no stylesheet
/// defines, it passes to the next on the ladder.</summary>
internal sealed class CssClassAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation) : LanguageAssist
{
    /// <summary>How far before and after a place the text is read for the tag it is in.</summary>
    private const int Reach = 16 * 1024;

    /// <summary>The most rules the hover card shows.</summary>
    private const int MostShown = 4;

    /// <summary>The most declarations of a class's rule the card beside the completion list shows, and the most characters
    /// of each, so it fits beside the list.</summary>
    private const int ItemDeclarations = 8;

    private const int ItemWidth = 40;

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || Razor(shown) is not { } razor || context.Current?.Lsp is not { } lsp
            || context.Stylesheets?.Invoke() is not { } stylesheets || ClassAt(shown.Text.Snapshot, context.Caret, razor) is null)
        {
            return false;
        }
        await completion.ServerListAsync(lsp, trigger, incomplete, context.WebOf(shown), async (asked, caret, cancel) =>
        {
            if (ClassAt(asked, caret, razor) is not ({ } start, _))
            {
                return null;
            }
            PageClasses classes = await stylesheets.ClassesAsync(shown.Path, cancel);
            string path = shown.Path;
            // A framework's stylesheet defines thousands of classes, each described: made off the window's thread.
            List<CompletionItem> items = await BackgroundWork.Run(() => Items(path, classes), cancel);
            return items.Count == 0 ? null : new CodeCompletion.OwnItems(start, items, null) { WordCharacters = "-" };
        });
        return true;
    }

    /// <summary>Each class completion offers, named by its stylesheet, its first rule on the card beside the list.</summary>
    private static List<CompletionItem> Items(string markup, PageClasses classes) =>
        [.. classes.Classes.Values
            .Select(defined => defined.FirstOrDefault(d => d.Offered))
            .OfType<DefinedClass>()
            .Select(d => new CompletionItem
            {
                Label = d.Name,
                Kind = CompletionItemKind.Value,
                Detail = d.Sheet,
                Documentation = System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "markdown", value = $"```css\n{d.Text.Describe(d.Rule, ItemDeclarations, ItemWidth)}\n```" }),
                // A class the file's own scoped stylesheet gives comes before the pages'.
                SortText = (ScopedOwn(markup, d.Path) ? "!0" : "!1") + d.Name,
            })];

    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (await DefinedAtAsync(document, offset, cancel) is not ({ Count: > 0 } defined, _))
        {
            return false;
        }
        cancel.ThrowIfCancellationRequested();
        foreach (IGrouping<string, DefinedClass> sheet in defined.GroupBy(d => d.Path, PathNames.Comparer))
        {
            List<DefinedClass> rules = [.. sheet];
            card.AddText(rules.Count == 1 ? $"{rules[0].Sheet}" : $"{rules[0].Sheet}: {Plural.Of(rules.Count, "rule")}");
            card.AddCode(string.Join("\n\n", rules.Take(MostShown).Select(d => d.Text.Describe(d.Rule))));
            if (rules.Count > MostShown)
            {
                card.AddText($"and {Plural.Of(rules.Count - MostShown, "more rule")}");
            }
        }
        return true;
    }

    public override Task<bool> DefinitionAsync(int offset) => GoToDeclarationAsync(context, navigation, offset);

    /// <summary>Each rule that names the class at an offset, in its stylesheet; the words for there being none, a stylesheet
    /// that could not be read named, where the class is written but none defines it.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (await DefinedAtAsync(document, offset, CancellationToken.None) is not ({ } defined, var said))
        {
            return null;
        }
        List<(string Path, TextPosition Position)> places = [];
        List<string?> names = [];
        foreach (IGrouping<string, DefinedClass> sheet in defined.GroupBy(d => d.Path, PathNames.Comparer))
        {
            TextSnapshot text = TextSnapshot.From(sheet.First().Text.Text);
            foreach (DefinedClass rule in sheet)
            {
                int line = text.GetLineFromOffset(rule.Rule.At);
                places.Add((rule.Path, new TextPosition(line, rule.Rule.At - text.GetLineStart(line))));
                names.Add($"{Selector(rule)} in {rule.Sheet}");
            }
        }
        return new Declared(Declared.Served(places, ServerPlaces.Texts(context.Editors)), said ?? string.Empty) { Names = names, Title = "Rules" };
    }

    /// <summary>The rules that name the class written at an offset, and the words for there being none; null where no class is written.</summary>
    private async Task<(IReadOnlyList<DefinedClass>? Defined, string? None)> DefinedAtAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        if (Razor(document) is not { } razor || context.Stylesheets?.Invoke() is not { } stylesheets
            || ClassAt(document.Text.Snapshot, offset, razor) is not ({ } start, { } end) || end == start)
        {
            return (null, null);
        }
        string name = document.Text.Snapshot.GetText(start, end - start);
        PageClasses classes = await stylesheets.ClassesAsync(document.Path, cancel);
        if (classes.Classes.TryGetValue(name, out IReadOnlyList<DefinedClass>? defined))
        {
            return (defined, null);
        }
        string none = $"No stylesheet the page links defines the class {name}";
        return ([], classes.Unread.Count > 0 ? $"{none}; {classes.Unread[0]}" : none);
    }

    /// <summary>A rule's selector as written, its whitespace one space.</summary>
    private static string Selector(DefinedClass rule) =>
        string.Join(' ', rule.Text.Text.AsSpan(rule.Rule.RuleStart, rule.Rule.BodyStart - rule.Rule.RuleStart - 1).ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether a stylesheet is a markup file's own scoped one.</summary>
    private static bool ScopedOwn(string markup, string sheet) => PathNames.Same(markup + ".css", sheet);

    /// <summary>Whether a file writes its classes as Razor does, as HTML does, or is neither (null).</summary>
    private static bool? Razor(OpenDocument document) =>
        RazorFiles.Is(document.Path) ? true
        : document.Language == Languages.Language.Html ? false
        : null;

    /// <summary>Where the class written at an offset begins and ends, read from the text near it.</summary>
    internal static (int Start, int End)? ClassAt(TextSnapshot text, int offset, bool razor)
    {
        int from = Math.Max(0, offset - Reach);
        int to = Math.Min(text.Length, offset + Reach / 4);
        return MarkupTags.ClassAt(text.GetText(from, to - from), offset - from, razor) is ({ } start, { } end) ? (start + from, end + from) : null;
    }
}
