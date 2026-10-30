using System.Collections.Specialized;
using System.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.LanguageServer;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>The texts of documents source generators made, as the server holds them, each with its project and its
/// generator. A URI whose project the server no longer has (it was started again since) is found by its project's file
/// and name, its generator and its hint name.</summary>
public sealed class GeneratedTexts : IExtensionWorkspaceMessageHandler<GeneratedAsked, GeneratedFound>
{
    public async Task<GeneratedFound> ExecuteAsync(GeneratedAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        List<GeneratedText?> found = [];
        foreach (GeneratedSource source in message.Documents)
        {
            SourceGeneratedDocument? document = await FindAsync(context.Solution, source, cancellationToken).ConfigureAwait(false);
            found.Add(document is null ? null : new GeneratedText(SourceGeneratedDocumentUri.Create(document.Identity).UriString,
                (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString(), document.Project.FilePath ?? string.Empty,
                document.Project.Name, document.Identity.Generator.TypeName));
        }
        return new GeneratedFound(found);
    }

    private static async Task<SourceGeneratedDocument?> FindAsync(Solution solution, GeneratedSource source, CancellationToken cancel)
    {
        if (!System.Uri.TryCreate(source.Uri, UriKind.Absolute, out Uri? uri) || uri.Scheme != SourceGeneratedDocumentUri.Scheme)
        {
            return null;
        }
        if ((await solution.GetTextDocumentsAsync(new DocumentUri(uri), cancel).ConfigureAwait(false)).FirstOrDefault() is SourceGeneratedDocument named)
        {
            return named;
        }
        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        if (source.ProjectPath is null || query["hintName"] is not { } hint || query["typeName"] is not { } generator)
        {
            return null;
        }
        foreach (Project project in solution.Projects.Where(p => p.FilePath == source.ProjectPath && p.Name == source.ProjectName))
        {
            foreach (SourceGeneratedDocument document in await project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false))
            {
                if (document.HintName == hint && document.Identity.Generator.TypeName == generator)
                {
                    return document;
                }
            }
        }
        return null;
    }
}
