using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Localization;

namespace Passenger.App.CSharp;

/// <summary>Localized strings known from the code that reads them (an <c>IStringLocalizer&lt;T&gt;</c>'s or
/// <c>IHtmlLocalizer&lt;T&gt;</c>'s keys, a generated resource class's properties, in C# and in Razor) and the .resx and XLIFF
/// files that give them (<see cref="ResourceFiles"/>): a key's hover card says each language's value, Go to Declaration goes
/// to each file's entry, completion in a localizer's string offers the resource's keys, and a key a language lacks is
/// warned of (<see cref="CSharpDocument.LocalizationFindingsAsync"/>) with Passenger's own fix, which asks the value and adds it.
/// XAML's <c>{x:Static p:Resources.Key}</c> of a generated class (<see cref="XamlLocalization"/>) has the same card and entries
/// (<see cref="XamlAssist"/>) and fix.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>The key read at an offset of a C# or Razor file, with its resource and that resource's files; null elsewhere,
    /// a C# file asking nothing where the offset is in neither a string nor a name.</summary>
    private async Task<(LocalizedKey Read, LocalizedResource Resource, ResourceSet? Set)?> LocalizedAtAsync(LspDocument lsp, OpenDocument document, int offset,
        CancellationToken cancel = default)
    {
        if (!lsp.IsRazor && (context.Syntax is not { } parsed || !ReferenceEquals(parsed.Text, lsp.Text.Snapshot)
            || !new[] { parsed.Root.FindToken(Math.Max(0, offset - 1)), parsed.Root.FindToken(offset) }.Any(t => t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.IdentifierToken))))
        {
            return null;
        }
        TextSnapshot asked = lsp.Text.Snapshot;
        if (context.CSharp is not { } csharp || await csharp.LocalizedAsync(document.Path, cancel) is not { } localized || lsp.Text.Snapshot != asked)
        {
            return null;
        }
        foreach (LocalizedKey read in localized.Keys)
        {
            if (lsp.OffsetOf(new LspPosition(read.At.Line, read.At.Character)) <= offset && offset <= lsp.OffsetOf(new LspPosition(read.End.Line, read.End.Character)))
            {
                return (read, localized.Resources[read.Resource], localized.Sets[read.Resource]);
            }
        }
        return null;
    }

    /// <summary>What the hover card says of a localized key: each language's value (the neutral file's first), or that it has
    /// none, is not translated yet or wants review; that no file gives the resource where none is found.</summary>
    private async Task<string?> LocalizationHoverAsync(int offset, CancellationToken cancel)
    {
        if (context.Editors.Active is not { } document || context.WebCurrent is not null || context.CSharp?.For(document)?.Lsp is not { } lsp
            || await LocalizedAtAsync(lsp, document, offset, cancel) is not ({ } read, { } resource, var set))
        {
            return null;
        }
        if (set is null)
        {
            return $"'{read.Key}' ({resource.Type}): no .resx gives {resource.Name}";
        }
        return LocalizationCard(read.Key, resource.Type, set);
    }

    /// <summary>A localized key's card: the key and its type (with the files' name where it is not the type's: a resource under
    /// another name, a Designer class's .resx), then each language's value; C#'s and XAML's alike.</summary>
    internal static string LocalizationCard(string key, string type, ResourceSet set) =>
        string.Join('\n', [$"'{key}' ({(set.Name == type ? type : $"{type}, {set.Name}")}):", .. set.Languages(key)]);

    /// <summary>Where Go to Declaration goes from a localized key: each file's entry of it (a .resx's <c>&lt;data name&gt;</c>,
    /// an XLIFF unit's id), named by its language, at the editor's lines, and for a generated class's property whether the
    /// server's own declaration of it goes first; null where the offset is on no key. Shared with the agents' definition.</summary>
    internal async Task<(IReadOnlyList<(string Path, TextPosition Position, LspPosition Served, string Label)> Places, bool Generated, string Key, string Type)?> LocalizedDeclarationsAsync(
        OpenDocument document, int offset)
    {
        if (context.CSharp?.For(document)?.Lsp is not { } lsp || await LocalizedAtAsync(lsp, document, offset) is not ({ } read, { } resource, var set))
        {
            return null;
        }
        IReadOnlyList<(string, TextPosition, LspPosition, string)> places = [];
        if (set is not null)
        {
            OpenTexts texts = context.Editors.Texts();
            places = await BackgroundWork.Run(() => (IReadOnlyList<(string, TextPosition, LspPosition, string)>)[.. set.Files.Select(f => (File: f, Entry: f.Entry(read.Key)))
                .Where(f => f.Entry is not null).Select(f =>
                {
                    TextSnapshot? text = texts.TextOf(f.File.Path);
                    int at = Math.Min(f.Entry!.NameStart, text?.Length ?? 0);
                    TextPosition position = text?.GetPosition(at) ?? new TextPosition(0, 0);
                    return (f.File.Path, position, text is null ? new LspPosition(0, 0) : ServerLines.Of(text).PositionOf(at),
                        $"{(f.File.Culture.Length == 0 ? "neutral" : f.File.Culture)}: {f.File.Name}");
                })]);
        }
        return (places, resource.Kind == LocalizedKinds.Class, read.Key, resource.Type);
    }

    /// <summary>Go to Declaration from a localized key: each language's entry of it, beside a generated class's property the
    /// server declares; false where the caret is on no key, for the server's own.</summary>
    private async Task<bool> LocalizationGoTo(LspDocument lsp, int offset)
    {
        if (context.Editors.Active is not { } document || await LocalizedDeclarationsAsync(document, offset) is not { } found)
        {
            return false;
        }
        List<LspLocation> places = [];
        List<string?> names = [];
        if (found.Generated)
        {
            foreach (LspLocation declared in await lsp.DefinitionAsync(offset))
            {
                places.Add(declared);
                names.Add($"{found.Type}.{found.Key}");
            }
        }
        foreach ((string path, _, LspPosition served, string label) in found.Places)
        {
            places.Add(new LspLocation(DocumentUris.FromPath(path), new LspRange(served, served)));
            names.Add(label);
        }
        if (context.Editors.Active == document)
        {
            navigation.Go(places, offset, "Declarations", $"No file of {found.Type} gives '{found.Key}'", names);
        }
        return true;
    }

    /// <summary>What completion offers in a localizer's string: every key the resource's files have, its neutral value (else a
    /// language's) beside it; with where the key starts and the rest of it after the caret, which it replaces. Null anywhere else.</summary>
    private async Task<(int Start, IReadOnlyList<CompletionItem> Items, string Tail)?> LocalizedCompletionAsync(LspDocument lsp, TextSnapshot asked, int caret,
        CancellationToken cancel)
    {
        // Only a string an indexer or GetString is given asks the extension, so a list typed anywhere else waits on nothing more.
        int line = asked.GetLineFromOffset(Math.Min(caret, asked.Length));
        int lineStart = asked.GetLineStart(line);
        if (!IndexerString.IsMatch(asked.GetText(lineStart, Math.Min(caret, asked.Length) - lineStart))
            || context.Editors.Active is not { } document || await LocalizedAtAsync(lsp, document, caret, cancel) is not ({ Exact: true } read, { Kind: LocalizedKinds.Localizer }, { } set)
            || lsp.Text.Snapshot != asked)
        {
            return null;
        }
        int start = lsp.OffsetOf(new LspPosition(read.At.Line, read.At.Character));
        int end = lsp.OffsetOf(new LspPosition(read.End.Line, read.End.Character));
        IReadOnlyList<CompletionItem> items = [.. set.Keys.Select(key => new CompletionItem
        {
            Label = key,
            Kind = CompletionItemKind.Value,
            Detail = (set.Neutral?.Entry(key) ?? set.Files.Select(f => f.Entry(key)).FirstOrDefault(e => e is { Translated: true }))?.Value ?? set.Name,
            InsertText = key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal),
            SortText = " " + key,
            FilterText = key,
        })];
        return items.Count == 0 ? null : (start, items, caret < end ? asked.GetText(caret, end - caret) : string.Empty);
    }

    /// <summary>A line up to the caret that ends inside a string given to an indexer or to GetString.</summary>
    private static readonly System.Text.RegularExpressions.Regex IndexerString = new(@"(\[|GetString\()\s*""[^""]*$");

    /// <summary>The fixes of a C#, Razor or XAML file's warnings, on an offset's line, that a language lacks a localized key
    /// (<see cref="LackingKeys"/>), Passenger's own and listed first: its value asked for and given in that language's file (a
    /// .resx's new entry, an XLIFF unit's target), one command of global undo, worked out again as it is chosen and refused
    /// once the file gives the key.</summary>
    public IReadOnlyList<EditorFix> LocalizationFixes(OpenDocument file, int offset) =>
        [.. LackingKeys(file, offset).OrderBy(m => m.Range.Start <= offset && offset <= m.Range.End ? 0 : 1)
            .DistinctBy(m => (m.Key, m.File)).Select(m => TranslationFix(file, m.Range, m.Key, m.File, m.Culture, m.Base, m.Untranslated))];

    /// <summary>The keys a file's warnings say a language lacks, on an offset's line (any line for null): a C# or Razor file's as
    /// its last pull placed them (<see cref="CSharpDocument.LocalizationMissingOn"/>), a XAML file's as its last check found them
    /// (<see cref="XamlDocument.Gaps"/>), each where it is read in the text as it stands.</summary>
    private IReadOnlyList<(TextRange Range, string Key, string File, string Culture, string Base, bool Untranslated)> LackingKeys(OpenDocument file, int? offset)
    {
        if (context.XamlOf(file) is { } xaml)
        {
            TextSnapshot text = file.Text.Snapshot;
            int? line = offset is { } at ? text.GetLineFromOffset(Math.Min(at, text.Length)) : null;
            return [.. xaml.Gaps.Where(g => line is not { } on || g.Range.Start <= text.Length && text.GetLineFromOffset(g.Range.Start) == on)
                .Select(g => (g.Range, g.Gap.Key, g.Gap.File.Path, g.Gap.Culture, g.Gap.BasePath, g.Gap.Untranslated))];
        }
        return context.CSharp?.For(file) is { Lsp: { } lsp } features ? features.LocalizationMissingOn(offset is { } served ? lsp.PositionOf(served).Line : null) : [];
    }

    private EditorFix TranslationFix(OpenDocument file, TextRange range, string key, string target, string culture, string basePath, bool untranslated)
    {
        string name = Path.GetFileName(target);
        return new(untranslated ? $"Translate '{key}' in {name}…" : $"Add '{key}' to {name}…", "a key a language lacks", range, string.Empty)
        {
            Again = _ => LackingKeys(file, null).FirstOrDefault(m => m.Key == key && m.File == target) is { } now
                ? TranslationFix(file, now.Range, key, target, culture, basePath, now.Untranslated) : null,
            Asks = new ValueAsked($"'{key}' in {ResourceFiles.LanguageOf(culture)}, kept in {name}", false, (value, _) => TranslateAsync(file, key, target, culture, basePath, value)),
        };
    }

    /// <summary>A key given its value in a language's file, as one command: a .resx's new entry written as its others are, an
    /// XLIFF unit's target filled in and made translated (or the unit XliffTasks would write, its source the neutral text);
    /// worked out off the UI thread from the file's text as the editor has it when open, and made through the editor or
    /// <see cref="FileText.Save"/>; refused once the file gives the key. The read's warning is pulled again.</summary>
    private async Task<RefactorOutcome> TranslateAsync(OpenDocument file, string key, string target, string culture, string basePath, string value)
    {
        string name = Path.GetFileName(target);
        bool xliff = target.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase);
        OpenTexts texts = context.Editors.Texts();
        (string? was, string? now, string? problem) = await BackgroundWork.Run(() =>
        {
            string text = texts.Text(target) ?? string.Empty;
            ResourceFile read = ResourceFiles.Parse(target, culture, xliff ? ResourceFormat.Xliff : ResourceFormat.Resx, text);
            if (read.Entry(key) is { Translated: true })
            {
                return (text, null, $"{name} gives '{key}' already");
            }
            if (!xliff)
            {
                return (text, ResourceFiles.WithEntry(text, key, value), $"{name} has no <root> to hold it");
            }
            ResourceEntry? neutral = ResourceFiles.Of(basePath).Neutral?.Entry(key);
            return (text, ResourceFiles.WithTarget(text, key, value, neutral?.Value ?? key, neutral?.Comment), $"{name} has no <body> to hold it");
        });
        if (now is null)
        {
            return RefactorOutcome.Refused($"'{key}' was not added: {problem}");
        }
        IReadOnlyList<TextEdit> Edits(string text)
        {
            string? made = text == was ? now : xliff ? ResourceFiles.WithTarget(text, key, value, key, null) : ResourceFiles.WithEntry(text, key, value);
            return made is null || made == text ? [] : [TextEdit.Between(text, made)];
        }
        string title = xliff ? $"Translate '{key}'" : $"Add '{key}'";
        if (actions.ApplyFileChanges(title, [new XamlFileChange(target, Edits)]) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        context.CSharp?.For(file)?.RefreshDiagnostics();
        context.XamlOf(file)?.Recheck();
        return new RefactorOutcome($"Added '{key}' to {name}", null, [new RefactoredFile(target, "changed", [])], []);
    }
}
