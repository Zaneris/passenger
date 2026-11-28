using Passenger.App.Database;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A SQL file's completion, from its console's connection as it stands (<see cref="SqlCompletions"/>): the columns
/// of the tables its statement names, after a table's name or alias and a dot its columns, every table and view, the
/// schemas and the database's keywords; a file of no connection, or one not opened yet this session, its keywords alone.
/// A name typed as a word's first letter, or a dot after a name, opens the list. Reformat Code lays its statements out by
/// its database (<see cref="SqlFormatter"/>), a selection's statements alone, indented and broken as the file is. A
/// MongoDB console's completion is its shell's (<see cref="ShellCompletions"/>), a $ opening the list too.</summary>
internal sealed class SqlAssist(AssistContext context, CodeCompletion completion) : LanguageAssist
{
    /// <summary>A SQL file's database and what its connection holds, where it is a console; null for a file of none.</summary>
    public Func<OpenDocument, (DatabaseKind Kind, Task<SqlCatalog?> Catalog)?>? Live { get; set; }

    /// <summary>How Reformat Code writes keywords, as Settings leaves it.</summary>
    public SqlKeywordCase Keywords { get; set; }

    private readonly Dictionary<CompletionItem, SqlCompletions.Candidate> offered = [];

    private static bool Owns(OpenDocument? document) => document is not null && SqlConsole.Speaks(document.Language);

    private static bool IsShell(OpenDocument document) => document.Language == Language.MongoDb;

    private static bool IsRedis(OpenDocument document) => document.Language == Language.Redis;

    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } typed || !Owns(typed))
        {
            return false;
        }
        string written = context.Editor.Document.Snapshot.ToString();
        int caret = context.Caret;
        bool shell = IsShell(typed);
        bool redis = IsRedis(typed);
        bool startsName = text.Length == 1 && (char.IsLetter(text[0]) || text[0] == '_' || shell && text[0] == '$')
            && (caret < 2 || (redis ? char.IsWhiteSpace(written[caret - 2]) : !NamePart(written[caret - 2])));
        bool afterDot = text == "." && caret >= 2 && (NamePart(written[caret - 2]) || written[caret - 2] is '"' or ']' or '`' || shell && written[caret - 2] == ')');
        if ((startsName || afterDot && !redis) && !InLiteral(written, caret, typed))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !Owns(shown))
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        (DatabaseKind Kind, Task<SqlCatalog?> Catalog)? live = Live?.Invoke(shown);
        SqlCatalog? catalog = live is { } known ? await known.Catalog : null;
        if (cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        string text = asked.ToString();
        if (InLiteral(text, context.Caret, shown))
        {
            completion.Close();
            return true;
        }
        (int start, IReadOnlyList<SqlCompletions.Candidate> candidates) = IsShell(shown) ? ShellCompletions.At(text, context.Caret, catalog)
            : IsRedis(shown) ? RedisCompletions.At(text, context.Caret, catalog)
            : SqlCompletions.At(text, context.Caret, live?.Kind ?? DatabaseKind.Sqlite, catalog);
        if (candidates.Count == 0)
        {
            completion.Close();
            return true;
        }
        offered.Clear();
        List<CompletionItem> items = [];
        foreach (SqlCompletions.Candidate candidate in candidates)
        {
            CompletionItem item = new()
            {
                Label = candidate.Label,
                Kind = candidate.Kind switch
                {
                    SqlCompletions.Kind.Column => CompletionItemKind.Field,
                    SqlCompletions.Kind.Table => CompletionItemKind.Class,
                    SqlCompletions.Kind.Schema => CompletionItemKind.Module,
                    _ => CompletionItemKind.Keyword,
                },
                Detail = candidate.Detail,
                SortText = $"{(int)candidate.Kind}{items.Count:D6}",
            };
            offered[item] = candidate;
            items.Add(item);
        }
        // A Redis key holds the punctuation its naming takes (user:42, cart.items).
        completion.Open(new CodeCompletion.Completion(items, start, [], false) { Own = this, WordCharacters = IsRedis(shown) ? "_$:.-{}*@#/" : "_$" });
        return true;
    }

    public override void Commit(CompletionItem item, int start)
    {
        string text = context.Editor.Document.Snapshot.ToString();
        int end = context.Caret;
        if (context.Editors.Active is { } shown && IsRedis(shown))
        {
            end = RedisCommands.WordAt(text, end).End;
        }
        while (end < text.Length && NamePart(text[end]))
        {
            end++;
        }
        string insert = offered.TryGetValue(item, out SqlCompletions.Candidate candidate) ? candidate.Insert : item.Label;
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), insert)], SelectionSet.Single(start + insert.Length));
    }

    public override async Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within)
    {
        if (!Owns(document) || IsShell(document) || IsRedis(document))
        {
            return null;
        }
        const string what = "Reformat Code";
        if (CodeActions.Reformatted.ReadOnly(document, what) is { } refused)
        {
            return refused;
        }
        using ChangeTracker asked = new(document.Text);
        string text = asked.Snapshot.ToString();
        DatabaseKind kind = Live?.Invoke(document)?.Kind ?? DatabaseKind.Sqlite;
        (int start, int end) = (0, text.Length);
        if (within is { } range)
        {
            List<(int Start, int End)> touched = [.. SqlText.Statements(text, kind).Where(s => s.End >= range.Start && s.Start <= range.End)];
            if (touched.Count == 0)
            {
                return CodeActions.Reformatted.Apply(context.Editors, document, asked, [], what);
            }
            (start, end) = (touched[0].Start, touched[^1].End);
        }
        string part = text[start..end];
        SqlFormatOptions options = new(document.Indentation.Options, Keywords);
        string eol = document.LineEnding;
        // The text's breaks are '\n' to the formatter, a "\r\n" file's written back as its own.
        string laid = await BackgroundWork.Run(() => SqlFormatter.Format(part.ReplaceLineEndings("\n"), kind, options));
        List<TextEdit> edits = laid == part.ReplaceLineEndings("\n") ? [] : LineEndings.Fit(asked.Snapshot, [new TextEdit(start, end - start, laid)], eol);
        return CodeActions.Reformatted.Apply(context.Editors, document, asked, edits, what);
    }

    public override string? Describe(CompletionItem item) => offered.TryGetValue(item, out SqlCompletions.Candidate candidate)
        ? $"{candidate.Label}: {candidate.Detail}" : null;

    private static bool NamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Whether the caret stands in a string or a comment, where nothing is offered.</summary>
    private static bool InLiteral(string text, int caret, OpenDocument document)
    {
        bool shell = IsShell(document);
        bool redis = IsRedis(document);
        int i = 0;
        while (i < caret)
        {
            int? skipped = shell ? DocumentShell.Skipped(text, i) : redis ? RedisCommands.Skipped(text, i)
                : text[i] is '\'' or '-' or '/' ? SqlText.Skipped(text, i, DatabaseKind.Postgres) : null;
            if (skipped is { } past)
            {
                // A line comment that runs to the caret holds it.
                bool line = text[i] == '-' || redis && text[i] == '#' || shell && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/';
                if (past > caret || past == caret && line)
                {
                    return true;
                }
                i = past;
            }
            else
            {
                i++;
            }
        }
        return false;
    }
}
