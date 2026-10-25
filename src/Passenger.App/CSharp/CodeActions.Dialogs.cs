using Avalonia.Controls;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>The refactorings that ask their options in a dialog of their own: Change Signature, Extract Interface, Move
/// to Another File and Move to Namespace, Pull Members Up and Push Members Down. Each asks Passenger's extension in the
/// language server what is at the caret, shows its dialog, and on Next has the extension compute the change, which is
/// applied as one command that Undo takes back whole, the files it makes included. The making is a method of its own
/// per refactoring (<see cref="ChangeSignatureAsync"/>...), which an agent's refactor tool calls with the dialog's
/// options.</summary>
internal sealed partial class CodeActions
{
    public async Task ChangeSignature()
    {
        const string name = "Change Signature";
        if (await DescribeAsync<Signature>(name, Handlers.DescribeSignature) is not ({ } signature, { } document, { } caret))
        {
            return;
        }
        using (caret)
        {
            if (signature.Problem is { } problem)
            {
                context.Say(problem);
                return;
            }
            await ShowAsync(new ChangeSignatureDialog(signature, (method, returnType, parameters) =>
                Shown(ChangeSignatureAsync(document, caret.Offset, signature, method, returnType, parameters))));
        }
    }

    public async Task ExtractInterface()
    {
        const string name = "Extract Interface";
        if (await DescribeAsync<InterfaceSource>(name, Handlers.DescribeInterface) is not ({ } source, { } document, { } caret))
        {
            return;
        }
        using (caret)
        {
            if (source.Problem is { } problem)
            {
                context.Say(problem);
                return;
            }
            await ShowAsync(new ExtractInterfaceDialog(source, (interfaceName, file, members) =>
                Shown(ExtractInterfaceAsync(document, caret.Offset, source, interfaceName, file, members))));
        }
    }

    /// <summary>Move on a type: a list of where it can go, then the dialog of the one chosen.</summary>
    public async Task Move()
    {
        const string name = "Move";
        if (await DescribeAsync<TypeSource>(name, Handlers.DescribeType) is not ({ } type, { } document, { } caret))
        {
            return;
        }
        if (type.Problem is { } problem)
        {
            caret.Dispose();
            context.Say(problem);
            return;
        }
        popups.ShowModal(new ChoicePopup($"Move {type.Kind} '{type.Name}'",
            [new Choice("Move to Another File…", string.Empty, true, Mnemonic: '1'), new Choice("Move to Namespace…", string.Empty, false, Mnemonic: '2')]),
            caret.Offset, name, chosen =>
            {
                if (chosen?.Value is not bool toFile)
                {
                    return Task.CompletedTask;
                }
                // Not awaited: the list's choice is work a screenshot waits for, and the dialog waits for the person.
                IdeErrors.Observe(MoveAsync(type, document, new FollowedCaret(new ChangeTracker(document.Text), caret.Offset), toFile));
                return Task.CompletedTask;
            });
        // The list's own let go as it closes, however it closes: a choice follows the caret on from there with one of its own.
        popups.WhenClosed(caret.Dispose);
    }

    private async Task MoveAsync(TypeSource type, OpenDocument document, FollowedCaret caret, bool toFile)
    {
        using (caret)
        {
            await ShowAsync(toFile
                ? MoveTypeDialog.ToFile(type, file => Shown(MoveTypeToFileAsync(document, caret.Offset, type, file)))
                : MoveTypeDialog.ToNamespace(type, space => Shown(MoveTypeToNamespaceAsync(document, caret.Offset, type, space))));
        }
    }

    /// <summary>Pull Members Up (to a base type in the solution) or Push Members Down (to the classes derived from the
    /// one at the caret).</summary>
    public async Task MoveMembers(bool up)
    {
        string name = up ? "Pull Members Up" : "Push Members Down";
        if (await DescribeAsync<Hierarchy>(name, Handlers.DescribeHierarchy) is not ({ } hierarchy, { } document, { } caret))
        {
            return;
        }
        using (caret)
        {
            if (MembersMoveProblem(hierarchy, up) is { } problem)
            {
                context.Say(problem);
                return;
            }
            Func<string, IReadOnlyList<string>, IReadOnlyList<string>, Task<string?>> make = (target, members, made) =>
                Shown(MoveMembersAsync(document, caret.Offset, hierarchy, up, target, members, made));
            await ShowAsync(up ? MembersMoveDialog.PullUp(hierarchy, make) : MembersMoveDialog.PushDown(hierarchy, make));
        }
    }


