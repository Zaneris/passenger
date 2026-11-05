using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's answers from Passenger's own reading of it (<see cref="GodotScriptTypes"/>), first whether or not
/// Godot's server for it runs: completion (<see cref="CompleteAsync"/>), the cards, Parameter Info, Go to Declaration,
/// Find Usages and Rename across the project's scripts, its scenes and C# (<see cref="CodeActions.GDScriptUsesAsync"/>),
/// and a node's C# script's members (<see cref="GodotCSharpMembers"/>), which Godot's server never reads. Where Passenger's
/// reading has no answer, Godot's server is asked where it serves.</summary>
internal sealed partial class GDScriptAssist(AssistContext context, CodeCompletion completion, ParameterInfo parameters, CodeNavigation navigation, CodeActions actions)
    : LanguageAssist
{
    /// <summary>Whether a file is a GDScript Passenger reads.</summary>
    private bool Answers(OpenDocument? document) =>
        document is not null && GodotScriptTypes.IsScript(document.Path) && context.GodotScriptTypes?.Invoke() is not null;

    /// <summary>Whether Godot's server serves a file, to be asked for what Passenger's reading cannot answer.</summary>
    private bool Served(OpenDocument document) => context.WebOf(document) is { Serving: true };

    /// <summary>Godot's server asked for where none serves a file, for a person's question Passenger's reading has no
    /// answer for: it answers the next one once it serves.</summary>
    private void AskGodot(OpenDocument document) => context.WebOf(document)?.AskServer?.Invoke();

    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        char last = text[^1];
        int caret = context.Caret;
        TextSnapshot snapshot = shown.Text.Snapshot;
        char before = caret >= 2 && caret - 2 < snapshot.Length ? snapshot[caret - 2] : '\0';
        // A member's dot, a name's first letter, an annotation's @, a node path's $, % or /, and a string's quote.
        if (last is '.' or '@' or '$' or '%' or '/' or '"' or '\'')
        {
            IdeErrors.Observe(completion.Start(last.ToString()));
        }
        else if (AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1 && before is not ('$' or '%' or '@'))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        // A call's signature after its bracket or comma; gone after its closing bracket.
        parameters.AfterTyped(last);
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

    public override void Commit(CompletionItem item, int start)
    {
        Offered? made = offered.GetValueOrDefault(item) ?? GlobalOffered(item);
        string insert = made?.Insert ?? item.Label;
        int back = made?.Back ?? 0;
        TextSnapshot text = context.Editor.Document.Snapshot;
        int end = context.Caret;
        Replacing replaces = made?.Replaces ?? Replacing.Name;
        while (end < text.Length && (replaces switch
        {
            Replacing.String => text[end] is not ('"' or '\'' or '\n'),
            Replacing.Path => AssistContext.IsIdentifierChar(text[end]) || text[end] == '/',
            _ => AssistContext.IsIdentifierChar(text[end]),
        }))
        {
            end++;
        }
        // A call's parentheses written after the name already are its own.
        if (insert.EndsWith("()", StringComparison.Ordinal) && end < text.Length && text[end] == '(')
        {
            insert = insert[..^2];
            back = 0;
        }
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), insert)], SelectionSet.Single(start + insert.Length - back));
        if (back > 0)
        {
            // The call's parentheses put in hold the caret: its signature over them.
            IdeErrors.Observe(parameters.Show());
        }
    }

    /// <summary>What one of the engine's global items puts in: a utility function its call's parentheses, the caret inside
    /// them where it takes arguments; null for any other, which puts in its name.</summary>
    private Offered? GlobalOffered(CompletionItem item) =>
        GlobalsNamed.TryGetValue(item, out Named? named) && named is OfEngine { Kind: "func" } function && listedFrom is { } typed
            ? new Offered(named, function.Name + "()", SignatureOf(typed, named) is { Parameters.Count: 0, Varargs: false } ? 0 : 1) : null;

    public override string? Describe(CompletionItem item) =>
        (offered.GetValueOrDefault(item)?.What ?? (GlobalsNamed.TryGetValue(item, out Named? global) ? global : null)) is { } named && listedFrom is { } typed
            ? SaidOf(typed, named) : null;

    /// <summary>A function's or signal's signature where a name is one: a script's as written, the engine's as its API gives
    /// it; null for any other.</summary>
    private static GDScriptSignature? SignatureOf(GodotScriptTypes.Typed typed, Named named)
    {
        switch (named)
        {
            case OfScript { Member: { Kind: "func" or "signal", Declaration: { Script: { } script, Start: var start } } }:
                GDScriptTree? tree = script == typed.Analysis.Path ? typed.Tree : typed.Analyzer.Project.Script(script);
                return tree is not null && GDScriptSignature.DeclaredAt(tree, start) is ({ } node, int offset)
                    ? node switch
                    {
                        GDScriptFunction function => GDScriptSignature.Of(function, tree.Text, offset),
                        GDScriptSignal signal => GDScriptSignature.Of(signal, tree.Text, offset),
                        _ => null,
                    }
                    : null;
            case OfEngine { Kind: "func" or "signal" } engine when typed.Analyzer.Engine is { } api:
                GodotApiMethod? method = engine.Owner == GlobalScope ? api.Functions.GetValueOrDefault(engine.Name)
                    : api.Builtins.GetValueOrDefault(engine.Owner) is { } builtin ? builtin.Methods.GetValueOrDefault(engine.Name)
                    : api.Chain(engine.Owner).Select(c => engine.Kind == "signal" ? c.Signals.GetValueOrDefault(engine.Name) : c.Methods.GetValueOrDefault(engine.Name))
                        .FirstOrDefault(m => m is not null);
                return method is null ? null : GDScriptSignature.Of(method, engine.Owner == GlobalScope ? null : engine.Owner, typed.Analyzer, engine.Kind);
            default:
                return null;
        }
    }

    /// <summary>How a card's first line writes what a name names: a function's or signal's signature, a variable's or
    /// constant's type, a class by what it is.</summary>
    private static string SaidOf(GodotScriptTypes.Typed typed, Named named) => named switch
    {
        OfScript or OfEngine when SignatureOf(typed, named) is { } signature => signature.ToString(),
        OfScript { Member: var member } => $"{member.Kind} {member.Name}{(member.Type.IsVariant ? string.Empty : $": {member.Type}")}",
        OfEngine engine => $"{engine.Kind} {(engine.Owner == GlobalScope ? string.Empty : engine.Owner + ".")}{engine.Name}{(engine.Type.IsVariant ? string.Empty : $": {engine.Type}")}",
        OfClass { Kind: "autoload" } autoload => $"{autoload.Name}, an autoload",
        OfClass { Kind: "class_name" } scripted => $"class {scripted.Name}",
        OfClass { Kind: "type" } builtin => $"{builtin.Name}, a built-in type",
        OfClass engineClass => typed.Analyzer.Engine?.Classes.GetValueOrDefault(engineClass.Name)?.Inherits is { } inherits ? $"class {engineClass.Name} extends {inherits}" : $"class {engineClass.Name}",
        OfCSharp csharp => csharp.Member.Shown,
        OfWords words => words.Said,
        _ => string.Empty,
    };

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
        GodotEngineApi? engine = context.GodotScriptTypes?.Invoke() is { } types && await types.TypedAsync(document) is { } typed ? typed.Analyzer.Engine : null;
        GDScriptFormatted ordered = await BackgroundWork.Run(() => GDScriptFormatter.OrderParts(text.ReplaceLineEndings("\n"), engine));
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

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        Declared? declared = await DeclaredAsync(shown, offset);
        if (declared is null && Served(shown))
        {
            // Godot's server, for what Passenger's reading finds no declaration of.
            return false;
        }
        if (declared is null)
        {
            AskGodot(shown);
            context.Say(Refusals.NoDeclaration);
        }
        else if (context.Editors.Active == shown)
        {
            navigation.Go(declared, offset);
        }
        return true;
    }

    /// <summary>Find Usages of a script's member or local by Passenger's reading, the places that may use it unseen said
    /// beside them; Godot's server's for any other name where it serves.</summary>
    public override async Task<bool> UsagesAsync(int offset, bool inPopup)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        using ChangeTracker asked = new(shown.Text);
        (IReadOnlyList<LspLocation> places, IReadOnlyList<GDScriptPlace> unsure)? found = await actions.GDScriptUsesAsync(shown, offset);
        if (found is not ({ } places, { } unsure))
        {
            if (Served(shown))
            {
                return false;
            }
            AskGodot(shown);
            context.Say("Find Usages finds nothing here: Passenger reads no member or local of a script at the caret, and Godot's server is not serving the script yet");
            return true;
        }
        TextSnapshot text = shown.Text.Snapshot;
        string name = text.GetText(TextNavigation.WordAt(text, Math.Min(asked.MapOffset(offset), text.Length)));
        string? note = unsure.Count == 0 ? null
            : $"{Plural.Of(unsure.Count, "place")} may use it where Passenger can't see whose member is read: {GDScriptRefactorings.Places(unsure)}";
        context.ShowUsages($"Usages of {(name.Length > 0 ? name : "symbol")}", places, inPopup ? asked.MapOffset(offset) : null, note);
        return true;
    }

    /// <summary>Rename at the caret of a script, by Passenger's reading first (<see cref="CodeActions.ShowGDScriptRename"/>).</summary>
    public override bool Rename(int caret)
    {
        if (context.Editors.Active is not { } shown || !Answers(shown))
        {
            return false;
        }
        actions.ShowGDScriptRename();
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
