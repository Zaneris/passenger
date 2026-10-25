using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Optimize Imports and refactorings beyond Refactor This: Safe Delete, built from the server's definition and
/// usages and the file's syntax, and Introduce Field, Introduce Parameter and Encapsulate Field, the server's own code
/// actions under names of their own.</summary>
internal sealed partial class CodeActions
{
    /// <summary>A refactoring the server offers as code actions in words of its own: which of its actions are it, which
    /// of their nested choices fit it, and what to say when the server offers none here.</summary>
    public sealed record ServerRefactoring(string Name, Func<CodeAction, bool> Pick, string Missing, Func<ActionChoice, bool>? Keep = null);

    /// <summary>The server makes a field only of a constant (its Introduce constant, whose nested choices include a
    /// local constant) or of a field initializer's value; of anything else, a local.</summary>
    public static readonly ServerRefactoring IntroduceField = new("Introduce Field",
        a => a.Title.StartsWith(RoslynNames.IntroduceField, StringComparison.Ordinal) || a.Title.StartsWith(RoslynNames.IntroduceConstant, StringComparison.Ordinal),
        "the C# language server makes a field only of a constant expression",
        c => !c.Text.Contains(RoslynNames.Local, StringComparison.OrdinalIgnoreCase));

    public static readonly ServerRefactoring IntroduceParameter = new("Introduce Parameter",
        a => a.Title.StartsWith(RoslynNames.IntroduceParameter, StringComparison.Ordinal),
        "choose an expression in a method's body that uses none of its locals");

    public static readonly ServerRefactoring EncapsulateField = new("Encapsulate Field",
        a => a.Title.StartsWith(RoslynNames.EncapsulateField, StringComparison.Ordinal),
        "put the caret on a field's declaration");

    /// <summary>A refactoring the server offers under its own words, over the selection: its one action carried out, or
    /// a list of them when it offers several (Encapsulate Field's with and without the property used), then the choices
    /// nested under the one chosen (Introduce Parameter's into a new overload or an extracted method).</summary>
    public async Task Refactor(ServerRefactoring refactoring)
    {
        string name = refactoring.Name;
        if (context.Ask(name) is not { } lsp || context.Editors.Active is not { } document)
        {
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            Selection primary = Editor.Selections.Primary;
            using Asked asked = WorkspaceEdits.Track(context.Editors);
            IReadOnlyList<CodeAction> offered = await OfferedAsync(lsp, primary.Range, refactoring);
            if (offered.Count == 0)
            {
                context.Say($"{name} is not available here: {refactoring.Missing}");
                return;
            }
            Action done = () => context.Say($"{name} done");
            if (offered.Count == 1)
            {
                await Carry(document, offered[0], primary.Caret, name, asked, done, refactoring.Keep);
                return;
            }
            popups.ShowModal(new ChoicePopup(name, [.. offered.Select(a => new Choice(a.Title, string.Empty, a))]), primary.Caret, name,
                choice => choice?.Value is CodeAction chosen ? Carry(document, chosen, primary.Caret, name, null, done, refactoring.Keep) : Task.CompletedTask);
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(name, failed);
        }
    }

    /// <summary>The server's actions that are a refactoring over a range of a file, those it would not let run left
    /// out.</summary>
    public static async Task<IReadOnlyList<CodeAction>> OfferedAsync(LspDocument lsp, TextRange range, ServerRefactoring refactoring) =>
        [.. (await lsp.CodeActionsAsync(range, [])).Where(a => a.Disabled is null && refactoring.Pick(a))];

    /// <summary>The choices the server nests under one of a refactoring's actions that are of its kind (Introduce Field's
    /// without its local constant), as its list offers them.</summary>
    public static IReadOnlyList<ActionChoice> ChoicesOf(CodeAction action, ServerRefactoring refactoring) =>
        refactoring.Keep is { } keep ? [.. ClientCommands.Choices(action).Where(keep)] : ClientCommands.Choices(action);

