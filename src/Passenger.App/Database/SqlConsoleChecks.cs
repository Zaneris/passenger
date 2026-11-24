using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Database;

/// <summary>Each open SQL file a console of a connection opened this session, marked where it names a table, view or
/// column its connection's schema does not hold (<see cref="SqlChecks"/>), as it opens, as its typing pauses and as the
/// schema is read again; off the window's thread, and never opening a connection: a file of none, or of one not opened, has
/// no marks. A UI-thread object.</summary>
internal sealed class SqlConsoleChecks(Func<OpenDocument, (DatabaseKind Kind, Task<SqlCatalog?> Catalog)?> live)
    : OpenFileChecks<object>(Editors.TypingPauses.SqlChecks)
{
    /// <summary>Who checks a console, as an agent is told while a check is under way.</summary>
    private const string Checker = "the console's connection's schema";

    private static readonly object Kept = new();

    protected override (string Checker, object Kept)? Follow(OpenDocument document) =>
        document.Language == Languages.Language.Sql && !document.IsReadOnly ? (Checker, Kept) : null;

    protected override async Task CheckAsync(OpenDocument document, object kept)
    {
        int version = document.Text.Version;
        string text = document.Text.Snapshot.ToString();
        if (live(document) is not { } known || await known.Catalog is not { } catalog)
        {
            Show(document, version, []);
            return;
        }
        List<SqlProblem> problems = await BackgroundWork.Run(() => SqlChecks.Of(text, known.Kind, catalog));
        Show(document, version, [.. problems.Select(p => new Marker(TextRange.FromBounds(p.Start, p.End), MarkerKind.Error, p.Message))]);
    }

    /// <summary>Each console checked again, its connection's schema read anew or its connection changed.</summary>
    public void SchemaChanged() => Changed();
}
