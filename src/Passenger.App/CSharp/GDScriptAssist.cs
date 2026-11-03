using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's answers from Passenger's own reading of it (<see cref="GodotScriptTypes"/>) while Godot's server
/// for it has not started: completion of the names in scope and of a value's members, the hover card's declaration and
/// type, Go to Declaration within the project's scripts, and a node's C# script's members (<see cref="GodotCSharpMembers"/>).
/// Once Godot's server runs, its answers are the file's, joined by those C# members, which its server never reads.</summary>
internal sealed class GDScriptAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation) : LanguageAssist
{
    private readonly Dictionary<CompletionItem, string> described = [];

    /// <summary>Whether a file is a GDScript whose own server cannot answer yet.</summary>
    private bool Answers(OpenDocument? document) =>
        document is not null && GodotScriptTypes.IsScript(document.Path) && context.GodotScriptTypes?.Invoke() is not null
        && context.WebOf(document) is not { Serving: true };

    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        char last = text[^1];
        if (last == '.' || AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(context.Caret) == context.Caret - 1)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        int caret = context.Caret;
        int start = context.WordStart(caret);
        GodotScriptTypes.Typed? typed = await types.TypedAsync(shown);
        if (cancel.IsCancellationRequested || typed is null || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        bool afterDot = start > 0 && asked[start - 1] == '.';
        List<GDScriptMemberInfo> members = afterDot ? MembersAfterDot(typed, start - 1) : InScope(typed, caret);
        IReadOnlyList<GodotCSharpMember> csharp = afterDot ? await CSharpMembersAsync(context, shown, typed, start - 1) : [];
        if (cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        if (members.Count == 0 && csharp.Count == 0)
        {
            completion.Close();
            return true;
        }
        described.Clear();
        List<CompletionItem> items = [];
        foreach (GodotCSharpMember member in csharp)
        {
            CompletionItem item = ItemOf(member);
            items.Add(item);
            described[item] = member.Shown;
        }
        foreach (GDScriptMemberInfo member in members.Where(m => csharp.All(c => c.Name != m.Name)))
        {
            CompletionItem item = new()
            {
                Label = member.Name,
                Kind = member.Kind switch
                {
                    "func" => CompletionItemKind.Method,
                    "const" => CompletionItemKind.Constant,
                    "signal" => CompletionItemKind.Event,
                    "enum" => CompletionItemKind.Enum,
                    "class" => CompletionItemKind.Class,
                    _ => CompletionItemKind.Variable,
                },
                Detail = member.Type.IsVariant ? member.Kind : member.Type.ToString(),
                FilterText = member.Name,
                SortText = member.Name.StartsWith('_') ? $"1{member.Name}" : $"0{member.Name}",
            };
            items.Add(item);
            described[item] = Said(member);
        }
        completion.Open(new CodeCompletion.Completion(items, start, [], false) { Own = this });
        return true;
    }

    internal static CompletionItem ItemOf(GodotCSharpMember member) => new()
    {
        Label = member.Name,
        Kind = member.Kind switch
        {
            "func" => CompletionItemKind.Method,
            "const" => CompletionItemKind.Constant,
            "signal" => CompletionItemKind.Event,
            _ => CompletionItemKind.Variable,
        },
        Detail = $"C# · {member.Shown}",
        FilterText = member.Name,
        SortText = $"0{member.Name}",
    };

    /// <summary>The members of the C# script the scenes run on the node before a dot (<c>$Player.</c>), read from its file;
    /// none where the value is no such node.</summary>
    internal static async Task<IReadOnlyList<GodotCSharpMember>> CSharpMembersAsync(AssistContext context, OpenDocument document, GodotScriptTypes.Typed typed, int dot)
    {
        if (dot < 1 || typed.Analysis.NodeAt(dot - 1) is not ({ } node, _) || node is not GDScriptExpression
            || typed.Analysis.TypeOf(node) is not { CSharpScript: { } res } || context.GodotOf(document.Path) is not ({ } index, { } folder)
            || (await index.ScenesAsync(folder)).FileOf(res) is not { } file)
        {
            return [];
        }
        OpenTexts texts = context.Editors.Texts();
        return await BackgroundWork.Run(() => texts.Text(file) is { } text ? GodotCSharpMembers.Of(file, text) : (IReadOnlyList<GodotCSharpMember>)[]);
    }

    /// <summary>The member of a node's C# script a name after the node's dot reads (<c>$Player.TakeDamage</c>); null for any
    /// other name.</summary>
    internal static async Task<GodotCSharpMember?> CSharpMemberAtAsync(AssistContext context, OpenDocument document, int offset)
    {
        if (context.GodotScriptTypes?.Invoke() is not { } types || await types.TypedAsync(document) is not { } typed
            || typed.Analysis.NodeAt(offset) is not ({ } node, int from) || node is not GDScriptIdentifier name)
        {
            return null;
        }
        int start = from + name.Start;
        TextSnapshot text = document.Text.Snapshot;
        return start > 1 && start <= text.Length && text[start - 1] == '.'
            ? (await CSharpMembersAsync(context, document, typed, start - 1)).FirstOrDefault(m => m.Name == name.Name)
            : null;
    }

    /// <summary>A C# member's card: its declaration as C# writes it, and the script that declares it.</summary>
    internal static void Describe(IHoverCard card, GodotCSharpMember member)
    {
        card.AddCode(member.Shown);
        card.AddText($"A member of {Path.GetFileName(member.Path)}, the C# script the scene runs on the node");
    }

    /// <summary>Where a C# member's name is written, for Go to Declaration.</summary>
    internal static async Task<(string Path, TextPosition Position)> PlaceOfAsync(AssistContext context, GodotCSharpMember member)
    {
        OpenTexts texts = context.Editors.Texts();
        TextSnapshot snapshot = await BackgroundWork.Run(() => texts.TextOf(member.Path) ?? TextSnapshot.From(string.Empty));
        int at = Math.Min(member.Start, snapshot.Length);
        int line = snapshot.GetLineFromOffset(at);
        return (member.Path, new TextPosition(line, at - snapshot.GetLineStart(line)));
    }

    /// <summary>The members of the value before a dot: its type's, through its classes and the engine's.</summary>
    private static List<GDScriptMemberInfo> MembersAfterDot(GodotScriptTypes.Typed typed, int dot)
    {
        if (typed.Analysis.NodeAt(dot - 1) is not ({ } node, _) || node is not GDScriptExpression)
        {
            return [];
        }
        GDScriptType owner = typed.Analysis.TypeOf(node);
        return owner.IsVariant ? [] : [.. typed.Analyzer.Members(owner)];
    }

    /// <summary>The names a place may use: its function's locals declared before it, its class's members, then the
    /// project's classes and autoloads.</summary>
    private static List<GDScriptMemberInfo> InScope(GodotScriptTypes.Typed typed, int caret)
    {
        List<GDScriptMemberInfo> all = [.. typed.Analysis.LocalsAt(caret)];
        HashSet<string> seen = new(all.Select(m => m.Name), StringComparer.Ordinal);
        foreach (GDScriptMemberInfo member in typed.Analyzer.Members(GDScriptAnalyzer.ScriptClass(typed.Tree, typed.Analysis.Path)))
        {
            if (seen.Add(member.Name))
            {
                all.Add(member);
            }
        }
        return all;
    }

    private static string Said(GDScriptMemberInfo member) =>
        member.Kind == "func" ? $"func {member.Name}(){(member.Type.IsVariant ? string.Empty : $" -> {member.Type}")}"
            : $"{member.Kind} {member.Name}{(member.Type.IsVariant ? string.Empty : $": {member.Type}")}";

    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int end = context.Caret;
        while (end < text.Length && AssistContext.IsIdentifierChar(text[end]))
        {
            end++;
        }
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), item.Label)], SelectionSet.Single(start + item.Label.Length));
    }

    public override string? Describe(CompletionItem item) => described.GetValueOrDefault(item);

    /// <summary>The GDScript code style as Settings leaves it, which a file's .editorconfig overrides.</summary>
    public GDScriptFormatSettings Format { get; set; } = new();

    /// <summary>Settings' Reformat on save.</summary>
    public bool FormatsOnSave { get; set; }

    /// <summary>Reformat Code on a GDScript, the window's and an agent's alike, by Passenger's formatter (Godot's style guide),
    /// off the window's thread, only the lines <paramref name="within"/> takes when given, as one undoable edit fitted to the
    /// file's line endings and indentation: its .editorconfig's (or the person's choice), else Godot's tabs, a selection
    /// the file's own. Refused, said, where laying it out would change what it says or it does not parse.</summary>
    public override Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within) =>
        GodotScriptTypes.IsScript(document.Path) ? ReformatAsync(document, within, "Reformat Code") : Task.FromResult<CodeActions.Reformatted?>(null);

    /// <summary>What a save does first to a changed script, as Settings ask: Reformat Code's layout, one undoable edit,
    /// saying only why it was not applied.</summary>
    public async Task BeforeSave(OpenDocument document)
    {
        if (FormatsOnSave && GodotScriptTypes.IsScript(document.Path) && !document.IsReadOnly
            && await ReformatAsync(document, null, "Reformat on save") is { Applied: false } refused)
        {
            context.Say(refused.Said);
        }
    }

    /// <summary>A script laid out by Godot's style guide as one undoable edit, by <see cref="Format"/> with the file's
    /// .editorconfig over it, only the lines of <paramref name="within"/> when given; what came of it, said as
    /// <paramref name="what"/>.</summary>
    private async Task<CodeActions.Reformatted?> ReformatAsync(OpenDocument document, TextRange? within, string what)
    {
        if (document.IsReadOnly)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: {Refusals.ReadOnly(document.Name)}");
        }
        using ChangeTracker asked = new(document.Text);
        TextSnapshot snapshot = asked.Snapshot;
        // A selection keeps the file's own indentation, which Godot reads as one throughout the file.
        IndentOptions indent = within is not null || document.Indentation.Source is IndentSource.EditorConfig or IndentSource.Chosen
            ? document.Indentation.Options : new IndentOptions(document.Indentation.Options.TabWidth, UseTabs: true, document.Indentation.Options.TabWidth);
        string path = document.Path;
        GDScriptFormatSettings settings = Format;
        GDScriptFormatted formatted = await BackgroundWork.Run(() =>
        {
            EditorConfig config = EditorConfig.For(path);
            GDScriptFormatOptions options = settings.Over(key => config[key]).Options(indent.UseTabs, indent.Size, indent.TabWidth);
            return GDScriptFormatter.Format(snapshot.ToString(), options);
        });
        if (formatted.Refused is { } why)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: {why}");
        }
        IEnumerable<TextEdit> kept = formatted.Edits;
        if (within is { } range)
        {
            int first = snapshot.GetLineStart(snapshot.GetLineFromOffset(range.Start));
            int last = snapshot.GetLine(snapshot.GetLineFromOffset(range.End)).EndIncludingBreak;
            kept = kept.Where(e => e.End >= first && e.Start <= last);
        }
        List<TextEdit> fitted = [.. Indentation.Fit(snapshot, LineEndings.Fit(snapshot, kept, document.LineEnding), indent)
            .Where(e => e.Text != snapshot.GetText(e.Start, e.Length))];
        if (within is not null && fitted.Count > 0
            && await BackgroundWork.Run(() => GDScriptFormatter.Differs(snapshot.ToString(), snapshot.Apply(fitted).ToString())) is { } partly)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: laying out only the selection would change what the script says ({partly}); reformat the whole file");
        }
        if (fitted.Count == 0 || !context.Editors.Documents.Contains(document))
        {
            return new CodeActions.Reformatted(true, false, "Already formatted");
        }
        if (asked.Map(fitted) is not { } edits)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: the code it changes was edited meanwhile");
        }
        WorkspaceEdits.ApplyTo(context.Editors, document, edits);
        return new CodeActions.Reformatted(true, true, "Reformatted");
    }

    /// <summary>A script's top-level parts put in the style guide's Code order (<see cref="GDScriptFormatter.OrderParts"/>) off
    /// the window's thread, as one undoable edit in the file's line endings; what came of it, or why not.</summary>
    public async Task<string> OrderPartsAsync(OpenDocument document)
    {
        const string what = "Order Script's Parts";
        if (document.IsReadOnly)
        {
            return $"{what} was not applied: {Refusals.ReadOnly(document.Name)}";
        }
        using ChangeTracker asked = new(document.Text);
        TextSnapshot snapshot = asked.Snapshot;
        string text = snapshot.ToString();
        GDScriptFormatted ordered = await BackgroundWork.Run(() => GDScriptFormatter.OrderParts(text.ReplaceLineEndings("\n")));
        if (ordered.Refused is { } why)
        {
            return $"{what} was not applied: {why}";
        }
        if (ordered.Edits.Count == 0)
        {
            return "The script's parts are already in order";
        }
        IReadOnlyList<TextEdit> edits = LineEndings.Fit(snapshot, [new TextEdit(0, snapshot.Length, ordered.Text)], document.LineEnding);
        if (!context.Editors.Documents.Contains(document) || asked.Map(edits) is not { } mapped)
        {
            return $"{what} was not applied: the script was edited meanwhile";
        }
        WorkspaceEdits.ApplyTo(context.Editors, document, mapped);
        return "The script's parts are in order";
    }

    /// <summary>A script's reading made for its text as it stands, which its fixes stand on.</summary>
    public override async Task ReadyAsync(OpenDocument document)
    {
        if (GodotScriptTypes.IsScript(document.Path) && context.GodotScriptTypes?.Invoke() is { } types)
        {
            await types.TypedAsync(document);
        }
    }

    /// <summary>The fixes of Godot's findings on an offset's line, from Passenger's reading of the script as it stands
    /// (whoever's marks are shown there): each made as one edit of the text it was offered for.</summary>
    public override IReadOnlyList<EditorFix> Fixes(OpenDocument file, int offset)
    {
        if (!GodotScriptTypes.IsScript(file.Path) || context.GodotScriptTypes?.Invoke()?.Latest(file) is not { } typed)
        {
            return [];
        }
        TextSnapshot text = file.Text.Snapshot;
        int line = text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length));
        List<EditorFix> fixes = [];
        foreach (GDScriptFinding finding in typed.Findings.Where(f => f.Start <= text.Length && text.GetLineFromOffset(f.Start) == line))
        {
            foreach (GDScriptFix fix in GDScriptFixes.Of(typed.Analysis, finding))
            {
                TextEdit first = fix.Edits[0];
                EditorFix made = new(fix.Title, finding.Code is { } code ? $"Godot's {code}" : "Godot's error", new TextRange(first.Start, first.Length), first.Text)
                {
                    Edits = fix.Edits,
                };
                // Made on the text it was worked out for; refused once that text has changed.
                fixes.Add(made with { Again = now => now == text ? made : null });
            }
        }
        return [.. fixes.DistinctBy(f => f.Title)];
    }

    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!Answers(document) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return false;
        }
        GodotScriptTypes.Typed? typed = await types.TypedAsync(document);
        cancel.ThrowIfCancellationRequested();
        if (typed?.Analysis.NodeAt(offset) is not ({ } node, _) || node is not GDScriptIdentifier name)
        {
            return false;
        }
        if (await CSharpMemberAtAsync(context, document, offset) is { } csharp)
        {
            cancel.ThrowIfCancellationRequested();
            Describe(card, csharp);
            return true;
        }
        GDScriptDeclaration? declared = typed.Analysis.DeclarationOf(name);
        GDScriptType type = declared?.Type ?? typed.Analysis.TypeOf(name);
        card.AddCode(type.IsVariant ? name.Name : $"{name.Name}: {type}");
        if (declared is { EngineClass: { } engine })
        {
            card.AddText($"A member of {engine}");
        }
        return true;
    }

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        Declared? declared = await DeclaredAsync(shown, offset);
        if (declared is null)
        {
            context.Say(Refusals.NoDeclaration);
        }
        else if (context.Editors.Active == shown)
        {
            navigation.Go(declared, offset);
        }
        return true;
    }

    /// <summary>Where a name of the script is declared: a local's, a member's (this script's or another's of the project);
    /// null for the engine's own, which no file declares.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (!Answers(document) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return null;
        }
        if (await CSharpMemberAtAsync(context, document, offset) is { } csharp)
        {
            return new Declared(Declared.Served([await PlaceOfAsync(context, csharp)], ServerPlaces.Texts(context.Editors)), Refusals.NoDeclaration);
        }
        GodotScriptTypes.Typed? typed = await types.TypedAsync(document);
        if (typed?.Analysis.NodeAt(offset) is not ({ } node, _) || node is not GDScriptIdentifier name
            || typed.Analysis.DeclarationOf(name) is not { Script: { } script } declared)
        {
            return null;
        }
        string? path = script == typed.Analysis.Path ? document.Path
            : context.GodotOf(document.Path) is ({ } index, { } folder) ? (await index.ScenesAsync(folder)).FileOf(script) : null;
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        TextSnapshot snapshot = path == document.Path ? document.Text.Snapshot : TextSnapshot.From(await File.ReadAllTextAsync(path));
        int line = snapshot.GetLineFromOffset(Math.Min(declared.Start, snapshot.Length));
        return new Declared(Declared.Served([(path, new TextPosition(line, declared.Start - snapshot.GetLineStart(line)))], ServerPlaces.Texts(context.Editors)),
            Refusals.NoDeclaration);
    }
}
