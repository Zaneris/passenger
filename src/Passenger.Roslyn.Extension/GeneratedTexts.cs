using System.Collections.Specialized;
using System.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.GeneratedCodeRecognition;
using Microsoft.CodeAnalysis.LanguageServer;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>The texts of documents source generators made, as the server holds them, each with its project and its
/// generator. A URI whose project the server no longer has (it was started again since) is found by its project's file
/// and name, its generator and its hint name.</summary>
public sealed class GeneratedTexts : IExtensionWorkspaceMessageHandler<GeneratedAsked, GeneratedFound>
{
    public async Task<GeneratedFound> ExecuteAsync(GeneratedAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<GeneratedText?> found = [];
        foreach (GeneratedSource source in message.Documents)
        {
            SourceGeneratedDocument? document = await FindAsync(context.Solution, source, cancellationToken).ConfigureAwait(false);
            found.Add(document is null ? null : await TextOfAsync(document, cancellationToken).ConfigureAwait(false));
        }
        return new GeneratedFound(found);
    }

    internal static async Task<GeneratedText> TextOfAsync(SourceGeneratedDocument document, CancellationToken cancel) =>
        new(SourceGeneratedDocumentUri.Create(document.Identity).UriString, (await document.GetTextAsync(cancel).ConfigureAwait(false)).ToString(),
            document.Project.FilePath ?? string.Empty, document.Project.Name, document.Identity.Generator.TypeName);

    private static async Task<SourceGeneratedDocument?> FindAsync(Solution solution, GeneratedSource source, CancellationToken cancel)
    {
        if (!System.Uri.TryCreate(source.Uri, UriKind.Absolute, out Uri? uri) || uri.Scheme != SourceGeneratedDocumentUri.Scheme)
        {
            return null;
        }
        if ((await solution.GetTextDocumentsAsync(new DocumentUri(source.Uri), cancel).ConfigureAwait(false)).FirstOrDefault() is SourceGeneratedDocument named)
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

/// <summary>The texts of the documents source generators make under a file name for the projects of a name, for a document
/// known by no URI: what an earlier run copied.</summary>
public sealed class GeneratedNamed : IExtensionWorkspaceMessageHandler<GeneratedNameAsked, GeneratedFound>
{
    public async Task<GeneratedFound> ExecuteAsync(GeneratedNameAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<GeneratedText?> found = [];
        foreach (Project project in context.Solution.Projects.Where(p => (p.FilePath is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file) : p.Name) == message.Project))
        {
            foreach (SourceGeneratedDocument document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Path.GetFileName(document.HintName) == message.File)
                {
                    found.Add(await GeneratedTexts.TextOfAsync(document, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        return new GeneratedFound(found);
    }
}

/// <summary>Which of the documents named are generated code, as Roslyn's own check tells it for its analyzers and code
/// fixes: a source generator's document, and a C# file an .editorconfig's <c>generated_code</c> marks, else one whose name
/// (<c>.g.cs</c>, <c>.designer.cs</c>, <c>.generated.cs</c>) or opening comment (<c>&lt;auto-generated&gt;</c>) says a tool
/// wrote it. A URI of no C# document, a Razor or XAML file's, is not.</summary>
public sealed class GeneratedDocuments : IExtensionWorkspaceMessageHandler<GeneratedDocumentsAsked, GeneratedDocumentsFound>
{
    public async Task<GeneratedDocumentsFound> ExecuteAsync(GeneratedDocumentsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<bool> generated = [];
        foreach (string uri in message.Uris)
        {
            generated.Add(await IsGeneratedAsync(context.Solution, uri, cancellationToken).ConfigureAwait(false));
        }
        return new GeneratedDocumentsFound(generated);
    }

    /// <summary>Whether any document of a URI is generated: a file linked into several projects is one in each, and an
    /// .editorconfig may mark it in one of them.</summary>
    private static async Task<bool> IsGeneratedAsync(Solution solution, string uri, CancellationToken cancel)
    {
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            return false;
        }
        foreach (TextDocument document in await solution.GetTextDocumentsAsync(new DocumentUri(uri), cancel).ConfigureAwait(false))
        {
            if (document is SourceGeneratedDocument
                || document is Document code && code.Project.Services.GetService<IGeneratedCodeRecognitionService>() is { } recognition
                && await recognition.IsGeneratedCodeAsync(code, cancel).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }
}