    public async Task OptimizeUsings()
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            if (context.Ask("Optimize Imports") is not { } lsp || context.Editors.Active is not { } document)
            {
                return;
            }
            (bool removed, bool sorted) = await TidyUsings(lsp, document);
            context.Say(Optimized(removed, sorted));
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Optimize Imports", failed);
        }
    }

    /// <summary>What Optimize Imports says it did to a file.</summary>
    public static string Optimized(bool removed, bool sorted) => (removed, sorted) switch
    {
        (true, true) => "Optimize Imports removed unused usings and sorted the rest",
        (true, false) => "Optimize Imports removed unused usings",
        (false, true) => "Optimize Imports sorted the usings",
        _ => "The usings are already optimized",
    };

    /// <summary>Optimize Imports in C#, as Code Cleanup's first step too: the usings the server finds unnecessary
    /// removed (IDE0005's fix), then the rest sorted (its Sort Usings); whether each changed the file.</summary>
    public async Task<(bool Removed, bool Sorted)> TidyUsings(LspDocument lsp, OpenDocument document)
    {
        // Asked of the server now rather than read from the diagnostics shown, which a file just opened has yet to get.
        (IReadOnlyList<Diagnostic> found, _) = await lsp.DiagnosticsAsync();
        List<Diagnostic> unused = [.. found.Where(d => d.Code is { ValueKind: System.Text.Json.JsonValueKind.String } code && code.GetString() == RoslynNames.UnnecessaryUsing)];
        bool removed = unused.Count > 0
            && await ApplyNamed(lsp, document, LineRange(document.Text.Snapshot, document.Text.Snapshot.GetLineFromOffset(lsp.OffsetOf(unused[0].Range.Start))), unused, RoslynNames.RemoveUnnecessaryUsings);
        TextSnapshot text = document.Text.Snapshot;
        int usingLine = Enumerable.Range(0, text.LineCount).FirstOrDefault(l => text.GetLineText(l).TrimStart().StartsWith("using ", StringComparison.Ordinal) && text.GetLineText(l).TrimEnd().EndsWith(';'), -1);
        bool sorted = false;
        if (usingLine >= 0)
        {
            string before = text.ToString();
            sorted = await ApplyNamed(lsp, document, LineRange(text, usingLine), [], RoslynNames.SortUsings) && document.Text.Snapshot.ToString() != before;
        }
        return (removed, sorted);
    }

    private static TextRange LineRange(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        return new TextRange(span.Start, span.Length);
    }

    /// <summary>Safe Delete of the symbol at the caret (its declaration or a use of it): the server's definition says
    /// where it is declared, the file's syntax what deleting it takes out, and the server's usages what would break.
    /// With no usage outside the declaration it is deleted at once; with some, they are listed first and it is deleted
    /// only if the person says Delete Anyway.</summary>
    public async Task SafeDelete()
    {
        if (context.Ask("Safe Delete") is not { } lsp)
        {
            return;
        }
        int caret = context.Caret;
        Doomed? doomed;
        using (BackgroundWork.Scope())
        {
            try
            {
                (doomed, string? whyNot) = await FindDoomedAsync(lsp, caret);
                if (whyNot is not null)
                {
                    context.Say(whyNot);
                }
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Safe Delete", failed);
                return;
            }
        }
        if (doomed is null)
        {
            return;
        }
        try
        {
            if (doomed.Uses.Count > 0 && !await DeleteAnyway(doomed))
            {
                context.Say($"Safe Delete left the {doomed.Removal.Kind} '{doomed.Removal.Name}' where it is");
                return;
            }
            context.Say(Delete(doomed).Said);
        }
        finally
        {
            doomed.Dispose();
        }
    }

    /// <summary>What Safe Delete would take out of the symbol at an offset (its declaration or a use of it), and the usages
    /// that would break; else why it cannot (no declaration, one outside the folder's code, a parameter). The caller
    /// disposes what it found.</summary>
    public async Task<(Doomed? Doomed, string? WhyNot)> FindDoomedAsync(LspDocument lsp, int caret)
    {
        IReadOnlyList<LspLocation> declared = await lsp.DefinitionAsync(caret);
        if (declared.Count != 1)
        {
            return (null, declared.Count == 0 ? "Nothing to delete here: put the caret on a name" : $"Safe Delete cannot delete a symbol declared in {declared.Count} places");
        }
        string path = DocumentUris.ToPath(declared[0].Uri);
        string? root = context.CSharp?.Session?.Folder;
        OpenDocument? open = context.Editors.Find(path);
        if (root is null || !PathNames.IsUnder(path, root) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || open?.IsReadOnly == true)
        {
            return (null, "Safe Delete deletes only what this folder's own code declares");
        }
        ParsedText? syntax = open?.Syntax?.Current ?? (FileText.Load(path) is { } file ? SafeDeletion.Parse(file.Text) : null);
        if (syntax is null)
        {
            return (null, $"Safe Delete cannot read {Path.GetFileName(path)} as text");
        }
        ServerLines lines = ServerLines.Of(syntax.Text);
        int at = lines.OffsetOf(declared[0].Range.Start);
        if (SafeDeletion.At(syntax, at) is not { } removal)
        {
            return (null, "Safe Delete removes a type, a member or a local, and none is declared here");
        }
        if (removal.WhyNot is { } why)
        {
            return (null, why);
        }
        // The open file followed from the text read, so what is typed while the usages come is carried.
        ChangeTracker? tracker = open is null ? null : new ChangeTracker(open.Text);
        try
        {
            // What uses it from inside what goes with it (a recursive call, a constructor of a type) goes too.
            IReadOnlyList<LspLocation> referenced = await lsp.ReferencesAsync(caret);
            Dictionary<string, string> paths = DocumentUris.ToPaths(referenced.Select(r => r.Uri));
            List<LspLocation> uses = [.. referenced
                .Where(r => paths[r.Uri] != path || !removal.Range.Contains(lines.OffsetOf(r.Range.Start)))
                .OrderBy(r => paths[r.Uri], StringComparer.Ordinal).ThenBy(r => r.Range.Start.Line).ThenBy(r => r.Range.Start.Character)];
            return (new Doomed(path, open, tracker, syntax.Text, at, removal, uses), null);
        }
        catch
        {
            tracker?.Dispose();
            throw;
        }
    }

    /// <summary>Asks whether to delete what is still used, listing each usage that would break by its file, line and code.</summary>
    private async Task<bool> DeleteAnyway(Doomed doomed)
    {
        if (TopLevel.GetTopLevel(Editor) is not Window owner)
        {
            return false;
        }
        string root = context.CSharp?.Session?.Folder ?? string.Empty;
        Grid rows = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Dictionary<string, string> paths = DocumentUris.ToPaths(doomed.Uses.Select(u => u.Uri));
        Func<string, LspPosition, (int Line, string Code)> preview = navigation.LinePreviews();
        foreach ((LspLocation use, int row) in doomed.Uses.Select((u, i) => (u, i)))
        {
            string path = paths[use.Uri];
            string shown = PathNames.Shown(path, root);
            (int line, string used) = preview(path, use.Range.Start);
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock where = new() { Text = $"{shown}:{line + 1}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 12, 1) };
            TextBlock code = new()
            {
                Text = used,
                FontFamily = Fonts.Mono,
                FontSize = 12,
                Foreground = InfoCard.Brush(TokenKeys.TextMuted),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(where, row);
            Grid.SetRow(code, row);
            Grid.SetColumn(code, 1);
            rows.Children.Add(where);
            rows.Children.Add(code);
        }
        int count = doomed.Uses.Count;
        string text = $"The {doomed.Removal.Kind} '{doomed.Removal.Name}' has {Plural.Of(count, "usage")} that would no longer compile once it is deleted:";
        ScrollViewer list = new() { MaxHeight = 220, Content = rows, Margin = new Thickness(0, 0, 0, 4) };
        return await new ConfirmDialog("Safe Delete", text, "Delete Anyway", detail: list).ShowDialog<bool>(owner);
    }

    /// <summary>Takes the declaration out of its file as it is now: an open one through its document, one undo step; one
    /// not open on disk, a command of its own. One whose declaration was edited meanwhile is left alone, and said so.</summary>
    public RefactorOutcome Delete(Doomed doomed)
    {
        Removal removal = doomed.Removal;
        string uses = doomed.Uses.Count == 0 ? string.Empty : $", leaving {Plural.Of(doomed.Uses.Count, "usage")} to fix";
        int first = doomed.Text.GetLineFromOffset(removal.Range.Start);
        int last = doomed.Text.GetLineFromOffset(Math.Max(removal.Range.Start, removal.Range.Start + removal.Range.Length - 1));
        try
        {
            if (doomed.Open is { } open)
            {
                if (!context.Editors.Documents.Contains(open) || open.Syntax is not { } parser
                    || SafeDeletion.At(parser.Current, doomed.Tracker!.MapOffset(doomed.Offset)) is not { WhyNot: null } now || now.Name != removal.Name)
                {
                    return RefactorOutcome.Refused($"Safe Delete was not applied: '{removal.Name}' was edited meanwhile");
                }
                WorkspaceEdits.ApplyTo(context.Editors, open, [new TextEdit(now.Range.Start, now.Range.Length, string.Empty)]);
            }
            else
            {
                if (FileText.Load(doomed.Path) is not { } file || file.Text != doomed.Text.ToString())
                {
                    return RefactorOutcome.Refused($"Safe Delete was not applied: {Path.GetFileName(doomed.Path)} changed meanwhile");
                }
                // A command of its own, as the file has no undo: Undo in the Solution view puts the declaration back.
                using Editors.CommandHistory.Recording recording = context.Editors.Commands.Record("Safe Delete");
                recording.Watch(doomed.Path);
                _ = FileText.Save(doomed.Path, doomed.Text.Replace(removal.Range.Start, removal.Range.Length, string.Empty).ToString(), file.Encoding, file.HasBom, file.CrBreaks);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RefactorOutcome.Refused($"Safe Delete failed: {error.Message}");
        }
        return new RefactorOutcome($"Deleted the {removal.Kind} '{removal.Name}'{uses}", null,
            [new RefactoredFile(doomed.Path, "changed", [.. Enumerable.Range(first + 1, last - first + 1)])], []);
    }

    /// <summary>What Safe Delete takes out: the declaring file (its document when open, followed from when its usages were
    /// asked for), its text then, where the name is in it, what goes, and the usages that would break.</summary>
    public sealed record Doomed(string Path, OpenDocument? Open, ChangeTracker? Tracker, TextSnapshot Text, int Offset, Removal Removal, IReadOnlyList<LspLocation> Uses) : IDisposable
    {
        public void Dispose() => Tracker?.Dispose();
    }
}