    /// <summary>What Passenger's extension says is at a place of a file for a refactoring, as its dialog would show it: the
    /// method Change Signature changes (<see cref="Handlers.DescribeSignature"/>), the type Extract Interface or Move takes,
    /// the hierarchy Pull Members Up and Push Members Down move members in.</summary>
    public static Task<T> DescribeAtAsync<T>(LspDocument lsp, int offset, string handler) where T : class =>
        lsp.ExtensionAsync<T>(handler, new Roslyn.Extension.Caret(lsp.PlaceOf(offset)));

    /// <summary>Why Pull Members Up or Push Members Down has nothing to do for the type described, said before its
    /// dialog would open; null when it has.</summary>
    public static string? MembersMoveProblem(Hierarchy hierarchy, bool up) => hierarchy.Problem
        ?? (up && hierarchy.Bases.Count == 0 ? $"'{hierarchy.Type}' has no base type in this solution to pull members up to"
        : !up && hierarchy.Derived.Count == 0 ? $"No class in this solution derives from '{hierarchy.Type}' to push members down to"
        : hierarchy.Members.Count == 0 ? $"'{hierarchy.Type}' has no members to move"
        : null);

    /// <summary>Change Signature on the method or constructor at an offset of a file, given its new name, return type and
    /// parameters (each the one it was, or new with the value every call passes): the declaration, its family's and every
    /// call changed, as one command.</summary>
    public Task<RefactorOutcome> ChangeSignatureAsync(OpenDocument document, int offset, Signature signature, string method, string returnType, IReadOnlyList<ParameterChange> parameters,
        Patience? patience = null) =>
        MakeAsync("Change Signature", document,
            lsp => lsp.ExtensionAsync<Refactored>(Handlers.ChangeSignature, new SignatureChange(lsp.PlaceOf(offset), method, returnType, parameters)),
            files => $"Changed the signature of {signature.Container}{(signature.Kind == "method" ? "." + method : string.Empty)} in {Plural.Of(files, "file")}", patience);

    /// <summary>Extract Interface from the type at an offset: an interface of this name, in a new file of this name beside
    /// the type's, of the members chosen by their ids, which the type then implements.</summary>
    public Task<RefactorOutcome> ExtractInterfaceAsync(OpenDocument document, int offset, InterfaceSource source, string name, string file, IReadOnlyList<string> members,
        Patience? patience = null) =>
        MakeAsync("Extract Interface", document,
            lsp => lsp.ExtensionAsync<Refactored>(Handlers.ExtractInterface, new InterfaceExtraction(lsp.PlaceOf(offset), name, file, members)),
            _ => $"Extracted the interface {name} from {source.Type} to {file}", patience);

    /// <summary>Move to Another File: the type at an offset into a new file of this name beside its own.</summary>
    public Task<RefactorOutcome> MoveTypeToFileAsync(OpenDocument document, int offset, TypeSource type, string file, Patience? patience = null) =>
        MakeAsync("Move to Another File", document,
            lsp => lsp.ExtensionAsync<Refactored>(Handlers.MoveTypeToFile, new TypeToFile(lsp.PlaceOf(offset), file)),
            _ => $"Moved {type.Kind} {type.Name} to {file}", patience);

    /// <summary>Move to Namespace: the type at an offset into this namespace, the files that use it following.</summary>
    public Task<RefactorOutcome> MoveTypeToNamespaceAsync(OpenDocument document, int offset, TypeSource type, string space, Patience? patience = null) =>
        MakeAsync("Move to Namespace", document,
            lsp => lsp.ExtensionAsync<Refactored>(Handlers.MoveTypeToNamespace, new TypeToNamespace(lsp.PlaceOf(offset), space)),
            files => $"Moved {type.Kind} {type.Name} to namespace {space} in {Plural.Of(files, "file")}", patience);

    /// <summary>Pull Members Up (into the base type whose id is the target) or Push Members Down (into each derived class
    /// whose id is a line of it): the members chosen by their ids, those listed as made abstract left abstract where they
    /// stay.</summary>
    public Task<RefactorOutcome> MoveMembersAsync(OpenDocument document, int offset, Hierarchy hierarchy, bool up, string target, IReadOnlyList<string> members, IReadOnlyList<string> made,
        Patience? patience = null) =>
        MakeAsync(up ? "Pull Members Up" : "Push Members Down", document,
            lsp => lsp.ExtensionAsync<Refactored>(up ? Handlers.PullMembersUp : Handlers.PushMembersDown, new MembersMove(lsp.PlaceOf(offset), target, members, made)),
            files => up
                ? $"Pulled {Plural.Of(members.Count, "member")} of {hierarchy.Type} up to {hierarchy.Bases.FirstOrDefault(b => b.Id == target)?.Text ?? target}"
                : $"Pushed {Plural.Of(members.Count, "member")} of {hierarchy.Type} down to {Plural.Of(target.Split('\n').Length, "class", "classes")}", patience);

