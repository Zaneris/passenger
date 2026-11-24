using System.Collections.Specialized;
using System.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.GeneratedCodeRecognition;
using Microsoft.CodeAnalysis.LanguageServer;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>Answers once the source generators' runs the server has queued have run: a load that changes a project's
/// analyzers queues one, run a moment later, and until then the project's generated code is what its generators made
/// before, none of a generator just come. Its answer names the files in each project's intermediate output folder that the
/// server holds otherwise than the disk: a load takes a design-time build's documents by their paths, so one it held
/// before keeps the text it read until it hears the file changed, though the build has written it again (the analyzers'
/// settings a generator reads, the global usings). It reads nothing under a folder about to go or move, as Windows moves no
/// folder with a file under it open.</summary>
public sealed class GeneratorsRun : IExtensionWorkspaceMessageHandler<GeneratorsAsked, GeneratorsRan>
{
    public async Task<GeneratorsRan> ExecuteAsync(GeneratorsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Microsoft.CodeAnalysis.Workspace workspace = context.Solution.Workspace;
        await workspace._updateSourceGeneratorsQueue.WaitUntilCurrentBatchCompletesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GeneratorsRan(await UnreadAsync(workspace.CurrentSolution, message.Passing ?? [], cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The documents of every kind in a project's intermediate output folder, where its builds write, whose text the
    /// solution holds otherwise than the disk; one gone from disk, or under a folder <paramref name="passing"/> names, is
    /// passed over.</summary>
    private static async Task<List<string>> UnreadAsync(Solution solution, IReadOnlyList<string> passing, CancellationToken cancellationToken)
    {
        List<string> unread = [];
        foreach (Project project in solution.Projects)
        {
            if (Path.GetDirectoryName(project.CompilationOutputInfo.AssemblyPath) is not { Length: > 0 } built)
            {
                continue;
            }
            foreach (TextDocument document in project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
            {
                if (document.FilePath is not { } path || !Passenger.Core.PathNames.IsUnder(path, built) || unread.Contains(path)
                    || passing.Any(folder => Passenger.Core.PathNames.Within(path, folder)))
                {
                    continue;
                }
                string disk;
                try
                {
                    disk = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (TextHash.Of((await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString()) != TextHash.Of(disk))
                {
                    unread.Add(path);
                }
            }
        }
        return unread;
    }
}

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

/// <summary>The documents the source generators of the projects named make now, each project's build of every target
/// framework, their generators run where the server has not run them yet.</summary>
public sealed class GeneratedListed : IExtensionWorkspaceMessageHandler<GeneratedListAsked, GeneratedListFound>
{
    public async Task<GeneratedListFound> ExecuteAsync(GeneratedListAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        HashSet<string> asked = new(message.Projects, Passenger.Core.PathNames.Comparer);
        List<GeneratedFile> files = [];
        foreach (Project project in context.Solution.Projects.Where(p => p.FilePath is { } path && asked.Contains(path)))
        {
            string? framework = project.State.NameAndFlavor.flavor;
            foreach (SourceGeneratedDocument document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add(new GeneratedFile(project.FilePath!, framework, document.Identity.Generator.TypeName, document.Identity.Generator.AssemblyName,
                    document.HintName, SourceGeneratedDocumentUri.Create(document.Identity).UriString));
            }
        }
        return new GeneratedListFound(files);
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
