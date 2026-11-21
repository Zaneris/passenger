using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A C# file's outline from its own parse (<see cref="Workspace.OpenDocument.Syntax"/>), never the language
/// server's nor a parse of its own: the newest parse of the text as it stands, waited for while one is under way, read into
/// rows off the window's thread (<see cref="Declarations.Rows{T}"/>). Asked on the window's thread; a text that changes again
/// before its parse lands is given up, its next parse being read as it lands.</summary>
internal sealed class CSharpOutline(CSharpHighlighter syntax, TextDocument text) : FileOutline
{
    public override async Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        ParsedText parsed = syntax.Newest ?? await syntax.ParsedAsync(text.Snapshot) ?? throw new OperationCanceledException(cancel);
        cancel.ThrowIfCancellationRequested();
        return await BackgroundWork.Run(() => Declarations.Rows(parsed, (declared, depth) =>
            new SymbolRow(declared.Name, declared.Type is { } type ? $": {type}" : null, null, declared.Symbol, path, declared.At, depth, declared.LastLine), before, cancel), cancel);
    }
}