    /// <summary>What Passenger's extension says is at the caret for a refactoring, with the file and the caret followed from
    /// now; null, said in the status bar, when the server cannot be asked.</summary>
    private async Task<(T? Found, OpenDocument? Document, FollowedCaret? Caret)> DescribeAsync<T>(string name, string handler) where T : class
    {
        if (context.Ask(name) is not { } lsp || context.Editors.Active is not { } document)
        {
            return (null, null, null);
        }
        FollowedCaret caret = new(new ChangeTracker(document.Text), context.Caret);
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            return (await DescribeAtAsync<T>(lsp, context.Caret, handler), document, caret);
        }
        catch (LanguageServerFailed failed)
        {
            caret.Dispose();
            context.Failed(name, failed);
            return (null, null, null);
        }
    }

    internal async Task ShowAsync(RefactoringDialog dialog)
    {
        if (TopLevel.GetTopLevel(Editor) is Window owner)
        {
            await dialog.ShowDialog<bool>(owner);
        }
    }

    /// <summary>A refactoring's outcome as its dialog takes it: what it said in the status bar once made, else why not, for
    /// the dialog to say.</summary>
    private async Task<string?> Shown(Task<RefactorOutcome> making)
    {
        RefactorOutcome made = await making;
        if (made.Problem is null)
        {
            context.Say(made.Said);
        }
        return made.Problem;
    }

    /// <summary>A refactoring computed by Passenger's extension for a file and applied as one command, which Undo takes back
    /// whole: every file it changes, open or not, and every file it makes. What it came to: the files it changed and what
    /// to say of it, or why it was not made (for an agent's call, told its <paramref name="patience"/>).</summary>
    internal async Task<RefactorOutcome> MakeAsync(string name, OpenDocument document, Func<LspDocument, Task<Refactored>> ask, Func<int, string> done, Patience? patience)
    {
        try
        {
            LspDocument lsp = context.Now(document);
            using Asked asked = WorkspaceEdits.Track(context.Editors);
            Refactored made = await ask(lsp);
            if (made.Problem is { } problem)
            {
                return RefactorOutcome.Refused(problem);
            }
            if (made.Changes.Count == 0)
            {
                return new RefactorOutcome($"{name} changed nothing", null, [], []);
            }
            System.Text.Json.JsonElement edit = RoslynExtension.WorkspaceEdit(made);
            if (await WorkspaceEdits.StaleAsync(context.Editors, lsp, edit, asked.Since, patience) is { } stale)
            {
                return RefactorOutcome.Refused($"{name} was not applied: {stale}");
            }
            List<string> dropped = [];
            IReadOnlyList<RefactoredFile> files = WorkspaceEdits.Files(edit, WorkspaceEdits.Answered(context.Editors, asked));
            using (CommandHistory.Recording recording = context.Editors.Commands.Record(name))
            {
                foreach (FileChange change in made.Changes)
                {
                    recording.Watch(PathNames.Canonical(change.Path));
                }
                _ = WorkspaceEdits.Apply(context.Editors, edit, asked, dropped);
            }
            context.ChangedFiles();
            // Counted by file: a file made is its creation and its text, which the edit counts apart.
            string said = done(made.Changes.Count - dropped.Count) + (dropped.Count > 0 ? $", but not in {string.Join(", ", dropped)}: it was edited meanwhile" : string.Empty);
            return new RefactorOutcome(said, null, files, dropped);
        }
        catch (LanguageServerFailed failed)
        {
            return RefactorOutcome.Refused($"{name} failed: {failed.Reason}");
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused(failed.Message);
        }
    }

    /// <summary>Where the caret was as a refactoring was asked for, followed through what changes the file until its
    /// dialog is done.</summary>
    private sealed class FollowedCaret(ChangeTracker tracker, int offset) : IDisposable
    {
        public int Offset => tracker.MapOffset(offset);

        public void Dispose() => tracker.Dispose();
    }
}
