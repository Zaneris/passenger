using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's answers from Passenger's own reading of it (<see cref="GodotScriptTypes"/>) while Godot's server
/// for it has not started: completion of the names in scope and of a value's members, the hover card's declaration and
/// type, and Go to Declaration within the project's scripts. Once Godot's server runs, its answers are the file's.</summary>
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
        List<GDScriptMemberInfo> members = start > 0 && asked[start - 1] == '.' ? MembersAfterDot(typed, start - 1) : InScope(typed, caret);
        if (members.Count == 0)
        {
            completion.Close();
            return true;
        }
        described.Clear();
        List<CompletionItem> items = [];
        foreach (GDScriptMemberInfo member in members)
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

    /// <summary>The fixes of Godot's findings on an offset's line, from Passenger's reading of the script as it stands
    /// (whoever's marks are shown there): each made as one edit of the text it was offered for.</summary>
    public IReadOnlyList<EditorFix> Fixes(OpenDocument file, int offset)
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
