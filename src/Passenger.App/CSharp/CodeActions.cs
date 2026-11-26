using System.Diagnostics;
using Avalonia;
using Passenger.App.Editors;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.WinForms;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>One of Passenger's own refactorings as Refactor This lists it: its title and keys, what running it says where it
/// could not run (null once it ran), and the server's actions it stands for (<see cref="CodeActions.ServersOf"/>).</summary>
internal sealed record OwnRefactoring(string Title, string Keys, Func<string?> Run, (Func<CodeAction, bool> Actions, bool Made)? Servers);

/// <summary>Code actions and refactorings: Show Context Actions (Passenger's own fixes first, then ESLint's and the language
/// server's), Generate and Refactor This, the refactorings by their own keys and dialogs, Rename, Reformat Code, Code
/// Cleanup and Optimize Imports, each change one command that Undo takes back whole.</summary>
internal sealed partial class CodeActions(AssistContext context, PopupPlacement popups, CodeNavigation navigation)
{
    private CodeEditor Editor => context.Editor;

    /// <summary>The fixes and refactorings at the caret (those <paramref name="only"/> takes), listed under
    /// <paramref name="title"/>, the one chosen made as the work of the action <paramref name="id"/>; in a C# file, Passenger's
    /// own <paramref name="refactorings"/> first, those the server's own actions make only where it offers them, and none of
    /// the server's actions one of them stands for.</summary>
    public async Task ShowFixes(string id, Func<CodeAction, bool>? only = null, string title = "Fixes and refactorings", IReadOnlyList<OwnRefactoring>? refactorings = null)
    {
        using (BackgroundWork.Scope())
        {
            await ShowFixesCore(only, title, id, refactorings ?? []);
        }
    }

    /// <summary>The fixes Passenger offers itself at an offset of a file, which Show Context Actions lists before asking the
    /// language server; none by default.</summary>
    public Func<OpenDocument, int, IReadOnlyList<EditorFix>>? EditorFixes { get; set; }

    /// <summary>Asks the person for a value a fix sets (a title, what is asked, whether it is a secret, masked as typed);
    /// null when they gave none.</summary>
    public Func<string, string, bool, Task<string?>>? AskValue { get; set; }

    /// <summary>Asks the person to agree to a fix (a title, what is asked, the button that agrees); whether they did.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmFix { get; set; }

    /// <summary>The fixes Passenger offers itself at an offset of a file that a language server is asked for first (a fake of
    /// the type there), listed after <see cref="EditorFixes"/>; none by default.</summary>
    public Func<OpenDocument, int, Task<IReadOnlyList<EditorFix>>>? EditorFixesAsked { get; set; }

    internal IReadOnlyList<EditorFix> OwnFixes(OpenDocument? file, int offset) =>
        file is null ? [] : [.. EditorFixes?.Invoke(file, offset) ?? [], .. context.Languages.Fixes(file, offset)];

    /// <summary>Passenger's own fixes at an offset of a file (<see cref="OwnFixes"/>) once each language's reading of it is
    /// up to its text: what Show Context Actions and an agent's code_actions both list first.</summary>
    internal async Task<IReadOnlyList<EditorFix>> OwnFixesAsync(OpenDocument? file, int offset)
    {
        if (file is null)
        {
            return [];
        }
        await context.Languages.ReadyAsync(file);
        IReadOnlyList<EditorFix> own = OwnFixes(file, offset);
        return EditorFixesAsked is { } asked ? [.. own, .. await asked(file, offset)] : own;
    }

