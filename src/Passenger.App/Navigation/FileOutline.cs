using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>Where a file's outline comes from, as Structure, File Structure and an agent's outline ask it: its language
/// server's symbols (<see cref="Served"/>), or Passenger's own reading of the file (a Godot scene's tree). Its rows are made
/// off the window's thread, a row equal to the one <c>before</c> has in its place being that one.</summary>
public abstract class FileOutline
{
    /// <summary>The outline of the file at <paramref name="path"/>; a server that cannot answer throws
    /// <see cref="LanguageServerFailed"/>.</summary>
    public abstract Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel);

    /// <summary>A language server's document's outline: its types and members, a member's type after its name.</summary>
    public static FileOutline Served(LspDocument lsp) => new ServerOutline(lsp);

    private sealed class ServerOutline(LspDocument lsp) : FileOutline
    {
        public override async Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
        {
            IReadOnlyList<DocumentSymbol> symbols = await lsp.OutlineAsync(cancel);
            cancel.ThrowIfCancellationRequested();
            ServerLines lines = ServerLines.Of(lsp.Text.Snapshot);
            return await BackgroundWork.Run(() => ToolWindows.StructureView.OutlineOf(symbols, path, lines, before, cancel), cancel);
        }
    }
}
