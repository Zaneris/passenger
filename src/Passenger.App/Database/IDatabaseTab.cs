using Avalonia.Controls;
using Passenger.App.Workspace;

namespace Passenger.App.Database;

/// <summary>What the Database window shows among the editors, a tab of no file: a table's rows or a schema's diagram.</summary>
public interface IDatabaseTab : IDisposable
{
    /// <summary>Its tab's document, which holds no text.</summary>
    OpenDocument Document { get; }

    /// <summary>The connection it reads, by id.</summary>
    Guid Connection { get; }

    string Title { get; }

    /// <summary>What it is, for what refuses a file's text to say.</summary>
    string ShownAs { get; }

    /// <summary>Its view, made as its tab is first shown.</summary>
    Control Make();
}
