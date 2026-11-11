using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;

namespace Passenger.Roslyn.Extension;

// Each reads the solution the server has open, with the open files as the editor has them, and answers with what it
// would change; the server names each by its type's full name (Handlers in Messages.cs).

public sealed class DescribeSignature : IExtensionDocumentMessageHandler<Caret, Signature>
{
    public Task<Signature> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        Signatures.DescribeAsync(context.Document(), message.At, cancellationToken);
}

public sealed class ChangeSignature : IExtensionDocumentMessageHandler<SignatureChange, Refactored>
{
    public Task<Refactored> ExecuteAsync(SignatureChange message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        Signatures.ChangeAsync(context.Document(), message, cancellationToken);
}

public sealed class DescribeInterface : IExtensionDocumentMessageHandler<Caret, InterfaceSource>
{
    public Task<InterfaceSource> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        Interfaces.DescribeAsync(context.Document(), message.At, cancellationToken);
}

public sealed class ExtractInterface : IExtensionDocumentMessageHandler<InterfaceExtraction, Refactored>
{
    public Task<Refactored> ExecuteAsync(InterfaceExtraction message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        Interfaces.ExtractAsync(context.Document(), message, cancellationToken);
}

public sealed class DescribeType : IExtensionDocumentMessageHandler<Caret, TypeSource>
{
    public Task<TypeSource> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        TypeMoves.DescribeAsync(context.Document(), message.At, cancellationToken);
}

public sealed class MoveTypeToFile : IExtensionDocumentMessageHandler<TypeToFile, Refactored>
{
    public Task<Refactored> ExecuteAsync(TypeToFile message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        TypeMoves.ToFileAsync(context.Document(), message, cancellationToken);
}

public sealed class MoveTypeToNamespace : IExtensionDocumentMessageHandler<TypeToNamespace, Refactored>
{
    public Task<Refactored> ExecuteAsync(TypeToNamespace message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        TypeMoves.ToNamespaceAsync(context.Document(), message, cancellationToken);
}

public sealed class DescribeHierarchy : IExtensionDocumentMessageHandler<Caret, Hierarchy>
{
    public Task<Hierarchy> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        MemberMoves.DescribeAsync(context.Document(), message.At, cancellationToken);
}

public sealed class PullMembersUp : IExtensionDocumentMessageHandler<MembersMove, Refactored>
{
    public Task<Refactored> ExecuteAsync(MembersMove message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        MemberMoves.PullUpAsync(context.Document(), message, cancellationToken);
}

public sealed class PushMembersDown : IExtensionDocumentMessageHandler<MembersMove, Refactored>
{
    public Task<Refactored> ExecuteAsync(MembersMove message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        MemberMoves.PushDownAsync(context.Document(), message, cancellationToken);
}

internal static class DocumentMessages
{
    /// <summary>The C# file a message names: Passenger sends a document's messages only about a .cs file, so its document
    /// is a source document, never an additional or analyzer config file.</summary>
    public static Document Document(this ExtensionDocumentMessageContext context) =>
        context.TextDocument as Document ?? throw new InvalidOperationException($"{context.TextDocument.FilePath} is not a C# document");
}

/// <summary>The files' texts as the server holds them, by their fingerprints: a file changed on disk that the server has
/// not read again since differs from the disk, and an edit placed by the server's lines would land elsewhere in it.</summary>
public sealed class DocumentTexts : IExtensionWorkspaceMessageHandler<TextsAsked, TextsHeld>
{
    public async Task<TextsHeld> ExecuteAsync(TextsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<TextHeld> held = [];
        foreach (string path in message.Paths)
        {
            held.Add(new TextHeld(path, await HashOfAsync(context.Solution, path, cancellationToken).ConfigureAwait(false)));
        }
        return new TextsHeld(held);
    }

    /// <summary>The <see cref="TextHash"/> of the text a solution holds for a path, null where it has no document of it.
    /// The solution finds a path's documents ignoring case, so one of a name differing only in letter case, another file
    /// where the file system tells case apart (a file renamed so), is passed over there.</summary>
    internal static async Task<string?> HashOfAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        TextDocument? document = solution.GetDocumentIdsWithFilePath(path)
            .Select(id => (TextDocument?)solution.GetDocument(id) ?? solution.GetAdditionalDocument(id) ?? solution.GetAnalyzerConfigDocument(id))
            .FirstOrDefault(d => d?.FilePath is { } held && Passenger.Core.PathNames.Same(held, path));
        return document is null ? null : TextHash.Of((await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString());
    }
}

/// <summary>The solution's documents written on disk since a time, or gone from it since, the nearest folder above that is
/// still there written then, and the changed files asked of that are documents, with the texts held of them: those the
/// server may hold as they were.</summary>
public sealed class DocumentsWritten : IExtensionWorkspaceMessageHandler<WrittenAsked, WrittenFound>
{
    public async Task<WrittenFound> ExecuteAsync(WrittenAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<TextHeld> changed = [];
        foreach (string path in message.Changed)
        {
            if (await DocumentTexts.HashOfAsync(context.Solution, path, cancellationToken).ConfigureAwait(false) is { } hash)
            {
                changed.Add(new TextHeld(path, hash));
            }
        }
        return new WrittenFound(Written(message.Since, context.Solution, cancellationToken), changed);
    }

    private static List<DocumentWritten> Written(DateTime since, Solution solution, CancellationToken cancellationToken)
    {
        List<DocumentWritten> found = [];
        HashSet<string> looked = new(StringComparer.Ordinal);
        foreach (Project project in solution.Projects)
        {
            foreach (TextDocument document in project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is not { } path || !looked.Add(path))
                {
                    continue;
                }
                try
                {
                    FileInfo file = new(path);
                    if (file.Exists ? file.LastWriteTimeUtc >= since : FolderWritten(path) >= since)
                    {
                        found.Add(new DocumentWritten(path, Gone: !file.Exists));
                    }
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    // One whose time cannot be read is not said written.
                }
            }
        }
        return found;
    }

    /// <summary>When the nearest folder above a path that is there was last written: a file taken out of it writes it.</summary>
    private static DateTime FolderWritten(string path)
    {
        for (DirectoryInfo? folder = new FileInfo(path).Directory; folder is not null; folder = folder.Parent)
        {
            if (folder.Exists)
            {
                return folder.LastWriteTimeUtc;
            }
        }
        return DateTime.MinValue;
    }
}
