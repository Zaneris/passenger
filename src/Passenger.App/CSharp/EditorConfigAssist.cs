using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>An .editorconfig as its readers take it (<see cref="EditorConfigKnown"/>): completion lists a section's globs by
/// the folder's files, a section's keys (editorconfig.org's, Passenger's, Roslyn's options and a severity for each
/// diagnostic the projects' analyzers report) and a key's values; the hover card says what a key does, its values and
/// default, and what files a section's glob matches; Go to Declaration from a glob lists those files, and from a
/// diagnostic's key opens its page.</summary>
internal sealed class EditorConfigAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation) : LanguageAssist
{
    /// <summary>The most files a section's card names.</summary>
    private const int MostNamed = 8;

    /// <summary>The characters beyond a name's that narrow a list rather than close it, by where it is.</summary>
    private const string KeyCharacters = "._-";
    private const string GlobCharacters = "*.{},/-_!?";
    private const string ValueCharacters = "-_.";

    /// <summary>The open folder and its files by their paths from it, as the index's walk lists them.</summary>
    public Func<(string Folder, IReadOnlyList<string> Files)?>? Indexed { get; set; }

    /// <summary>The words each item's card says, by the item.</summary>
    private readonly Dictionary<CompletionItem, IReadOnlyList<string>> described = [];

    /// <summary>Where the list now open puts its item, and what of the text it replaces.</summary>
    private Spot? listed;

    private static bool IsEditorConfig(OpenDocument? document) => document?.Language == Language.EditorConfig;

