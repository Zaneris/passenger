using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>Spans of a source generator's documents taken to the files their code was written in, as the server takes a
/// reference's (a Razor component's C# to its line in the .razor). Code Razor wrote of no line of its file (the class, its
/// BuildRenderTree) is that file's at its start; any other span stays as it was.</summary>
public sealed class WrittenPlaces : IExtensionWorkspaceMessageHandler<WrittenSpans, WrittenSpans>
{
    public async Task<WrittenSpans> ExecuteAsync(WrittenSpans message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        List<UriSpan> written = [];
        foreach (UriSpan span in message.Spans)
        {
            written.Add(await WrittenAsync(context.Solution, span, cancellationToken).ConfigureAwait(false) ?? span);
        }
        return new WrittenSpans(written);
    }

    private static async Task<UriSpan?> WrittenAsync(Solution solution, UriSpan span, CancellationToken cancel)
    {
        if (!System.Uri.TryCreate(span.Uri, UriKind.Absolute, out Uri? uri) || uri.Scheme != SourceGeneratedDocumentUri.Scheme
            || (await solution.GetTextDocumentsAsync(new DocumentUri(uri), cancel).ConfigureAwait(false)).FirstOrDefault() is not SourceGeneratedDocument document)
        {
            return null;
        }
        SourceText text = await document.GetValueTextAsync(cancel).ConfigureAwait(false);
        int start = Offset(text, span.Start);
        TextSpan generated = TextSpan.FromBounds(start, Math.Max(start, Offset(text, span.End)));
        if (await ProtocolConversions.TextSpanToLocationAsync(document, generated, isStale: false, cancel).ConfigureAwait(false) is { } mapped
            && mapped.DocumentUri.ParsedUri?.Scheme != SourceGeneratedDocumentUri.Scheme)
        {
            return new UriSpan(mapped.DocumentUri.UriString, new Place(mapped.Range.Start.Line, mapped.Range.Start.Character),
                new Place(mapped.Range.End.Line, mapped.Range.End.Character));
        }
        if (!document.IsRazorSourceGeneratedDocument() || await document.GetSyntaxTreeAsync(cancel).ConfigureAwait(false) is not { } tree
            || tree.GetLineMappings(cancel).Select(m => m.MappedSpan).FirstOrDefault(m => m.HasMappedPath && m.Path.Length > 0) is not { Path: { } path })
        {
            return null;
        }
        Place top = new(0, 0);
        return new UriSpan(ProtocolConversions.CreateAbsoluteDocumentUri(path).UriString, top, top);
    }

    private static int Offset(SourceText text, Place at) =>
        at.Line >= text.Lines.Count ? text.Length : Math.Min(text.Lines[at.Line].Start + at.Character, text.Lines[at.Line].End);
}
