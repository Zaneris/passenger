using System.Text.Json;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A change a language server makes by its own command (<c>workspace/executeCommand</c>: TypeScript's
/// refactorings, ESLint's fixes), for the window and an agent alike: any edit its code action carries made first, then the
/// command run, and each edit the server asks for as it runs (<c>workspace/applyEdit</c>) made on the window's thread in
/// the same recording, carried through what was typed since the command was sent, one command of Undo, its writes waited
/// for until they have landed. A file the server made empty for its edit a moment before (Move to a new file's) is one the
/// command made, deleted again where the edit is refused.</summary>
internal static class ServerCommands
{
    /// <summary>How long before a command was sent a file found empty may have been made by it: a disk's clock is coarser
    /// than the IDE's.</summary>
    private static readonly TimeSpan Coarse = TimeSpan.FromSeconds(1);

    /// <summary>The change made, under <paramref name="title"/> in Undo and said as <paramref name="what"/>: the action's
    /// <paramref name="edit"/>, answered for the open files as <paramref name="asked"/> followed them, then the command, which
    /// <paramref name="run"/> sends. What it came to: the files it changed and those left as edited meanwhile, what to say
    /// (that it changed nothing, or is done after a file landed late), why not, and where the server asked a name it wrote
    /// be renamed. An agent's refusal for files the server had yet to read is told its <paramref name="patience"/>.</summary>
    public static async Task<RefactorOutcome> RunAsync(EditorArea editors, string title, string what, JsonElement? edit, Func<CommandRun, Task> run,
        Asked? asked, Patience? patience, Action<string>? waiting)
    {
        UiThread.Check();
        List<RefactoredFile> files = [];
        List<string> dropped = [];
        HashSet<string> created = new(StringComparer.Ordinal);
        string? refused = null;
        using CommandHistory.Recording recording = editors.Commands.RecordWrites(title);

        // The first edit refused refuses those after it: the server goes on from what it believes made.
        async Task<string?> Make(JsonElement change, Asked? answered)
        {
            if (refused is not null)
            {
                return refused;
            }
            DateTime since = answered?.Since ?? DateTime.UtcNow;
            List<string> made = Made(editors, change, since);
            if (await WorkspaceEdits.StaleAsync(editors, (LspDocument?)null, change, since, patience, made) is { } stale)
            {
                Unmake(made, recording);
                return refused = stale;
            }
            IReadOnlyList<RefactoredFile> changed = WorkspaceEdits.Files(change, WorkspaceEdits.Answered(editors, answered));
            foreach (string path in made)
            {
                recording.Made(path);
            }
            try
            {
                WorkspaceEdits.ApplyAsCommand(editors, change, recording, answered, dropped);
            }
            catch (WorkspaceEditFailed failed)
            {
                Unmake(made, recording);
                return refused = failed.Message;
            }
            files.AddRange(changed);
            created.UnionWith(made);
            return null;
        }

        if (edit is { } carried && await Make(carried, asked) is { } problem)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {problem}");
        }
        // The command's edits are answered for the open files as they were as it was sent.
        using Asked sent = WorkspaceEdits.Track(editors);
        CommandRun command = new(change => UiThread.InvokeAsync(() => Make(change, sent)));
        try
        {
            await run(command);
        }
        catch (LanguageServerFailed failed)
        {
            // What it made before it failed stands, as the command's.
            refused ??= files.Count == 0 ? failed.Reason : null;
        }
        bool waited;
        try
        {
            waited = await WorkspaceEdits.LandedAsync(recording, waiting);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {failed.Message}");
        }
        if (refused is not null && files.Count == 0)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {refused}");
        }
        List<RefactoredFile> shown = [.. files.Where(f => !(editors.Find(f.Path) is { } open && dropped.Contains(open.Name)))
            .Select(f => created.Contains(f.Path) ? f with { Change = "created", Lines = [] } : f).DistinctBy(f => (f.Path, f.Change))];
        string said = refused is not null ? $"{what} was made only in part: {refused}"
            : files.Count == 0 && dropped.Count == 0 ? $"{what} changed nothing"
            : waited ? $"{what} done" : string.Empty;
        return new RefactorOutcome(said, null, shown, dropped) { RenameAt = command.RenameAt };
    }

    /// <summary>The files an edit fills that are not open and that its server made empty for it since a moment before
    /// <paramref name="since"/>, by when they were written: Windows gives a file made under a name deleted a moment before
    /// that file's time of making.</summary>
    private static List<string> Made(EditorArea editors, JsonElement edit, DateTime since)
    {
        List<string> made = [];
        foreach (FileEdits change in Shapes.WorkspaceEdit(edit).Where(c => c.Operation is null))
        {
            string path = DocumentUris.ToPath(change.Uri);
            FileInfo file = new(path);
            if (editors.Find(path) is null && file.Exists && file.Length == 0 && file.LastWriteTimeUtc >= since - Coarse && !made.Contains(path))
            {
                made.Add(path);
            }
        }
        return made;
    }

    /// <summary>The files a refused edit's server made empty for it deleted again, and left out of the command.</summary>
    private static void Unmake(IReadOnlyList<string> made, CommandHistory.Recording recording)
    {
        foreach (string path in made)
        {
            recording.Unwritten(path);
            try
            {
                if (new FileInfo(path) is { Exists: true, Length: 0 })
                {
                    File.Delete(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left as the server made it: empty, and named in no change.
            }
        }
    }
}