    public override bool Typed(string text)
    {
        if (!IsEditorConfig(context.Editors.Active))
        {
            return false;
        }
        int caret = context.Caret;
        TextSnapshot snapshot = context.Editor.Document.Snapshot;
        Spot spot = Spot.At(snapshot.ToString(), caret, null);
        char last = text[^1];
        bool opens = spot.Kind switch
        {
            SpotKind.Glob => last == '[' || spot.Start == caret - 1,
            SpotKind.Key => spot.Start == caret - 1 && !char.IsWhiteSpace(last),
            SpotKind.Value => last is ':' or ',' || last == ' ' && caret >= 2 && snapshot[caret - 2] == '=' || spot.Start == caret - 1 && !char.IsWhiteSpace(last),
            _ => false,
        };
        if (opens)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !IsEditorConfig(shown))
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        int caret = context.Caret;
        EditorConfigKnown known = await KnownAsync(shown);
        string text = asked.ToString();
        Spot spot = Spot.At(text, caret, known);
        IReadOnlyList<(CompletionItem Item, IReadOnlyList<string> Said)> items = spot.Kind switch
        {
            SpotKind.Glob when Indexed?.Invoke() is { } indexed => await BackgroundWork.Run(() => GlobItems(shown.Path, indexed)),
            SpotKind.Key => KeyItems(known, spot.Preamble),
            SpotKind.Value => ValueItems(known, spot),
            _ => [],
        };
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
        foreach ((CompletionItem item, IReadOnlyList<string> said) in items)
        {
            described[item] = said;
        }
        listed = spot;
        completion.Open(new CodeCompletion.Completion([.. items.Select(i => i.Item)], spot.Start, [], false)
        {
            Own = this,
            WordCharacters = spot.Kind switch { SpotKind.Glob => GlobCharacters, SpotKind.Key => KeyCharacters, _ => ValueCharacters },
        });
        return true;
    }

    /// <summary>The item in place of the glob, key or value it completes, as far as that runs; a key with no value yet
    /// followed by <c> = </c> and the list of its values.</summary>
    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        Spot spot = Spot.At(text.ToString(), context.Caret, null);
        int end = spot.Kind != SpotKind.None && spot.Start == start ? spot.End : context.Caret;
        string written = item.InsertText ?? item.Label;
        bool valued = spot.Kind == SpotKind.Key && !spot.Valued;
        if (valued)
        {
            written += " = ";
        }
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), written)], SelectionSet.Single(start + written.Length));
        if (valued)
        {
            IdeErrors.Observe(completion.Start(null));
        }
    }

    public override bool Describe(CompletionItem item, IHoverCard card)
    {
        if (!described.TryGetValue(item, out IReadOnlyList<string>? said) || said.Count == 0)
        {
            return false;
        }
        card.AddText(string.Join('\n', said));
        return true;
    }

    /// <summary>A key's or a value's card (<see cref="EditorConfigKnown.Said"/>), or what files a section's glob matches.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!IsEditorConfig(document))
        {
            return false;
        }
        string text = document.Text.Snapshot.ToString();
        Spot spot = Spot.At(text, offset, null);
        if (spot.Kind == SpotKind.Glob && EditorConfig.Lines(text).SectionAt(offset) is { } section && section.GlobAt.Start <= offset && offset <= section.GlobAt.End)
        {
            (string, IReadOnlyList<string>)? indexed = Indexed?.Invoke();
            IReadOnlyList<string> files = await BackgroundWork.Run(() => Matched(document.Path, section.Glob, indexed));
            cancel.ThrowIfCancellationRequested();
            card.AddCode($"[{section.Glob}]", Language.EditorConfig);
            card.AddText(MatchedWords(section.Glob, files));
            return true;
        }
        if (spot.Kind is not (SpotKind.Key or SpotKind.Value) || spot.Key.Length == 0)
        {
            return true;
        }
        EditorConfigKnown known = await KnownAsync(document);
        cancel.ThrowIfCancellationRequested();
        card.AddCode(spot.Key, Language.EditorConfig);
        card.AddText(known.Said(spot.Key) is { } said ? string.Join('\n', said)
            : known.Server is null && EditorConfigKnown.IsDotnet(spot.Key) ? "Roslyn's server has not said what it reads yet" : "Nothing Passenger knows of reads this key");
        return true;
    }

    public override Task<bool> DefinitionAsync(int offset) => GoToDeclarationAsync(context, navigation, offset, IsEditorConfig);

    /// <summary>From a section's glob, the files it matches under the .editorconfig's folder; from a diagnostic's key, the
    /// diagnostic's page; null elsewhere.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (!IsEditorConfig(document))
        {
            return null;
        }
        string text = document.Text.Snapshot.ToString();
        if (EditorConfig.Lines(text).SectionAt(offset) is { } section && section.GlobAt.Start <= offset && offset <= section.GlobAt.End)
        {
            string folder = Path.GetDirectoryName(document.Path)!;
            (string, IReadOnlyList<string>)? indexed = Indexed?.Invoke();
            IReadOnlyList<string> files = await BackgroundWork.Run(() => Matched(document.Path, section.Glob, indexed));
            return new Declared([.. files.Select(f => new LspLocation(DocumentUris.FromPath(Path.Join(folder, f)), Start))], $"[{section.Glob}] matches no file under its folder")
            {
                Names = [.. files.Select(f => (string?)f)],
                Title = $"Files [{section.Glob}] matches",
            };
        }
        if (Spot.At(text, offset, null) is { Kind: SpotKind.Key or SpotKind.Value } spot && EditorConfigKnown.IdOf(spot.Key) is { } id)
        {
            EditorConfigKnown known = await KnownAsync(document);
            return known.DiagnosticOf(spot.Key) is { Help: { } help } && Uri.TryCreate(help, UriKind.Absolute, out Uri? page)
                ? new Declared([], $"{id}'s page") { Page = page }
                : new Declared([], $"No analyzer the projects run gives a page of {id}");
        }
        return null;
    }

    /// <summary>A file's first place.</summary>
    private static readonly LspRange Start = new(new LspPosition(0, 0), new LspPosition(0, 0));

    /// <summary>What the folder's readers take of an .editorconfig, with what Roslyn's server read of it where it answers.</summary>
    internal async Task<EditorConfigKnown> KnownAsync(OpenDocument document) =>
        EditorConfigKnown.For(context.CSharp is { } csharp ? await csharp.EditorConfigKeysAsync(document.Path) : null);

    /// <summary>The files under an .editorconfig's folder a section's glob matches, by their paths from it, of those the
    /// index lists. A worker's.</summary>
    private static IReadOnlyList<string> Matched(string config, string glob, (string Folder, IReadOnlyList<string> Files)? indexed) =>
        [.. Under(Path.GetDirectoryName(config)!, indexed).Where(f => EditorConfig.Matches(glob, "/" + f)).Order(StringComparer.Ordinal)];

    /// <summary>The index's files under a folder, by their paths from it in forward slashes.</summary>
    private static IEnumerable<string> Under(string folder, (string Folder, IReadOnlyList<string> Files)? indexed)
    {
        if (indexed is not ({ } root, { } files))
        {
            return [];
        }
        return files.Select(f => Path.Join(root, f)).Where(f => PathNames.IsUnder(f, folder)).Select(f => PathNames.Relative(f, folder)).OfType<string>();
    }

    private static string MatchedWords(string glob, IReadOnlyList<string> files) =>
        files.Count == 0 ? "Matches no file under this .editorconfig's folder"
            : $"Matches {Plural.Of(files.Count, "file")} under this .editorconfig's folder: "
                + Plural.Listed([.. files.Take(MostNamed), .. files.Count > MostNamed ? [$"{files.Count - MostNamed} more"] : Array.Empty<string>()]);

    /// <summary>The globs a section may take: every file, each extension among the files under the folder (the most
    /// written first), and each folder at the top. A worker's.</summary>
    private static List<(CompletionItem, IReadOnlyList<string>)> GlobItems(string config, (string Folder, IReadOnlyList<string> Files) indexed)
    {
        List<string> files = [.. Under(Path.GetDirectoryName(config)!, indexed)];
        List<(CompletionItem, IReadOnlyList<string>)> items = [Glob("*", "every file", 0, files.Count)];
        foreach (IGrouping<string, string> kind in files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).Where(g => g.Key.Length > 1).OrderByDescending(g => g.Count()))
        {
            items.Add(Glob("*" + kind.Key, $"{kind.Key} files", items.Count, kind.Count()));
        }
        foreach (IGrouping<string, string> top in files.Where(f => f.Contains('/', StringComparison.Ordinal)).GroupBy(f => f[..f.IndexOf('/', StringComparison.Ordinal)]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            items.Add(Glob(top.Key + "/**", $"files under {top.Key}/", items.Count, top.Count()));
        }
        return items;

        static (CompletionItem, IReadOnlyList<string>) Glob(string glob, string what, int rank, int count) =>
            (new CompletionItem { Label = glob, Kind = CompletionItemKind.File, Detail = Plural.Of(count, "file"), SortText = $"{rank:D5}" }, [$"[{glob}]: {what}, {Plural.Of(count, "file")} here now"]);
    }

    /// <summary>The keys a section takes, each with its card; before the first section, only <c>root</c>.</summary>
    private static List<(CompletionItem, IReadOnlyList<string>)> KeyItems(EditorConfigKnown known, bool preamble)
    {
        List<(CompletionItem, IReadOnlyList<string>)> items = [];
        foreach (KnownKey key in known.Keys.Where(k => preamble == (k.Name == "root")))
        {
            int rank = key.From switch { "EditorConfig" => 0, "Passenger" => 1, _ => 2 };
            items.Add((Key(key.Name, key.Said[0].Split(" › ")[^1], rank), known.Said(key.Name) ?? []));
        }
        if (preamble)
        {
            return items;
        }
        items.Add((Key(EditorConfigKnown.AnalyzerDiagnostic + "severity", "every analyzer's diagnostics", 3), known.Said(EditorConfigKnown.AnalyzerDiagnostic + "severity") ?? []));
        foreach (string category in known.Categories)
        {
            string name = $"{EditorConfigKnown.AnalyzerDiagnostic}{EditorConfigKnown.Category}{category}{EditorConfigKnown.SeveritySuffix}";
            items.Add((Key(name, $"{category} diagnostics", 3), known.Said(name) ?? []));
        }
        foreach (EditorConfigDiagnostic diagnostic in known.Diagnostics)
        {
            string name = $"{EditorConfigKnown.Diagnostic}{diagnostic.Id}{EditorConfigKnown.SeveritySuffix}";
            items.Add((Key(name, diagnostic.Title, 4), known.Said(name) ?? []));
        }
        return items;

        static CompletionItem Key(string name, string detail, int rank) =>
            new() { Label = name, Kind = CompletionItemKind.Property, Detail = detail, SortText = $"{rank}{name}" };
    }

    /// <summary>The values a key takes where it is typed: its own, a severity after a code style value's colon, or a
    /// diagnostic's severity.</summary>
    private static List<(CompletionItem, IReadOnlyList<string>)> ValueItems(EditorConfigKnown known, Spot spot)
    {
        IReadOnlyList<string> values;
        string? fallback = null;
        if (spot.AfterColon)
        {
            values = known.Severities;
        }
        else if (EditorConfigKnown.IdOf(spot.Key) is not null || EditorConfigKnown.CategoryOf(spot.Key) is not null)
        {
            values = known.DiagnosticSeverities;
        }
        else if (known.KeyOf(spot.Key) is { } key)
        {
            values = key.Values;
            fallback = key.Default?.Split(':')[0];
        }
        else
        {
            values = [];
        }
        return [.. values.Select((v, i) => ((new CompletionItem
        {
            Label = v,
            Kind = CompletionItemKind.EnumMember,
            Detail = string.Equals(v, fallback, StringComparison.OrdinalIgnoreCase) ? "default" : null,
            SortText = $"{i:D3}",
        }), (IReadOnlyList<string>)[]))];
    }

    /// <summary>Where on an .editorconfig's line an offset is.</summary>
    internal enum SpotKind
    {
        None,
        Glob,
        Key,
        Value,
    }

    /// <summary>Where an offset of an .editorconfig is: a section's glob, a key or a value (its part after a severity's
    /// colon or a list's comma), with the stretch an item there replaces, the line's key, whether the line has a value
    /// yet, and whether it is before the first section.</summary>
    internal sealed record Spot(SpotKind Kind, int Start, int End, string Key, bool Valued, bool Preamble, bool AfterColon)
    {
        private static readonly Spot Nowhere = new(SpotKind.None, 0, 0, string.Empty, false, false, false);

        /// <summary>The spot of an offset of an .editorconfig's text; a value's severity is told only where the key takes
        /// one by <paramref name="known"/>.</summary>
        public static Spot At(string text, int offset, EditorConfigKnown? known)
        {
            offset = Math.Clamp(offset, 0, text.Length);
            int start = TextNavigation.LineStart(text, offset);
            int end = TextNavigation.LineEnd(text, offset);
            int first = start;
            while (first < end && char.IsWhiteSpace(text[first]))
            {
                first++;
            }
            if (first < end && text[first] is '#' or ';' && offset > first)
            {
                return Nowhere;
            }
            int last = end;
            while (last > first && char.IsWhiteSpace(text[last - 1]))
            {
                last--;
            }
            bool preamble = !HeaderBefore(text, start);
            if (first < end && text[first] == '[')
            {
                int close = text.IndexOf(']', first, end - first);
                return offset <= first ? Nowhere : new Spot(SpotKind.Glob, first + 1, close < 0 ? last : close, string.Empty, false, false, false);
            }
            int equals = first < end ? text.IndexOf('=', first, end - first) : -1;
            if (equals < 0 || offset <= equals)
            {
                int keyEnd = equals < 0 ? last : equals;
                while (keyEnd > first && char.IsWhiteSpace(text[keyEnd - 1]))
                {
                    keyEnd--;
                }
                return new Spot(SpotKind.Key, Math.Min(first, offset), Math.Max(keyEnd, offset), text[first..keyEnd].ToLowerInvariant(), equals >= 0, preamble, false);
            }
            string key = text[first..equals].Trim().ToLowerInvariant();
            int valueStart = equals + 1;
            while (valueStart < offset && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }
            KnownKey? taken = known?.KeyOf(key);
            int colon = taken is { Severity: true } ? text.IndexOf(':', valueStart, offset - valueStart) : -1;
            if (colon >= 0)
            {
                return new Spot(SpotKind.Value, Skip(text, colon + 1, offset), Math.Max(last, offset), key, true, preamble, true);
            }
            int comma = taken is { Kind: EditorConfigValueKinds.List } ? text.LastIndexOf(',', offset - 1, offset - valueStart) : -1;
            if (comma >= 0)
            {
                int next = text.IndexOf(',', offset, end - offset);
                return new Spot(SpotKind.Value, Skip(text, comma + 1, offset), next < 0 ? Math.Max(last, offset) : next, key, true, preamble, false);
            }
            int valueEnd = taken is { Kind: EditorConfigValueKinds.List } && text.IndexOf(',', offset, end - offset) is int following and >= 0 ? following
                : taken is { Severity: true } && text.IndexOf(':', offset, end - offset) is int severity and >= 0 ? severity : Math.Max(last, offset);
            return new Spot(SpotKind.Value, valueStart, valueEnd, key, true, preamble, false);
        }

        private static int Skip(string text, int at, int limit)
        {
            while (at < limit && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            return at;
        }

        /// <summary>Whether a section's header stands on a line before an offset (a line's start).</summary>
        private static bool HeaderBefore(string text, int before)
        {
            for (int at = 0; at < before; at = TextNavigation.NextLineStart(text, at))
            {
                int first = at;
                while (first < before && text[first] is ' ' or '\t')
                {
                    first++;
                }
                if (first < before && text[first] == '[')
                {
                    return true;
                }
            }
            return false;
        }
    }
}
