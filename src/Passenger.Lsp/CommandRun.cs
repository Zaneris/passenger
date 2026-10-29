using System.Text.Json;

namespace Passenger.Lsp;

/// <summary>One of a server's own commands as the client runs it (<see cref="LanguageServer.ExecuteCommandAsync(LspCommand, CommandRun, CancellationToken)"/>),
/// taking what the server asks of the client while it runs: each edit it asks to be made (<c>workspace/applyEdit</c>),
/// made by <paramref name="apply"/> (null once made, else why not), and the place it asks a name be renamed at once.</summary>
public sealed class CommandRun(Func<JsonElement, Task<string?>> apply)
{
    public Func<JsonElement, Task<string?>> Apply => apply;

    /// <summary>Where the server asked a name it wrote be renamed (TypeScript's Extract to function's new name), in the
    /// text as its edits left it; null while it has asked none.</summary>
    public LspLocation? RenameAt { get; internal set; }
}