    /// <summary>Some of Passenger's own fixes listed as Show Context Actions lists them, under a title, the one chosen made.</summary>
    internal void ShowOwnFixes(string title, OpenDocument document, IReadOnlyList<EditorFix> fixes) =>
        popups.ShowModal(new ChoicePopup(title, [.. fixes.Select(f => new Choice(f.Title, f.Detail, f))]), Editor.Selections.Primary.Caret, title,
            choice => choice?.Value is EditorFix fix ? ApplyOwnFix(document, fix) : Task.CompletedTask);

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
        if (now.Confirms is { } confirms)
        {
            IdeErrors.Observe(ConfirmAndMakeAsync(now.Title.TrimEnd('…'), confirms));
            return;
        }
        if (now.Edits is { } edits)
        {
            WorkspaceEdits.ApplyTo(context.Editors, document, edits);
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

    /// <summary>What a refactoring came to said, or where it was refused only for uses it can't see, the person asked
    /// whether to make it anyway (<see cref="RefactorOutcome.Anyway"/>).</summary>
    private void Told(string title, RefactorOutcome made)
    {
        if (made.Anyway is { } anyway && ConfirmFix is not null)
        {
            // Not awaited: the question holds no background work, so a settle waits on the IDE, never on the person.
            IdeErrors.Observe(ConfirmAndMakeAsync(title, anyway));
            return;
        }
        context.Say(made.Problem ?? made.Said);
    }

    /// <summary>A fix's question asked of the person (<see cref="ConfirmFix"/>) as the files stand, then the fix made as
    /// background work once they agree, what it came to said; nothing when they do not.</summary>
    private async Task ConfirmAndMakeAsync(string title, FixConfirmed confirms)
    {
        try
        {
            string? question;
            using (BackgroundWork.Scope())
            {
                question = await confirms.Question();
            }
            if (question is null)
            {
                context.Say($"{title} was not applied: the file changed, so it no longer fits");
                return;
            }
            if (ConfirmFix is null || !await ConfirmFix(title, question, confirms.Yes))
            {
                return;
            }
            using (BackgroundWork.Scope())
            {
                RefactorOutcome made = await confirms.Make(null);
                context.Say(made.Problem ?? made.Said);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    private async Task ShowFixesCore(Func<CodeAction, bool>? only, string title, string id, IReadOnlyList<OwnRefactoring> refactorings)
    {
        if (context.XamlCurrent is { } xaml)
        {
            await context.Languages.Xaml.FixesAsync(this, xaml, only is null ? OwnFixes(context.Editors.Active, context.Caret) : [], refactor: only is not null);
            return;
        }
        string what = only is null ? "Show Context Actions" : title;
        // Passenger's own fixes first, which need no server (a compose file's are its container engine's).
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        IReadOnlyList<EditorFix> own = only is null ? await OwnFixesAsync(document, context.Caret) : [];
        if (context.Editors.Active != document)
        {
            return;
        }
        Selection primary = Editor.Selections.Primary;
        IReadOnlyList<LintFix> linted = [];
        IReadOnlyList<CodeAction> actions = [];
        List<OwnRefactoring> listed = [];
        if (context.Ask(own.Count > 0 ? null : what) is { } lsp && context.Current is { } current)
        {
            int line = lsp.PositionOf(primary.Caret).Line;
            List<Diagnostic> here = current.Diagnostics
                .Where(d => d.Range.Start.Line <= line && line <= d.Range.End.Line)
                .ToList();
            bool csharp = current is CSharpDocument { Lsp.IsRazor: false };
            Task<IReadOnlyList<EditorFix>> pulls = only is null && csharp ? PullsIntoInterfacesAsync(lsp, document, primary) : NoFixes;
            LanguageServerFailed? unanswered = null;
            try
            {
                // The linters' fixes for the line first, before the language's own.
                linted = only is null ? await context.Languages.Web.LintFixes(document, primary.Caret) : [];
                actions = [.. (await lsp.CodeActionsAsync(primary.Range, here)).Where(a => a.Disabled is null && (only?.Invoke(a) ?? true))];
            }
            catch (LanguageServerFailed failed)
            {
                unanswered = failed;
            }
            own = [.. own, .. await pulls];
            if (unanswered is not null && own.Count == 0)
            {
                context.Failed(what, unanswered);
                return;
            }
            // Passenger's own refactorings are C#'s, never a Razor file's.
            listed = csharp ? [.. refactorings.Where(r => r.Servers is not ({ } made, true) || actions.Any(made))] : [];
            actions = [.. actions.Where(a => !listed.Any(r => r.Servers?.Actions(a) == true))];
        }
        else if (own.Count == 0)
        {
            return;
        }
        if (own.Count == 0 && listed.Count == 0 && actions.Count == 0 && linted.Count == 0)
        {
            context.Say(only is null ? "No fixes or refactorings here" : "No refactorings here");
            return;
        }
        List<Choice> choices = [.. own.Select(f => new Choice(f.Title, f.Detail, f)), .. listed.Select(r => new Choice(r.Title, r.Keys, r)),
            .. linted.Select(f => new Choice(f.Title, CodeActionKinds.Shown(f.Action.Kind), f)), .. actions.Select(a => new Choice(a.Title, CodeActionKinds.Shown(a.Kind), a))];
        popups.ShowModal(new ChoicePopup(title, choices), primary.Caret, what, choice =>
        {
            switch (choice?.Value)
            {
                case EditorFix fix:
                    return ApplyOwnFix(document, fix);
                case OwnRefactoring refactoring:
                    if (refactoring.Run() is { } why)
                    {
                        context.Say(why);
                    }
                    return Task.CompletedTask;
                case LintFix fix:
                    return context.Languages.Web.RunLintFix(document, fix);
                case CodeAction chosen:
                    return Carry(document, chosen, primary.Caret, chosen.Title, id, null);
                default:
                    return Task.CompletedTask;
            }
        });
    }

    private static readonly Task<IReadOnlyList<EditorFix>> NoFixes = Task.FromResult<IReadOnlyList<EditorFix>>([]);

    /// <summary>Pull Members Up of the member whose name the caret is on into each interface its class implements that
    /// lacks it, offered as fixes made as Pull Members Up makes them, with no dialog; none where the extension finds none,
    /// or cannot be asked.</summary>
    private async Task<IReadOnlyList<EditorFix>> PullsIntoInterfacesAsync(LspDocument lsp, OpenDocument document, Selection primary)
    {
        if (!primary.IsEmpty)
        {
            return [];
        }
        MemberPull pull;
        try
        {
            pull = await lsp.ExtensionAsync<MemberPull>(Handlers.InterfacesLacking, new Roslyn.Extension.Caret(lsp.PlaceOf(primary.Caret)));
        }
        catch (LanguageServerFailed)
        {
            // The server's own fixes are listed without them.
            return [];
        }
        if (pull.Member is not { } member)
        {
            return [];
        }
        Hierarchy hierarchy = new(null, pull.Type, [member], pull.Interfaces, []);
        return [.. pull.Interfaces.Select(into => new EditorFix($"Pull '{pull.Name}' up into '{into.Text}'", "Pull Members Up", new TextRange(primary.Caret, 0), string.Empty)
        {
            Elsewhere = (at, patience) => MoveMembersAsync(document, at.Start, hierarchy, up: true, into.Id, [member.Id], [], patience),
        })];
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
            context.Say(Refusals.NotAppliedEditedMeanwhile(what, made.Left));
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
        RefactorOutcome made = chosen.Edit is { } edit ? await ApplyEditAsync(edit, asked, what, what) : new RefactorOutcome(string.Empty, null, [], []);
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
    /// the C# server is asked what it holds of them, and of the files it answered from that it reads from disk (<paramref name="read"/>);
    /// another server's files are judged by when they changed alone.</summary>
    private Task<string?> StaleAsync(OpenDocument document, System.Text.Json.JsonElement edit, Asked? asked, Patience? patience = null,
        IReadOnlyCollection<string>? read = null) =>
        WorkspaceEdits.StaleAsync(context.Editors, context.CSharp?.For(document)?.Lsp, edit, asked?.Since, patience, read: read);

    /// <summary>The XAML whose <c>x:Class</c> is the class around an offset of a C# file, which the server reads from disk to
    /// make that class's fields of its named elements; none outside such a class.</summary>
    private async Task<IReadOnlyList<string>> MarkupOfClassAtAsync(OpenDocument document, int offset) =>
        document.Language == Languages.Language.CSharp && TypeRename.ClassAround(document, offset) is { } className && context.Xaml?.Invoke() is { } service
            && await service.ClassFileAsync(className, document.Path) is { } xaml ? [xaml] : [];

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
        if (context.Ask("Rename") is not { } served || context.Editors.Active is not { } document)
        {
            return;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        TextRange word = RenamedAt(document, text, caret);
        if (word.Length == 0 || !(WordCharacters.IsWord(text[word.Start]) || (context.WebCurrent is { } named && WebAssist.IsWebWordChar(named, text[word.Start]))))
        {
            return;
        }
        RenameBox box = ShowRenameBox(document, text, word);
        box.RenamesFilesChanged += (_, on) => RenameSettings = RenameSettings with { RenameFiles = on };
        IdeErrors.Observe(OfferFileRenames(box, document, word.Start));
        if (document.Language == Languages.Language.CSharp && !served.IsRazor && popups.Modal == box)
        {
            IdeErrors.Observe(OfferRenameSuggestionsAsync(box, document, served, text, word.Start));
        }
    }

    private async Task OfferRenameSuggestionsAsync(RenameBox box, OpenDocument document, LspDocument served, TextSnapshot text, int offset)
    {
        using IDisposable held = BackgroundWork.Scope();
        using CancellationTokenSource asking = new();
        void Closed(object? sender, VisualTreeAttachmentEventArgs e) => asking.Cancel();
        box.DetachedFromVisualTree += Closed;
        try
        {
            string[] suggestions = await served.RenameSuggestionsAsync(offset, asking.Token);
            if (popups.Modal == box && context.Editors.Active == document && document.Text.Snapshot == text)
            {
                box.OfferSuggestions(suggestions);
            }
        }
        catch (OperationCanceledException) when (asking.IsCancellationRequested)
        {
        }
        catch (LanguageServerFailed)
        {
            // Suggestions may be unavailable while the input is already usable.
        }
        finally
        {
            box.DetachedFromVisualTree -= Closed;
        }
    }

    /// <summary>The rename box over a name of a file, C#'s, a web language's or a GDScript's, renamed through its server or
    /// Passenger's reading of the scripts (<see cref="AskRename(OpenDocument, int, RenameBox, Func{Anchor, string, Patience, Task{RefactorOutcome}})"/>),
    /// with a type's files where the box offers them and they are chosen.</summary>
    private RenameBox ShowRenameBox(OpenDocument document, TextSnapshot text, TextRange word)
    {
        RenameBox box = new(text.GetText(word));
        AskRename(document, word.Start, box, (held, name, patience) => RenameSymbolAsync(document, held, name, box.RenamesFiles, patience));
        return box;
    }

    /// <summary>Rename's box opened on a name at an offset of a file: once a new name is given, <paramref name="rename"/> renames
    /// it from a place held on the name through what is written meanwhile, while the box is open and as the rename is worked
    /// out, made again as <see cref="AssistContext.PatientlyAsync(string, Func{Patience, Task{RefactorOutcome}})"/> makes a
    /// change, and what it came to said; the name given unchanged renames nothing. A server's failure is said by the box's
    /// answer (<see cref="PopupPlacement.Carried"/>).</summary>
    internal void AskRename(OpenDocument document, int at, RenameBox box, Func<Anchor, string, Patience, Task<RefactorOutcome>> rename)
    {
        string old = box.Text;
        ChangeTracker opened = new(document.Text);
        popups.ShowModal(box, at, async name =>
        {
            // Let go as the box closed, just before: what was written while it was open is still mapped.
            int now = opened.MapOffset(at);
            if (name is null || name == old)
            {
                return;
            }
            // Held on the name through what is written while the rename is out, however often it is worked out.
            Anchor held = HeldAt(document, now);
            try
            {
                Told("Rename", await context.PatientlyAsync(AppActions.Rename, patience => rename(held, name, patience)));
            }
            finally
            {
                held.Remove();
            }
        });
        popups.WhenClosed(opened.Dispose);
    }

    /// <summary>Rename's box opened on the name <paramref name="old"/> at an offset of a file (<see cref="AskRename(OpenDocument, int, RenameBox, Func{Anchor, string, Patience, Task{RefactorOutcome}})"/>).</summary>
    internal void AskRename(OpenDocument document, int at, string old, Func<Anchor, string, Patience, Task<RefactorOutcome>> rename) =>
        AskRename(document, at, new RenameBox(old), rename);

    /// <summary>Whether a name can be written as a C# identifier: as it is, or after an @ (a keyword's name).</summary>
    private static bool IsCSharpName(string name) => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name.StartsWith('@') ? name[1..] : name);

    /// <summary>A place in an open file a rename starts from, held on its name through what is typed until the server is
    /// asked: what is typed at the name's start goes before it, and at its end or inside after it. Its holder removes it.</summary>
    internal Anchor HeldAt(OpenDocument document, int offset) =>
        document.Text.AnchorAt(offset, RenamedAt(document, document.Text.Snapshot, offset).Start == offset ? AnchorGravity.After : AnchorGravity.Before);

    /// <summary>The name a rename at an offset of a file renames: a web language's through its own characters (CSS's
    /// --accent, JavaScript's $el).</summary>
    internal TextRange RenamedAt(OpenDocument document, TextSnapshot text, int offset) =>
        context.WebOf(document) is { } web ? WebAssist.WebWordAt(web, text, offset) : TextNavigation.WordAt(text, offset);

    /// <summary>The character just before a renamed name that the server's edit of it in <paramref name="path"/> replaces
    /// too (a '.', '#', '$' or '@'), where <paramref name="name"/> does not begin with it; else null.</summary>
    internal static char? SigilOf(System.Text.Json.JsonElement edit, string path, TextSnapshot text, TextRange word, string name)
    {
        if (word.Start == 0 || word.Length == 0)
        {
            return null;
        }
        char before = text[word.Start - 1];
        if (AssistContext.IsIdentifierChar(before) || char.IsWhiteSpace(before) || name.StartsWith(before))
        {
            return null;
        }
        foreach (FileEdits file in Shapes.WorkspaceEdit(edit).Where(f => f.Operation is null && PathNames.Same(DocumentUris.ToPath(f.Uri), path)))
        {
            foreach (LspTextEdit change in file.Edits)
            {
                if (text.GetOffset(ServerPlaces.PositionOf(text, change.Range.Start)) == word.Start - 1
                    && text.GetOffset(ServerPlaces.PositionOf(text, change.Range.End)) == word.End)
                {
                    return before;
                }
            }
        }
        return null;
    }

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

    /// <summary>What the folder's C# declares at <paramref name="declared"/> (Roslyn's line and column), read as
    /// <paramref name="written"/>, its own name or the property or a command the MVVM Toolkit makes of it, renamed from its
    /// declaration (<see cref="RenameSymbolAsync(OpenDocument, int, string, bool, Patience?)"/>) so it is read as
    /// <paramref name="name"/>: a field or method by the name that makes it, refused where none would. Its file is read for
    /// the rename where it is not open.</summary>
    internal async Task<RefactorOutcome> RenameDeclaredAsync(Passenger.Xaml.Metadata.SourcePlace declared, string written, string name, bool renameFiles, Patience? patience)
    {
        OpenDocument code;
        IDisposable hold;
        try
        {
            (code, hold) = context.Editors.Lend(declared.Path);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {Path.GetFileName(declared.Path)} could not be read: {unread.Message}");
        }
        try
        {
            if (context.CSharp?.For(code)?.Lsp is not { } lsp)
            {
                return RefactorOutcome.Refused($"Rename was not applied: the C# language server has yet to read {Path.GetFileName(declared.Path)}");
            }
            int offset = lsp.OffsetOf(new LspPosition(declared.Line, declared.Column));
            TextSnapshot text = code.Text.Snapshot;
            string own = text.GetText(TextNavigation.WordAt(text, offset));
            if (Passenger.Xaml.Features.CodeSymbol.NameFor(own, written, name) is not { } renamed)
            {
                return RefactorOutcome.Refused($"Rename was not applied: a source generator makes {written} of {own}, and no name of {own}'s is known to make '{name}'");
            }
            return renamed == own ? new RefactorOutcome($"{name} is its name already", null, [], [])
                : await RenameSymbolAsync(code, offset, renamed, renameFiles, patience);
        }
        finally
        {
            hold.Dispose();
        }
    }

    /// <summary>A component of a Windows Forms class's designer code renamed from <paramref name="old"/> to <paramref name="name"/>,
    /// where its field is declared: the string its Name is set to and its section's comment (<see cref="DesignerWriter.Renamed"/>),
    /// placed in the text they were read from; none for any other field.</summary>
    private List<PlacedEdits> DesignedRenames(IReadOnlyList<LspLocation> definitions, string old, string name)
    {
        List<PlacedEdits> placed = [];
        foreach (string path in definitions.Select(d => DocumentUris.ToPath(d.Uri)).Distinct(PathNames.Comparer))
        {
            string text = context.ReadOpenOrDisk(path);
            foreach (string designed in DesignerCode.Designed(text))
            {
                if (DesignerCode.Read(text, designed) is { } source && source.MadeAt(old) is not null && source.Fields.Any(f => f.Name == old)
                    && DesignerWriter.Renamed(text, source, old, name) is { Count: > 0 } edits)
                {
                    placed.Add(new PlacedEdits(path, ServerPlaces.EditsOf(TextSnapshot.From(text), edits), text));
                }
            }
        }
        return placed;
    }

    /// <summary>Writes in the IDE's log a rename the server answered with nothing, none of its files known to be behind: where
    /// it says the name is declared, and each file of its solution written in the last minute, how long before the
    /// request, whether it holds the file as the disk has it, and when it was told the file changed.</summary>
    private async Task NoteNothingToRenameAsync(string name, IReadOnlyList<LspLocation> definitions, Asked asked)
    {
        if (context.CSharp?.Session is not { } session)
        {
            return;
        }
        string files;
        try
        {
            WrittenLately lately = await session.WrittenSinceAsync(asked.Since - TimeSpan.FromMinutes(1));
            IReadOnlyList<Roslyn.Extension.TextHeld> held = await session.HeldTextsAsync([.. lately.Written.Select(w => w.Path)]);
            // Each file read off the window's thread, as files of any size may have been written.
            files = await BackgroundWork.Run(() => string.Join("; ", held.Select(t => $"{Path.GetFileName(t.Path)} {Durations.Said(asked.Since - File.GetLastWriteTimeUtc(t.Path))} before, "
                + (t.Hash is { } hash && HeldTexts.OnDisk(t.Path, hash) == true ? "held as on disk" : "held otherwise")
                + (session.Server.WatchedFiles.ToldAt(t.Path) is { } told ? $", told {Durations.Said(Stopwatch.GetElapsedTime(told, asked.Sent))} before" : ", never told"))));
        }
        catch (LanguageServerFailed failed)
        {
            files = failed.Reason;
        }
        IdeLog.Write(IdeLog.Ide, $"Rename of {name}: the language server renames nothing there, declaring it at [{string.Join(", ", definitions.Select(d => d.Uri))}]; written lately: {files}");
    }

    /// <summary>A name renamed from a place held on it (<see cref="HeldAt"/>), the server asked where the place is then.</summary>
    private async Task<RefactorOutcome> RenameSymbolAsync(OpenDocument document, Anchor at, string name, bool renameFiles, Patience? patience)
    {
        // A GDScript's member or local is renamed by Passenger's reading of the project's scripts, its server asked for any other name.
        if (IsGDScript(document) && await GDScriptRenameAsync(document, at.Offset, name, patience: patience) is { } passengers)
        {
            return passengers;
        }
        if (await context.TakenInAsync(document, () => at.Offset) is { } unloaded)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {unloaded}");
        }
        // A C# string naming a GDScript's member is renamed as from its declaration, its other uses following there.
        if (document.Language == Languages.Language.CSharp && await context.Languages.CSharp.ScriptMemberAtAsync(document, at.Offset) is ({ } script, int memberAt, var twice))
        {
            return twice is not null ? RefactorOutcome.Refused($"Rename was not applied: {twice}") : await RenameInScriptAsync(script, memberAt, name, patience);
        }
        // A named element's field is made from its XAML, so it is renamed as the element is, there and in the code-behind.
        IReadOnlyList<LspLocation> definitions = document.Language == Languages.Language.CSharp ? await context.Now(document).DefinitionAsync(at.Offset) : [];
        if (document.Language == Languages.Language.CSharp && await context.Languages.Xaml.NamedElementOfAsync(document, definitions, at.Offset) is { Path: { } elementFile } element)
        {
            return element.Name == name ? new RefactorOutcome($"{name} is its name already", null, [], [])
                : await context.Languages.Xaml.XamlRenameAsync(elementFile, element.Name, name, patience) ?? RefactorOutcome.Refused(XamlAssist.Unread(elementFile));
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
        if (component is null && context.WebOf(document) is null && name == old)
        {
            return new RefactorOutcome($"{old} is its name already: nothing was renamed", null, [], []);
        }
        // The open files as the request went: what is typed meanwhile carries the answer on.
        using Asked asked = WorkspaceEdits.Track(context.Editors);
        // A Godot script's member by any of Godot's names for it is renamed from the code's own, its other names following.
        GodotNamesFound? godot = component is null ? await context.Languages.CSharp.GodotNamesAsync(document, context.Now(document), at.Offset, uses: true) : null;
        (string own, string written, string? misnamed) = godot is null ? (name, name, null) : CSharpAssist.GodotRenamed(godot, name);
        if (misnamed is not null)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {misnamed}");
        }
        // Asked of the server running now: one there before may have been started again since.
        System.Text.Json.JsonElement? renamed = godot is { Via: not GodotNameVia.Own, Symbol: { Path: { } declaring, At: { } declared } } && context.CSharp?.Session is { } godotServer
            ? await godotServer.RenameAsync(new UriSpan(DocumentUris.FromPath(declaring), declared, declared), own)
            : component is null ? await context.Now(document).RenameAsync(at.Offset, name)
            : context.CSharp?.Session is { } session ? await session.RenameAsync(component.Name, name) : null;
        // A server renaming the name with a sigil the name shown leaves out (.card, #main, $accent, @accent) is asked again with it.
        if (renamed is { } bare && context.WebOf(document) is not null && SigilOf(bare, document.Path, text, RenamedAt(document, text, at.Offset), name) is char sigil)
        {
            renamed = await context.Now(document).RenameAsync(at.Offset, sigil + name);
        }
        if (renamed is not { } edit)
        {
            // A property the MVVM Toolkit makes, which the server renames nowhere, is renamed through the field or method it is made of.
            if (component is null && godot is null && document.Language == Languages.Language.CSharp
                && await context.Now(document).SymbolAtAsync(at.Offset) is { MadeOf: { Path: { } maker, At: { } made } } toolkitMade)
            {
                return await RenameDeclaredAsync(new Passenger.Xaml.Metadata.SourcePlace(PathNames.Canonical(maker), made.Line, made.Character), toolkitMade.Name, name, renameFiles, patience);
            }
            // A server behind a file it reads from disk (the class's XAML making a field, however long ago written) may not know the name yet.
            if (await StaleAsync(document, NoEdit, asked, patience, await MarkupOfClassAtAsync(document, at.Offset)) is { } unread)
            {
                return RefactorOutcome.Refused($"Rename was not applied: {unread}");
            }
            await NoteNothingToRenameAsync(old, definitions, asked);
            return new RefactorOutcome("This can't be renamed", null, [], []);
        }
        NamedSymbol? symbol = component is null && document.Language == Languages.Language.CSharp ? godot?.Symbol ?? await context.Now(document).SymbolAtAsync(at.Offset) : null;
        // Its uses in XAML, found where it is declared still: a binding's path, x:DataType, x:Static, a handler, a tag.
        (IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> inXaml, string? unfollowed) = symbol is not null
            ? await context.Languages.Xaml.CodeRenameAsync(symbol, own, asked) : ([], null);
        if (unfollowed is not null)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {unfollowed}");
        }
        // Its uses in a Godot project's scenes: a connection's method or signal, an exported member's key.
        (IReadOnlyList<PlacedEdits> inScenes, string? unscened) = symbol is not null ? await context.Languages.CSharp.GodotRenameAsync(symbol, own) : ([], null);
        if (unscened is not null)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {unscened}");
        }
        // What the MVVM Toolkit makes of a field or method, which no rename of its own name reaches: its property or command, its change methods.
        ToolkitFound toolkit = symbol is not null ? await context.Now(document).ToolkitRenamesAsync(at.Offset, own) : ToolkitFound.None;
        if (toolkit.Problem is { } unmade)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {unmade}");
        }
        // Its uses in the code by Godot's other names for it, which no rename of its own name reaches.
        if (godot is { Uses.Count: > 0 })
        {
            edit = TypeRename.With(edit, CSharpAssist.GodotNameEdits(godot, written));
        }
        // A GDScript's member's uses its own language's server never reads: its scenes' connections, the C# strings naming it.
        IReadOnlyList<PlacedEdits> scripted = Passenger.Workspace.GodotFileKinds.IsScript(document.Path) ? await context.Languages.CSharp.GodotScriptRenameAsync(document, at.Offset, name) : [];
        if (scripted.Count > 0)
        {
            edit = TypeRename.With(edit, scripted);
        }
        if (await StaleAsync(document, edit, asked, patience) is { } stale)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {stale}");
        }
        if (inXaml.Count > 0)
        {
            edit = TypeRename.With(edit, inXaml);
        }
        if (inScenes.Count > 0)
        {
            edit = TypeRename.With(edit, inScenes);
        }
        if (toolkit.Uses.Count > 0)
        {
            edit = TypeRename.With(edit, [.. toolkit.Uses.GroupBy(u => u.Path, PathNames.Comparer).Select(file => (file.Key, (IReadOnlyList<LspTextEdit>)[.. file.Select(u =>
                new LspTextEdit(new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character)), u.Name))]))]);
        }
        // A Windows Forms component's field: the Name its designer code sets and its section's comment, which no rename of the field reaches.
        if (symbol is not null && DesignedRenames(definitions, old, own) is { Count: > 0 } designed)
        {
            edit = TypeRename.With(edit, designed);
        }
        TakenAlong along = type is null ? new(edit, []) : await TakenAlongAsync(type, name, renameFiles, edit, asked);
        edit = along.Edit;
        List<(string From, string To)> moves = [.. along.Moves];
        IReadOnlyList<RefactoredFile> edited = await WorkspaceEdits.FilesAsync(context.Editors, edit, asked);
        // A scene's places were read from the project's index, which a save the folder's watcher has yet to tell of leaves behind.
        if (await context.Languages.CSharp.GodotOutOfDateAsync([.. inScenes, .. scripted], asked, patience) is { } overtaken)
        {
            return RefactorOutcome.Refused($"Rename was not applied: {overtaken}");
        }
        List<string> dropped = [];
        string command = $"Rename {old} to {name}";
        using CommandHistory.Recording recording = context.Editors.Commands.Record(command);
        TypeRename.Applied applied;
        List<string> left = [];
        string? ungodot;
        try
        {
            applied = TypeRename.Apply(context.Editors, edit, moves, asked, dropped, recording);
            ungodot = await GodotFollowAsync(edit, applied, recording, left);
            // Written whether or not every file moved: the code is renamed either way.
            await WorkspaceEdits.LandedAsync(recording, context.Say);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"Rename failed: {failed.Message}");
        }
        List<RefactoredFile> changed = [.. edited.Where(f => !(context.Editors.Find(f.Path) is { } open && dropped.Contains(open.Name)))];
        string movedSaid = applied.Moved.Count > 0 ? $"; {string.Join(", ", applied.Moved.Select(m => $"{Path.GetFileName(m.From)} to {Path.GetFileName(m.To)}"))}" : string.Empty;
        string said = dropped.Count > 0 ? Refusals.EditedMeanwhile("Renamed", dropped)
            : (changed.Count > 1 ? $"Renamed in {Numbers.Said(changed.Count)} files" : "Renamed") + movedSaid;
        if (applied.Unmoved is { } unmoved)
        {
            said += $"{(dropped.Count > 0 ? "; and" : ", but")} {unmoved}";
        }
        if (ungodot is not null || left.Count > 0)
        {
            said += $"; {ungodot ?? string.Join("; ", left)}";
        }
        return new RefactorOutcome(said, null, [.. changed, .. applied.Moved.Select(m => new RefactoredFile(m.From, RefactoredChange.Moved, [], m.To))], dropped) { Unmoved = applied.Unmoved };
    }

    /// <summary>What a type's rename to <paramref name="name"/> takes along, Rename's and a code action's alike: the
    /// <c>x:Class</c> naming it, where the edit does not change it already; its files named after it when
    /// <paramref name="renameFiles"/>, and the markup its class is made with (<see cref="TypeRename.MarkupAsync"/>), each but
    /// those the edit moves itself. The edit with them, and the moves.</summary>
    private async Task<TakenAlong> TakenAlongAsync(TypeRename.Named type, string name, bool renameFiles, System.Text.Json.JsonElement edit, Asked? asked)
    {
        TypeRename.Markup markup = await TypeRename.MarkupAsync(context.Xaml?.Invoke(), type, name, renameFiles);
        if (markup.ClassFile is { } xaml && type.FullName is { } fullName && TypeRename.ClassEdit(xaml, WorkspaceEdits.Answered(context.Editors, asked)(xaml), fullName, name) is { } named
            && !WorkspaceEdits.Changes(edit, named.Path, named.Edit.Range))
        {
            edit = TypeRename.With(edit, named.Path, named.Edit);
        }
        IReadOnlyList<string> moved = WorkspaceEdits.Moved(edit);
        List<(string From, string To)> moves = [.. (renameFiles ? type.Files : []).Select(f => (From: f, To: TypeRename.Renamed(f, name))).Concat(markup.Moves)
            .Where(m => m.From != m.To && File.Exists(m.From) && !moved.Any(p => PathNames.Same(p, m.From))).Distinct()];
        return new TakenAlong(edit, moves);
    }

    /// <summary>A type's rename's edit with what it takes along (<see cref="TakenAlongAsync"/>), and the files it moves.</summary>
    private sealed record TakenAlong(System.Text.Json.JsonElement Edit, IReadOnlyList<(string From, string To)> Moves);

    /// <summary>A Godot project's files following the files a change moved within its command, those its edit renamed and
    /// those a type's rename took along (<see cref="CSharpAssist.GodotFollowAsync"/>): null once they follow, else why one
    /// could not.</summary>
    private Task<string?> GodotFollowAsync(System.Text.Json.JsonElement edit, TypeRename.Applied applied, CommandHistory.Recording recording, List<string> left) =>
        context.Languages.CSharp.GodotFollowAsync([.. WorkspaceEdits.Renames(edit).Where(r => Path.Exists(r.To) && (!Path.Exists(r.From) || PathNames.Same(r.From, r.To)))
            .Concat(applied.Moved).Select(m => (m.From, PathNames.Canonical(m.To)))], recording, left);

    /// <summary>A GDScript's member renamed from where its script declares it, as Rename there renames it: the script lent
    /// with no tab where none shows it, renamed by Passenger's reading, else once Godot's server reads it, within
    /// <see cref="ScriptServed"/>.</summary>
    private async Task<RefactorOutcome> RenameInScriptAsync(string script, int offset, string name, Patience? patience)
    {
        (OpenDocument document, IDisposable hold) = context.Editors.Lend(script);
        using (hold)
        {
            if (await GDScriptRenameAsync(document, offset, name, patience: patience) is { } scripted)
            {
                return scripted;
            }
            Deadline until = Deadline.In(Waits.Of(ScriptServed));
            if (context.WebOf(document) is { Lsp: null })
            {
                context.Say($"Rename waits for Godot's server to read {document.Name}");
            }
            while (context.WebOf(document) is { Lsp: null } && context.Web?.Invoke()?.CannotStart(document) == false && !until.Passed)
            {
                // Looked at every 50 ms: a web server's document comes as the server starts, raising nothing.
                await Task.Delay(50);
            }
            if (context.WebOf(document)?.Lsp is null)
            {
                return RefactorOutcome.Refused($"Rename was not applied: {context.Web?.Invoke()?.Unready("Renaming its members", document) ?? $"no server reads {document.Name}"}");
            }
            return await RenameSymbolAsync(document, offset, name, renameFiles: false, patience);
        }
    }

    /// <summary>How long a rename from a C# string waits for Godot's server to read the script declaring the member, its
    /// editor started for it where none runs.</summary>
    private static readonly TimeSpan ScriptServed = TimeSpan.FromSeconds(90);

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

    /// <summary>Reformat Code on the file shown, held as the action's work from its first moment until it is done, which it
    /// tells what it came to: reformatted or not, or why not.</summary>
    private async Task ReformatCore()
    {
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        const string what = "Reformat Code";
        using Working? working = context.Work?.Begin(AppActions.Reformat, $"Reformatting {document.Name}…", stoppable: false);
        string? unready = null;
        LspDocument? Server()
        {
            unready = context.Unready(what);
            if (unready is not null)
            {
                context.Say(unready);
            }
            return context.Ask();
        }
        try
        {
            if (await ReformatAsync(document, Selected(document), Server) is { } done)
            {
                context.Say(done.Said);
                working?.Tell(done.Applied, done.Said);
            }
            else if (unready is not null)
            {
                working?.Tell(false, unready);
            }
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
            working?.Tell(false, $"{what} failed: {failed.Reason}");
        }
    }

    /// <summary>Vim's =: each stretch of the file's lines laid out as Reformat Code lays out a selection, the last first so
    /// the lines above stay where they were, then each caret on its line's first non-blank; what it came to said.</summary>
    internal async Task ReformatLinesAsync(OpenDocument document, IReadOnlyList<(int First, int Last)> lines)
    {
        using (BackgroundWork.Scope())
        {
            try
            {
                Reformatted? done = null;
                // dotnet format lays out the whole file, once for every stretch.
                if (UsesDotnetFormat(document))
                {
                    lines = lines.Count > 0 ? [lines[^1]] : lines;
                }
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    TextSnapshot text = document.Text.Snapshot;
                    if (lines[i].Last >= text.LineCount)
                    {
                        continue;
                    }
                    TextRange range = TextRange.FromBounds(text.GetLineStart(lines[i].First), text.GetLine(lines[i].Last).End);
                    done = await ReformatAsync(document, range, () => context.Ask("Reformat Code"));
                    if (done is not { Applied: true })
                    {
                        break;
                    }
                }
                if (context.Editors.Active == document)
                {
                    TextSnapshot after = document.Text.Snapshot;
                    bool breakEnds = Vim.KeepsFinalLineBreak(document.Text.Saved);
                    Editor.Selections = Editor.Selections.With(s => Selection.At(Vim.Settled(after, TextNavigation.IndentEnd(after, after.GetLine(after.GetLineFromOffset(s.Caret))), breakEnds)));
                }
                if (done is not null)
                {
                    context.Say(done.Said);
                }
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Reformat Code", failed);
            }
        }
    }

    /// <summary>What Reformat Code came to: whether it was applied, whether it changed the text (not when the file was
    /// formatted already), and what to say of it, or why it was not applied.</summary>
    internal sealed record Reformatted(bool Applied, bool Changed, string Said)
    {
        /// <summary>Why a reformat refused was not applied, as <see cref="Refused"/> was told; null where it says so otherwise.</summary>
        public string? Why { get; init; }

        /// <summary>A reformat not applied, saying why.</summary>
        public static Reformatted Refused(string what, string why) => new(false, false, $"{what} was not applied: {why}") { Why = why };

        /// <summary>A reformat of a file shown read-only, refused whatever its formatter would make of it; null for a file
        /// that takes one.</summary>
        public static Reformatted? ReadOnly(OpenDocument document, string what) =>
            document.ReadOnlyWhy is { } readOnly ? Refused(what, readOnly) : null;

        /// <summary>A reformat's edits, in the offsets of the text as <paramref name="asked"/> began, carried onto the open
        /// file as it is now and applied as one undoable edit; refused where the file is read-only or the code one changes
        /// was edited meanwhile, and no change where there are none or the file has closed.</summary>
        public static Reformatted Apply(EditorArea editors, OpenDocument document, ChangeTracker asked, IReadOnlyList<TextEdit> edits, string what)
        {
            if (ReadOnly(document, what) is { } refused)
            {
                return refused;
            }
            if (edits.Count == 0 || !editors.Documents.Contains(document))
            {
                return new(true, false, "Already formatted");
            }
            if (asked.Map(edits) is not { } mapped)
            {
                return Refused(what, "the code it changes was edited meanwhile");
            }
            WorkspaceEdits.ApplyTo(editors, document, mapped);
            return new(true, true, "Reformatted");
        }
    }

    /// <summary>Reformat Code on a file, the window's and an agent's alike, as one undoable edit, only <paramref name="within"/>
    /// when given: XAML by Passenger's own layout, a web language by the formatter its folder names (Oxfmt, Biome) or its
    /// project's Prettier where it formats it on Reformat Code, else the language <paramref name="server"/> gives, asked for
    /// only then; a read-only file refused before any is asked. Null with no server to ask, or once the file has closed.</summary>
    internal Task<Reformatted?> ReformatAsync(OpenDocument document, TextRange? within, Func<LspDocument?> server, CancellationToken cancel = default) =>
        ReformatAsync(document, within, () => Task.FromResult(server()), cancel);

    /// <summary><see cref="ReformatAsync(OpenDocument, TextRange?, Func{LspDocument?}, CancellationToken)"/> with a server
    /// that may be waited for.</summary>
    internal async Task<Reformatted?> ReformatAsync(OpenDocument document, TextRange? within, Func<Task<LspDocument?>> server, CancellationToken cancel = default)
    {
        if (Reformatted.ReadOnly(document, "Reformat Code") is { } refused)
        {
            return refused;
        }
        if (UsesDotnetFormat(document))
        {
            return await DotnetFormatOneAsync(document, Tidying.Reformat, within is not null, cancel);
        }
        within = within is { } range ? WholeLines(document.Text.Snapshot, range) : null;
        if (await context.Languages.FormatAsync(document, within) is { } done)
        {
            return done;
        }
        return await server() is { } lsp ? await ServerFormatAsync(lsp, document, "Reformat Code", within, cancel) : null;
    }

    /// <summary>How long a save waits for a language server's layout before it goes on without it.</summary>
    private static readonly TimeSpan SaveLimit = TimeSpan.FromSeconds(3);

    /// <summary>How long Reformat on save waits for dotnet format, which loads the projects each time.</summary>
    private static readonly TimeSpan DotnetFormatLimit = TimeSpan.FromSeconds(60);

    /// <summary>Reformat on save: a changed file laid out whole as Reformat Code lays it out, as one undoable edit, its
    /// language server given <see cref="SaveLimit"/> to answer. Null once done, or where nothing lays the file out; else
    /// why it was left as it was, in the status bar's words, the save going on regardless.</summary>
    internal async Task<string?> ReformatOnSaveAsync(OpenDocument document)
    {
        const string what = "Reformat on save";
        string? unready = null;
        LspDocument? ServerOf()
        {
            IServerDocument? served = context.ServerOf(document);
            if (served is { Lsp: null })
            {
                unready = context.WebOf(document) is not null && context.Web?.Invoke() is { } web ? web.Unready(what, document)
                    : context.CSharp?.Unready(what, document);
            }
            return served?.Lsp;
        }
        using CancellationTokenSource limit = new();
        // dotnet format loads the projects first, and the file is saved before it runs.
        bool dotnet = UsesDotnetFormat(document);
        limit.CancelAfter(Waits.Of(dotnet ? DotnetFormatLimit : SaveLimit));
        try
        {
            return await ReformatAsync(document, null, ServerOf, limit.Token) switch
            {
                null => unready,
                { Applied: true } => null,
                { Why: { } why } => $"{what} was not applied to {document.Name}: {why}",
                { } refused => refused.Said,
            };
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return dotnet ? $"{what} was not applied to {document.Name}: dotnet format did not finish within {Durations.Said(Waits.Of(DotnetFormatLimit))}"
                : $"{what} was not applied to {document.Name}: its language server did not answer within {Durations.Said(Waits.Of(SaveLimit))}";
        }
        catch (LanguageServerFailed failed)
        {
            return $"{what} failed: {failed.Reason}";
        }
    }

    /// <summary>Optimize imports on save: a changed C# file's unused usings removed and the rest sorted, as Optimize Imports
    /// does, its language server given <see cref="SaveLimit"/> to answer. Null once done, or where nothing serves the file;
    /// else why it was left as it was, in the status bar's words, the save going on regardless.</summary>
    internal async Task<string?> OptimizeImportsOnSaveAsync(OpenDocument document)
    {
        const string what = "Optimize imports on save";
        if (context.ServerOf(document) is not { } served)
        {
            return null;
        }
        if (served.Lsp is not { } lsp)
        {
            return context.CSharp?.Unready(what, document);
        }
        Task tidying = TidyUsings(lsp, document);
        try
        {
            await tidying.WaitAsync(Waits.Of(SaveLimit));
            return null;
        }
        catch (TimeoutException)
        {
            // Its edits, should they come, are carried through what was typed as Optimize Imports' are.
            UnawaitedWork.Dismiss(tidying);
            return $"{what} was not applied to {document.Name}: its language server did not answer within {Durations.Said(Waits.Of(SaveLimit))}";
        }
        catch (LanguageServerFailed failed)
        {
            return $"{what} failed: {failed.Reason}";
        }
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
    private async Task<Reformatted?> ServerFormatAsync(LspDocument lsp, OpenDocument document, string what, TextRange? within = null, CancellationToken cancel = default)
    {
        using ChangeTracker asked = new(document.Text);
        bool whole = within is null || !lsp.FormatsRanges;
        IReadOnlyList<LspTextEdit> answer = within is { } range && !whole
            ? await lsp.FormatAsync(range, document.Indentation.Options, document.Conventions, cancel)
            : await lsp.FormatAsync(document.Indentation.Options, document.Conventions, cancel);
        if (!context.Editors.Documents.Contains(document))
        {
            return null;
        }
        Reformatted done = Laid(document, asked, answer, what, within, whole);
        return done.Applied && within is not null && whole ? done with { Said = $"{done.Said} the whole file: its language server formats no selection" } : done;
    }

    /// <summary>One of the folder's formatters' layout of a file (Oxfmt's, Biome's), always of the whole file, carried
    /// through what was typed as it worked, applied; null where it lays none of the file out, or once the file has closed.</summary>
    internal async Task<Reformatted?> ToolFormatAsync(Web.ToolDocument tool, OpenDocument document, string what, TextRange? within, CancellationToken cancel = default)
    {
        using ChangeTracker asked = new(document.Text);
        if (await tool.FormatAsync(cancel) is not { } answer || !context.Editors.Documents.Contains(document))
        {
            return null;
        }
        Reformatted done = Laid(document, asked, answer, what, within, whole: true);
        return done.Applied && within is not null ? done with { Said = $"{done.Said} the whole file: {tool.Tool.Name} formats no selection" } : done;
    }

    /// <summary>A server's layout of a file, in the text as <paramref name="asked"/> began, applied as one undoable edit:
    /// <paramref name="within"/>'s own lines alone where it formatted only them.</summary>
    private Reformatted Laid(OpenDocument document, ChangeTracker asked, IReadOnlyList<LspTextEdit> answer, string what, TextRange? within, bool whole)
    {
        List<TextEdit> laid = WorkspaceEdits.Convert(asked.Snapshot, answer, asked.Document.LineEnding, document.Indentation.Options);
        if (within is { } kept && !whole)
        {
            // Only the selection's own lines are laid out: the line after it keeps its indentation.
            laid.RemoveAll(e => e.Start >= kept.End);
        }
        // The server's edits that come to nothing in the file's line breaks and indentation are no reformat.
        return Reformatted.Apply(context.Editors, document, asked, laid, what);
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

    /// <summary>Code Cleanup on a C# file: the unnecessary usings removed, the usings sorted, then, unless
    /// <paramref name="layOut"/> is false, the file reformatted by Settings' C# formatter; what it did, in the status bar's
    /// words ("removed unused usings"), none when the file was clean.</summary>
    public async Task<IReadOnlyList<string>> CleanUpAsync(LspDocument lsp, OpenDocument document, bool layOut = true)
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
        if (!layOut)
        {
            return done;
        }
        if ((UsesDotnetFormat(document) ? await DotnetFormatOneAsync(document, Tidying.Cleanup, false, CancellationToken.None)
            : await ServerFormatAsync(lsp, document, "Code Cleanup")) is { } formatted)
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
        RefactorOutcome made = await ApplyEditAsync(edit, asked, what, what);
        if (made.Problem is not null)
        {
            context.Say(made.Problem);
            return false;
        }
        if (made.Left.Count > 0)
        {
            context.Say(Refusals.NotAppliedEditedMeanwhile(what, made.Left));
        }
        return made.Left.Count == 0;
    }

    /// <summary>A server's edit made as one command of Undo under <paramref name="title"/>, the window's and an agent's alike,
    /// what it makes, moves or deletes followed as a refactoring's are (<see cref="AssistContext.ChangeFilesAsync"/>), once
    /// its files have landed: the files it changed and moved and those it left, with that it is done when the status bar said
    /// it was waiting for one, or that a file could not follow its type, or why not, each said as <paramref name="what"/>. A
    /// type it renames takes along what Rename's does (<see cref="TakenAlongAsync"/>), whatever action made it.</summary>
    internal async Task<RefactorOutcome> ApplyEditAsync(System.Text.Json.JsonElement edit, Asked? asked, string title, string what)
    {
        List<(string From, string To)> moves = [];
        foreach ((TypeRename.Named type, string name) in await TypeRename.RenamedByAsync(context.Editors, edit, asked))
        {
            TakenAlong along = await TakenAlongAsync(type, name, RenameSettings.RenameFiles, edit, asked);
            edit = along.Edit;
            moves.AddRange(along.Moves.Where(m => !moves.Any(n => PathNames.Same(n.From, m.From))));
        }
        List<string> dropped = [];
        IReadOnlyList<RefactoredFile> files = await WorkspaceEdits.FilesAsync(context.Editors, edit, asked);
        bool waited;
        TypeRename.Applied applied = new(0, [], null);
        List<string> left = [];
        string? unfollowed;
        using CommandHistory.Recording recording = context.Editors.Commands.Record(title);
        try
        {
            await context.ChangeFilesAsync([.. WorkspaceEdits.Paths(edit), .. moves.SelectMany(m => new[] { m.From, m.To })], recording,
                () => applied = TypeRename.Apply(context.Editors, edit, moves, asked, dropped, recording));
            unfollowed = await GodotFollowAsync(edit, applied, recording, left);
            waited = await WorkspaceEdits.LandedAsync(recording, context.Say);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {failed.Message}");
        }
        string said = applied.Unmoved is { } unmoved ? $"{what} was made, but {unmoved}"
            : (unfollowed ?? (left.Count > 0 ? string.Join("; ", left) : null)) is { } godot ? $"{what} was made, but {godot}"
            : waited ? $"{what} done" : string.Empty;
        return new RefactorOutcome(said, null, [.. files, .. applied.Moved.Select(m => new RefactoredFile(m.From, RefactoredChange.Moved, [], m.To))], dropped) { Unmoved = applied.Unmoved };
    }
}
