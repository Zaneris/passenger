using Passenger.App.Editors;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Code actions and refactorings: Show Context Actions (Passenger's own fixes first, then ESLint's and the language
/// server's), Generate and Refactor This, the refactorings by their own keys and dialogs, Rename, Reformat Code, Code
/// Cleanup and Optimize Imports, each change one command that Undo takes back whole.</summary>
internal sealed partial class CodeActions(AssistContext context, PopupPlacement popups, CodeNavigation navigation)
{
    private CodeEditor Editor => context.Editor;

    /// <summary>The fixes and refactorings at the caret (those <paramref name="only"/> takes), listed under
    /// <paramref name="title"/>, the one chosen made as the work of the action <paramref name="id"/>.</summary>
    public async Task ShowFixes(string id, Func<CodeAction, bool>? only = null, string title = "Fixes and refactorings")
    {
        using (BackgroundWork.Scope())
        {
            await ShowFixesCore(only, title, id);
        }
    }

    /// <summary>The fixes Passenger offers itself at an offset of a file, which Show Context Actions lists before asking the
    /// language server; none by default.</summary>
    public Func<OpenDocument, int, IReadOnlyList<EditorFix>>? EditorFixes { get; set; }

    /// <summary>Asks the person for a value a fix sets (a title, what is asked, whether it is a secret, masked as typed);
    /// null when they gave none.</summary>
    public Func<string, string, bool, Task<string?>>? AskValue { get; set; }

    internal IReadOnlyList<EditorFix> OwnFixes(OpenDocument? file, int offset) => file is not null && EditorFixes?.Invoke(file, offset) is { } fixes ? fixes : [];

    /// <summary>One of Passenger's own fixes made in the file shown, as one undoable edit: worked out again on its text as it
    /// stands (<see cref="EditorFix.On"/>), and refused, said in the status bar, when the text no longer has what it fixes;
    /// one made in another file (<see cref="EditorFix.Elsewhere"/>) is that file's command, said as it lands.</summary>
    internal async Task ApplyOwnFix(OpenDocument document, EditorFix fix)
    {
        if (context.Editors.Active != document)
        {
            return;
        }
        if (fix.On(document.Text.Snapshot) is not { } now)
        {
            context.Say($"{fix.Title} was not applied: the file changed, so it no longer fits");
            return;
        }
        if (now.Elsewhere is not null)
        {
            // Worked out again on the text as it stands each time it is made.
            RefactorOutcome outcome = await context.PatientlyAsync(AppActions.QuickFix, patience => fix.On(document.Text.Snapshot) is { Elsewhere: { } making } again
                ? making(again.Range, patience)
                : Task.FromResult(RefactorOutcome.Refused($"{fix.Title} was not applied: the file changed, so it no longer fits")));
            context.Say(outcome.Problem ?? outcome.Said);
            return;
        }
        if (now.Asks is { } asks)
        {
            // Not awaited: the question holds no background work, so a settle waits on the IDE, never on the person.
            IdeErrors.Observe(AskAndMakeAsync(now.Title.TrimEnd('…'), asks));
            return;
        }
        Editor.ApplyEdits([new TextEdit(now.Range.Start, now.Range.Length, now.Text)], SelectionSet.Single(now.Range.Start + (now.Caret ?? now.Text.Length)));
    }

    /// <summary>A fix's value asked of the person (<see cref="AskValue"/>), then the fix made from it as background work,
    /// what it came to said; nothing when they give none.</summary>
    private async Task AskAndMakeAsync(string title, ValueAsked asks)
    {
        try
        {
            if (AskValue is null || await AskValue(title, asks.Question, asks.Secret) is not { } value)
            {
                return;
            }
            using (BackgroundWork.Scope())
            {
                RefactorOutcome made = await asks.Make(value, null);
                context.Say(made.Problem ?? made.Said);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    private async Task ShowFixesCore(Func<CodeAction, bool>? only, string title, string id)
    {
        if (context.XamlCurrent is { } xaml)
        {
            await context.Languages.Xaml.FixesAsync(this, xaml, only is null ? OwnFixes(context.Editors.Active, context.Caret) : [], refactor: only is not null);
            return;
        }
        string what = only is null ? "Show Context Actions" : title;
        // Passenger's own fixes first, which need no server (a compose file's are its container engine's).
        IReadOnlyList<EditorFix> own = only is null ? OwnFixes(context.Editors.Active, context.Caret) : [];
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        Selection primary = Editor.Selections.Primary;
        IReadOnlyList<EslintFix> linted = [];
        IReadOnlyList<CodeAction> actions = [];
        if (context.Ask(own.Count > 0 ? null : what) is { } lsp && context.Current is { } current)
        {
            int line = lsp.PositionOf(primary.Caret).Line;
            List<Diagnostic> here = current.Diagnostics
                .Where(d => d.Range.Start.Line <= line && line <= d.Range.End.Line)
                .ToList();
            try
            {
                // ESLint's fixes for the line first, before the language's own.
                linted = only is null ? await context.Languages.Web.LintFixes(document, primary.Caret) : [];
                actions = [.. (await lsp.CodeActionsAsync(primary.Range, here)).Where(a => a.Disabled is null && (only?.Invoke(a) ?? true))];
            }
            catch (LanguageServerFailed failed)
            {
                if (own.Count == 0)
                {
                    context.Failed(what, failed);
                    return;
                }
            }
        }
        else if (own.Count == 0)
        {
            return;
        }
        if (own.Count == 0 && actions.Count == 0 && linted.Count == 0)
        {
            context.Say(only is null ? "No fixes or refactorings here" : "No refactorings here");
            return;
        }
        List<Choice> choices = [.. own.Select(f => new Choice(f.Title, f.Detail, f)), .. linted.Select(f => new Choice(f.Title, CodeActionKinds.Shown(f.Action.Kind), f)),
            .. actions.Select(a => new Choice(a.Title, CodeActionKinds.Shown(a.Kind), a))];
        popups.ShowModal(new ChoicePopup(title, choices), primary.Caret, what, choice =>
        {
            switch (choice?.Value)
            {
                case EditorFix fix:
                    return ApplyOwnFix(document, fix);
                case EslintFix fix:
                    return context.Languages.Web.RunLintFix(document, fix);
                case CodeAction chosen:
                    return Carry(document, chosen, primary.Caret, chosen.Title, id, null);
                default:
                    return Task.CompletedTask;
            }
        });
    }

    /// <summary>A code action chosen, carried out: one that offers choices (a fix-all's scopes, the actions nested under
    /// it) offers them as a list at <paramref name="at"/> and applies the one chosen there, or the one choice
    /// <paramref name="keep"/> leaves; any other is applied at once, as the work of the action <paramref name="id"/>.
    /// <paramref name="applied"/> runs once its change is made.</summary>
    private async Task Carry(OpenDocument document, CodeAction action, int at, string what, string id, Asked? listed, Action? applied = null, Func<ActionChoice, bool>? keep = null)
    {
        IReadOnlyList<ActionChoice> offered = ClientCommands.Choices(action);
        IReadOnlyList<ActionChoice> choices = keep is null ? offered : [.. offered.Where(keep)];
        if (offered.Count > 0 && choices.Count == 0)
        {
            context.Say($"{what} is not available here: the language server offers none of its kind");
            return;
        }
        if (choices.Count == 0 || keep is not null && choices.Count == 1)
        {
            (CodeAction chosen, string? scope) = choices.Count == 1 ? (choices[0].Action, choices[0].Scope) : (action, null);
            if (await Apply(document, chosen, scope, what, choices.Count == 1 ? null : listed, id))
            {
                applied?.Invoke();
            }
            return;
        }
        popups.ShowModal(new ChoicePopup(action.Title, [.. choices.Select(c => new Choice(c.Text, string.Empty, c))]), at, what, async choice =>
        {
            if (choice?.Value is ActionChoice chosen && await Apply(document, chosen.Action, chosen.Scope, what, null, id))
            {
                applied?.Invoke();
            }
        });
    }

    /// <summary>A code action's change made (<see cref="ApplyAsync"/>), what it came to said in the status bar, and a name the
    /// server asks be renamed then offered to rename. <paramref name="listed"/> follows the files from when the actions were
    /// asked for. One whose edit the server works out as it is made waits, as the work of the action <paramref name="id"/>,
    /// for a file not open written a moment before (<see cref="AssistContext.PatientlyAsync{T}"/>). Whether every file took it.</summary>
    private async Task<bool> Apply(OpenDocument document, CodeAction action, string? scope, string what, Asked? listed, string? id)
    {
        (RefactorOutcome made, bool applied) = id is not null && (action.Edit is null || scope is not null)
            ? await context.PatientlyAsync(id, patience => ApplyAsync(document, action, scope, what, listed, patience), done => done.Outcome.Problem is not null)
            : await ApplyAsync(document, action, scope, what, listed);
        if (made.Problem is not null)
        {
            context.Say(made.Problem);
            return false;
        }
        if (made.Left.Count > 0)
        {
            context.Say($"{what} was not applied to {string.Join(", ", made.Left)}: it was edited meanwhile");
        }
        if (made.Said.Length > 0)
        {
            context.Say(made.Said);
        }
        if (made.RenameAt is { } place && context.Editors.Active == document && PathNames.Same(DocumentUris.ToPath(place.Uri), document.Path))
        {
            Editor.Selections = SelectionSet.Single(ServerLines.Of(document.Text.Snapshot).OffsetOf(place.Range.Start));
            Rename();
        }
        return applied;
    }

    /// <summary>A code action's change made, as <see cref="Apply"/> makes it, for the status bar or an agent: why not, else
    /// the files it changed, those left as edited meanwhile, and what to say beyond its being done (a command it carries that
    /// Passenger cannot run, that it changed nothing, or that it is done after a file landed late); and whether every file
    /// took it. The files come under the projects its edit reaches are waited for, and it is resolved again once they are
    /// in them. A refusal for files the server had yet to read is told its command's <paramref name="patience"/>.</summary>
    public async Task<(RefactorOutcome Outcome, bool Applied)> ApplyAsync(OpenDocument document, CodeAction action, string? scope, string what, Asked? listed, Patience? patience = null)
    {
        bool resolve = (action.Edit is null && action.Data is not null) || scope is not null;
        for (bool again = false; ; again = true)
        {
            // An answer resolved now is for the text as it is now, and carried through what is typed while it comes.
            using Asked? resolving = resolve ? WorkspaceEdits.Track(context.Editors) : null;
            CodeAction chosen;
            try
            {
                chosen = resolve ? await context.Now(document).ResolveChosenAsync(action, scope) : action;
            }
            catch (LanguageServerFailed failed)
            {
                return (RefactorOutcome.Refused($"{what} was not applied: {failed.Reason}"), false);
            }
            if (!again && chosen.Edit is { } reaching)
            {
                (string? unloaded, bool waited) = await context.TakenInAsync(reaching);
                if (unloaded is not null)
                {
                    return (RefactorOutcome.Refused($"{what} was not applied: {unloaded}"), false);
                }
                if (waited && resolve)
                {
                    continue;
                }
            }
            return await ApplyChosenAsync(document, chosen, what, resolving ?? listed, patience);
        }
    }

    /// <summary>A code action's change made as <see cref="ApplyAsync"/> resolved it: with a command its server runs, by
    /// <see cref="ServerCommands"/>.</summary>
    private async Task<(RefactorOutcome Outcome, bool Applied)> ApplyChosenAsync(OpenDocument document, CodeAction chosen, string what, Asked? asked, Patience? patience)
    {
        if (chosen.Command is { } run && (context.CSharp?.For(document)?.Lsp ?? context.WebOf(document)?.Lsp) is { } server && server.Runs(run))
        {
            RefactorOutcome ran = await ServerCommands.RunAsync(context.Editors, what, what, chosen.Edit, taking => server.ExecuteCommandAsync(run, taking), asked, patience, context.Say);
            return (ran, ran.Problem is null && ran.Left.Count == 0 && ran.Files.Count > 0);
        }
        if (chosen.Edit is { } changes && await StaleAsync(document, changes, asked, patience) is { } stale)
        {
            return (RefactorOutcome.Refused($"{what} was not applied: {stale}"), false);
        }
        RefactorOutcome made = chosen.Edit is { } edit ? await ApplyEditAsync(edit, asked, what) : new RefactorOutcome(string.Empty, null, [], []);
        if (made.Problem is not null)
        {
            return (made, false);
        }
        string said = chosen.Command is { } command ? NotRun(what, command) : chosen.Edit is null ? $"{what} changed nothing" : made.Said;
        return (made with { Said = said }, chosen.Edit is not null && made.Left.Count == 0);
    }

    /// <summary>A workspace edit that changes nothing, for asking whether the server had read the files it answered from.</summary>
    private static readonly System.Text.Json.JsonElement NoEdit = System.Text.Json.JsonSerializer.SerializeToElement(new { });

    /// <summary>Why a server's edit must not be applied to the files not open it changes (<see cref="WorkspaceEdits.StaleAsync"/>):
    /// the C# server is asked what it holds of them; another server's files are judged by when they changed alone.</summary>
    private Task<string?> StaleAsync(OpenDocument document, System.Text.Json.JsonElement edit, Asked? asked, Patience? patience = null) =>
        WorkspaceEdits.StaleAsync(context.Editors, context.CSharp?.For(document)?.Lsp, edit, asked?.Since, patience);

    /// <summary>What the status bar says of a command left unrun: one its server does not run itself (<see cref="LanguageServer.Runs"/>).</summary>
    internal static string NotRun(string what, LspCommand command) => $"{what} needs {command.Command}, which Passenger cannot run";

    /// <summary>A refactoring by its own key, over the selection: the language server's action of that kind applied at
    /// once, as the work of the action <paramref name="id"/>, and for Extract Method its new method's name then offered to
    /// rename.</summary>
    public async Task Refactor(Func<CodeAction, bool> pick, string name, bool renameMethod, string id)
    {
        if (context.Ask(name) is not { } lsp || context.Editors.Active is not { } document)
        {
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            Selection primary = Editor.Selections.Primary;
            using Asked asked = WorkspaceEdits.Track(context.Editors);
            using ExtractedMethod? made = renameMethod && document.Syntax is { } syntax ? new ExtractedMethod(Editor.Document, syntax, primary) : null;
            CodeAction? action = (await lsp.CodeActionsAsync(primary.Range, [])).Where(a => a.Disabled is null).FirstOrDefault(pick);
            if (action is null)
            {
                context.Say($"{name} is not available here{(primary.IsEmpty && renameMethod ? ": select the code first" : string.Empty)}");
                return;
            }
            // Choices the server nests under the action (Introduce Variable's occurrences) are offered as a list first.
            await Carry(document, action, primary.Caret, name, id, asked, () =>
            {
                context.Say($"{name} done");
                // The call that replaced the selection is where the new name is typed, in the file still shown.
                if (made is not null && made.Document == Editor.Document && made.Call() is { } call)
                {
                    Editor.Selections = SelectionSet.Single(call);
                    Rename();
                }
            });
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(name, failed);
        }
    }

    /// <summary>Rename's options as they were left: whether a type's files and a file's type are renamed with it.</summary>
    public RenameSettings RenameSettings
    {
        get => renameSettings;
        set
        {
            if (value != renameSettings)
            {
                renameSettings = value;
                RenameSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private RenameSettings renameSettings = new();

    /// <summary>An option of Rename's turned on or off, for the window to keep.</summary>
    public event EventHandler? RenameSettingsChanged;

    /// <summary>Rename at the editor's caret, as the shown file's language renames: a XAML name, a compose file's, a JSON
    /// property, an Aspire resource's of its own, else through the language server.</summary>
    public void Rename() => context.Languages.Rename(context.Caret);

    /// <summary>Rename through the language server at the editor's caret, a type's files offered with it.</summary>
    public void ShowServerRename()
    {
        if (context.Ask("Rename") is null || context.Editors.Active is not { } document)
        {
            return;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        TextRange word = RenamedAt(document, text, caret);
        if (word.Length == 0 || !(TextNavigation.IsWordChar(text[word.Start]) || (context.WebCurrent is { } named && WebAssist.IsWebWordChar(named, text[word.Start]))))
        {
            return;
        }
        RenameBox box = new(text.GetText(word));
        box.RenamesFilesChanged += (_, on) => RenameSettings = RenameSettings with { RenameFiles = on };
        ChangeTracker opened = new(Editor.Document);
        popups.ShowModal(box, word.Start, async name =>
        {
            // Let go as the box closed, just before: what was typed while it was open is still mapped.
            int at = opened.MapOffset(word.Start);
            if (name is null)
            {
                return;
            }
            // Held on the name through what is typed while the rename is out, however often it is worked out.
            Anchor held = HeldAt(document, at);
            try
            {
                RefactorOutcome made = await context.PatientlyAsync(AppActions.Rename, patience => RenameSymbolAsync(document, held, name, box.RenamesFiles, patience));
                context.Say(made.Problem ?? made.Said);
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Rename", failed);
            }
            finally
            {
                held.Remove();
            }
        });
        popups.WhenClosed(opened.Dispose);
        IdeErrors.Observe(OfferFileRenames(box, document, word.Start));
    }

    /// <summary>Whether a name can be written as a C# identifier: as it is, or after an @ (a keyword's name).</summary>
    private static bool IsCSharpName(string name) => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name.StartsWith('@') ? name[1..] : name);

    /// <summary>A place in an open file a rename starts from, held on its name through what is typed until the server is
    /// asked: what is typed at the name's start goes before it, and at its end or inside after it. Its holder removes it.</summary>
    internal Anchor HeldAt(OpenDocument document, int offset) =>
        document.Text.AnchorAt(offset, RenamedAt(document, document.Text.Snapshot, offset).Start == offset ? AnchorGravity.After : AnchorGravity.Before);

    /// <summary>The name a rename at an offset of a file renames: a web language's through its own characters (CSS's
    /// --accent, JavaScript's $el).</summary>
    private TextRange RenamedAt(OpenDocument document, TextSnapshot text, int offset) =>
        context.WebOf(document) is { } web ? WebAssist.WebWordAt(web, text, offset) : TextNavigation.WordAt(text, offset);

    /// <summary>A name renamed through the language server at an offset of a file, a type's files named after it renamed
    /// with it when <paramref name="renameFiles"/> and the markup its class is made with following it
    /// (<see cref="TypeRename.MarkupAsync"/>), as one command of global undo: the window's Rename and an agent's alike. Why
    /// not, else the files it changed and moved, those it left as edited meanwhile, and the status bar's words, once the
    /// files not open it wrote have landed; a refusal for files the server had yet to read is told its <paramref name="patience"/>.</summary>
    internal async Task<RefactorOutcome> RenameSymbolAsync(OpenDocument document, int offset, string name, bool renameFiles, Patience? patience)
    {
        // Held on the name through what is typed while the rename is out.
        Anchor at = HeldAt(document, offset);
        try
        {
            return await RenameSymbolAsync(document, at, name, renameFiles, patience);
        }
        finally
        {
            at.Remove();
        }
    }

    /// <summary>A name renamed from a place held on it (<see cref="HeldAt"/>), the server asked where the place is then.</summary>
    private async Task<RefactorOutcome> RenameSymbolAsync(OpenDocument document, Anchor at, string name, bool renameFiles, Patience? patience)
    {
        if (await context.TakenInAsync(document, () => at.Offset) is { } unloaded)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {unloaded}");
        }
        // A named element's field is made from its XAML, so it is renamed as the element is, there and in the code-behind.
        if (document.Language == Languages.Language.CSharp
            && await context.Languages.Xaml.NamedElementOfAsync(document, await context.Now(document).DefinitionAsync(at.Offset), at.Offset) is { Path: { } elementFile } element)
        {
            return element.Name == name ? new RefactorOutcome($"{name} is its name already", null, [], [])
                : await context.Languages.Xaml.XamlRenameAsync(elementFile, element.Name, name) ?? RefactorOutcome.Refused($"{Path.GetFileName(elementFile)} is not XAML Passenger reads");
        }
        // A component's file at its start is the class Razor makes of it, which the server takes in the C# made of the file.
        TypeRename.Component? component = context.WebOf(document) is null
            ? await TypeRename.ComponentAtAsync(context.CSharp?.Session, document.Path, document.Text.Snapshot, at.Offset) : null;
        // Asked before the rename, where the type is declared still.
        TypeRename.Named? type = component?.Type ?? (context.WebOf(document) is null && context.CSharp?.Session?.Folder is { } folder
            ? await TypeRename.NamedAsync(context.Editors, folder, context.Now(document), at.Offset) : null);
        TextSnapshot text = document.Text.Snapshot;
        string old = component?.Type.Type ?? text.GetText(RenamedAt(document, text, at.Offset));
        if (context.WebOf(document) is null && !IsCSharpName(name))
        {
            return RefactorOutcome.Refused($"'{name}' is not a C# identifier, so {old} keeps its name");
        }
        // The open files as the request went: what is typed meanwhile carries the answer on.
        using Asked asked = WorkspaceEdits.Track(context.Editors);
        // Asked of the server running now: one there before may have been started again since.
        System.Text.Json.JsonElement? renamed = component is null ? await context.Now(document).RenameAsync(at.Offset, name)
            : context.CSharp?.Session is { } session ? await session.RenameAsync(component.Name, name) : null;
        if (renamed is not { } edit)
        {
            // Asked a moment after a file it reads from disk was written (a XAML file making a field), the server may not know the name yet.
            return await StaleAsync(document, NoEdit, asked, patience) is { } unread ? RefactorOutcome.Refused($"Rename was not applied: {unread}")
                : new RefactorOutcome("This can't be renamed", null, [], []);
        }
        TypeRename.Markup markup = type is null ? new([], null) : await TypeRename.MarkupAsync(context.Xaml?.Invoke(), type, name, renameFiles);
        if (await StaleAsync(document, edit, asked, patience) is { } stale)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {stale}");
        }
        Func<string, TextSnapshot?> answered = WorkspaceEdits.Answered(context.Editors, asked);
        if (markup.ClassFile is { } xaml && type?.FullName is { } fullName && TypeRename.ClassEdit(xaml, answered(xaml), fullName, name) is { } named)
        {
            edit = TypeRename.With(edit, named.Path, named.Edit);
        }
        IReadOnlyList<RefactoredFile> edited = await WorkspaceEdits.FilesAsync(context.Editors, edit, asked);
        List<(string From, string To)> moves = [.. (renameFiles ? type?.Files ?? [] : []).Select(f => (From: f, To: TypeRename.Renamed(f, name))).Concat(markup.Moves)
            .Where(m => m.From != m.To && File.Exists(m.From) && !edited.Any(f => f.Change == "moved" && PathNames.Same(f.Path, m.From))).Distinct()];
        List<string> dropped = [];
        string command = $"Rename {old} to {name}";
        using CommandHistory.Recording recording = context.Editors.Commands.Record(command);
        TypeRename.Applied applied;
        try
        {
            applied = TypeRename.Apply(context.Editors, edit, moves, asked, dropped, recording);
            // Written whether or not every file moved: the code is renamed either way.
            await WorkspaceEdits.LandedAsync(recording, context.Say);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"Rename failed: {failed.Message}");
        }
        List<RefactoredFile> changed = [.. edited.Where(f => !(context.Editors.Find(f.Path) is { } open && dropped.Contains(open.Name)))];
        string movedSaid = applied.Moved.Count > 0 ? $"; {string.Join(", ", applied.Moved.Select(m => $"{Path.GetFileName(m.From)} to {Path.GetFileName(m.To)}"))}" : string.Empty;
        string said = dropped.Count > 0 ? $"Renamed, but not in {string.Join(", ", dropped)}: it was edited meanwhile"
            : (changed.Count > 1 ? $"Renamed in {changed.Count} files" : "Renamed") + movedSaid;
        if (applied.Unmoved is { } unmoved)
        {
            said += $"{(dropped.Count > 0 ? "; and" : ", but")} {unmoved}";
        }
        return new RefactorOutcome(said, null, [.. changed, .. applied.Moved.Select(m => new RefactoredFile(m.From, "moved", [], m.To))], dropped);
    }

    /// <summary>Also rename files to reflect this change, offered in the open rename box once the server says the name
    /// is a type's declared in files named after it.</summary>
    private async Task OfferFileRenames(RenameBox box, OpenDocument document, int offset)
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            if (context.WebOf(document) is null && context.CSharp?.Session?.Folder is { } folder && await TypeRename.NamedAsync(context.Editors, folder, context.Now(document), offset) is { Files.Count: > 0 } named
                && popups.Modal == box)
            {
                box.OfferFiles(named.Files, RenameSettings.RenameFiles);
                popups.Place(box, offset, above: true);
            }
        }
        catch (LanguageServerFailed)
        {
            // The rename asks the same server, and says why it could not.
        }
    }

    public async Task Reformat()
    {
        using (BackgroundWork.Scope())
        {
            await ReformatCore();
        }
    }

    private async Task ReformatCore()
    {
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        try
        {
            if (await ReformatAsync(document, Selected(document), () => context.Ask("Reformat Code")) is { } done)
            {
                context.Say(done.Said);
            }
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Reformat Code", failed);
        }
    }

    /// <summary>What Reformat Code came to: whether it was applied, whether it changed the text (not when the file was
    /// formatted already), and what to say of it, or why it was not applied.</summary>
    internal sealed record Reformatted(bool Applied, bool Changed, string Said);

    /// <summary>Reformat Code on a file, the window's and an agent's alike, as one undoable edit, only <paramref name="within"/>
    /// when given: XAML by Passenger's own layout, a web language by its project's Prettier where it formats it on Reformat
    /// Code, else the language <paramref name="server"/> gives, asked for only then. Null with no server to ask, or once
    /// the file has closed.</summary>
    internal async Task<Reformatted?> ReformatAsync(OpenDocument document, TextRange? within, Func<LspDocument?> server)
    {
        within = within is { } range ? WholeLines(document.Text.Snapshot, range) : null;
        if (await context.Languages.FormatAsync(document, within) is { } done)
        {
            return done;
        }
        return server() is { } lsp ? await ServerFormatAsync(lsp, document, "Reformat Code", within) : null;
    }

    /// <summary>A selection ending at a line's start, as one of whole lines does, kept to the lines before it: the line it
    /// ends at is not among those to lay out.</summary>
    private static TextRange WholeLines(TextSnapshot text, TextRange range)
    {
        int line = text.GetLineFromOffset(range.End);
        return line > 0 && range.End == text.GetLineStart(line) && text.GetLine(line - 1).End > range.Start
            ? TextRange.FromBounds(range.Start, text.GetLine(line - 1).End)
            : range;
    }

    /// <summary>The language server's formatting of a file, or of <paramref name="within"/> where the server formats a
    /// range (else the whole file, said), carried through what was typed as it worked, applied; null once the file has
    /// closed.</summary>
    private async Task<Reformatted?> ServerFormatAsync(LspDocument lsp, OpenDocument document, string what, TextRange? within = null)
    {
        using ChangeTracker asked = new(document.Text);
        bool whole = within is null || !lsp.FormatsRanges;
        IReadOnlyList<LspTextEdit> answer = within is { } range && !whole
            ? await lsp.FormatAsync(range, document.Indentation.Options, document.Conventions)
            : await lsp.FormatAsync(document.Indentation.Options, document.Conventions);
        if (!context.Editors.Documents.Contains(document))
        {
            return null;
        }
        List<TextEdit> laid = WorkspaceEdits.Convert(asked.Snapshot, answer, asked.Document.LineEnding, document.Indentation.Options);
        if (within is { } kept && !whole)
        {
            // Only the selection's own lines are laid out: the line after it keeps its indentation.
            laid.RemoveAll(e => e.Start >= kept.End);
        }
        if (asked.Map(laid) is not { } edits)
        {
            return new Reformatted(false, false, $"{what} was not applied: the code it changes was edited meanwhile");
        }
        if (edits.Count > 0)
        {
            WorkspaceEdits.ApplyTo(context.Editors, document, edits);
        }
        // The server's edits that come to nothing in the file's line breaks and indentation are no reformat.
        string said = edits.Count == 0 ? "Already formatted" : "Reformatted";
        return new Reformatted(true, edits.Count > 0, within is not null && whole ? $"{said} the whole file: its language server formats no selection" : said);
    }

    /// <summary>The selection a file's reformatting keeps to: the editor's, when it shows the file and something is selected.</summary>
    private TextRange? Selected(OpenDocument document) =>
        context.Editors.Active == document && !Editor.Selections.Primary.IsEmpty ? Editor.Selections.Primary.Range : null;

    public async Task Cleanup()
    {
        using (BackgroundWork.Scope())
        {
            await CleanupCore();
        }
    }

    /// <summary>Code Cleanup on the file, through the language server's own fixes: the unnecessary usings removed
    /// (IDE0005's fix), the usings sorted, then the file reformatted.</summary>
    private async Task CleanupCore()
    {
        // Code Cleanup on XAML reformats it, by the same code style.
        if (context.Editors.Active is { } shown && XamlService.IsXaml(shown.Path))
        {
            Reformatted done = await context.Languages.Xaml.ReformatXamlAsync(shown, Selected(shown), "Code Cleanup");
            context.Say(!done.Applied ? done.Said : done.Changed ? "Code cleanup reformatted" : "Already clean");
            return;
        }
        if (context.Ask("Code Cleanup") is not { } lsp || context.Editors.Active is not { } document)
        {
            return;
        }
        try
        {
            context.Say(CleanedUp(await CleanUpAsync(lsp, document)));
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Code Cleanup", failed);
        }
    }

    /// <summary>What Code Cleanup says it did to a file.</summary>
    public static string CleanedUp(IReadOnlyList<string> done) => done.Count == 0 ? "Already clean" : $"Code cleanup {string.Join(", ", done)}";

    /// <summary>Code Cleanup on a C# file: the unnecessary usings removed, the usings sorted, then the file reformatted;
    /// what it did, in the status bar's words ("removed unused usings"), none when the file was clean.</summary>
    public async Task<IReadOnlyList<string>> CleanUpAsync(LspDocument lsp, OpenDocument document)
    {
        List<string> done = [];
        (bool removed, bool sorted) = await TidyUsings(lsp, document);
        if (removed)
        {
            done.Add("removed unused usings");
        }
        if (sorted)
        {
            done.Add("sorted the usings");
        }
        string unformatted = document.Text.Snapshot.ToString();
        if (await ServerFormatAsync(lsp, document, "Code Cleanup") is { } formatted)
        {
            if (!formatted.Applied)
            {
                context.Say(formatted.Said);
            }
            else if (document.Text.Snapshot.ToString() != unformatted)
            {
                done.Add("reformatted");
            }
        }
        return done;
    }

    /// <summary>Runs the server's action of that title at a range, if it offers one; whether it did.</summary>
    private async Task<bool> ApplyNamed(LspDocument lsp, OpenDocument document, TextRange range, IReadOnlyList<Diagnostic> diagnostics, string title)
    {
        using Asked asked = WorkspaceEdits.Track(context.Editors);
        IReadOnlyList<CodeAction> actions = await lsp.CodeActionsAsync(range, diagnostics);
        if (actions.FirstOrDefault(a => a.Title == title && a.Disabled is null) is not { } action)
        {
            return false;
        }
        return await Apply(document, action, null, title, asked, null);
    }

    /// <summary>A workspace edit applied, its open files' edits carried through what was typed since the request, as one
    /// command that Undo takes back whole (a fix all's files not open with it); one whose edited text changed meanwhile is
    /// left alone and named in the status bar. Whether every file took it, once those not open have landed.</summary>
    internal async Task<bool> ApplyWorkspaceAsync(System.Text.Json.JsonElement edit, Asked? asked, string what)
    {
        RefactorOutcome made = await ApplyEditAsync(edit, asked, what);
        if (made.Problem is not null)
        {
            context.Say(made.Problem);
            return false;
        }
        if (made.Left.Count > 0)
        {
            context.Say($"{what} was not applied to {string.Join(", ", made.Left)}: it was edited meanwhile");
        }
        return made.Left.Count == 0;
    }

    /// <summary><see cref="ApplyWorkspaceAsync"/>'s change made, once the files it wrote have landed: the files it changed
    /// and those it left, with that it is done when the status bar said it was waiting for one, or why not.</summary>
    private async Task<RefactorOutcome> ApplyEditAsync(System.Text.Json.JsonElement edit, Asked? asked, string what)
    {
        List<string> dropped = [];
        IReadOnlyList<RefactoredFile> files = await WorkspaceEdits.FilesAsync(context.Editors, edit, asked);
        bool waited;
        using CommandHistory.Recording recording = context.Editors.Commands.Record(what);
        try
        {
            WorkspaceEdits.ApplyAsCommand(context.Editors, edit, recording, asked, dropped);
            waited = await WorkspaceEdits.LandedAsync(recording, context.Say);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {failed.Message}");
        }
        return new RefactorOutcome(waited ? $"{what} done" : string.Empty, null, files, dropped);
    }
}
