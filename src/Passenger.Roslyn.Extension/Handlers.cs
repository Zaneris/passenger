using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;

namespace Passenger.Roslyn.Extension;

// Each reads the solution the server has open, with the open files as the editor has them, and answers with what it
// would change; the server names each by its type's full name (Handlers in Messages.cs).

public sealed class DescribeSignature : IExtensionDocumentMessageHandler<Caret, Signature>
{
    public Task<Signature> ExecuteAsync(Caret message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        Signatures.DescribeAsync(document, message.At, cancellationToken);
}

public sealed class ChangeSignature : IExtensionDocumentMessageHandler<SignatureChange, Refactored>
{
    public Task<Refactored> ExecuteAsync(SignatureChange message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        Signatures.ChangeAsync(document, message, cancellationToken);
}

public sealed class DescribeInterface : IExtensionDocumentMessageHandler<Caret, InterfaceSource>
{
    public Task<InterfaceSource> ExecuteAsync(Caret message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        Interfaces.DescribeAsync(document, message.At, cancellationToken);
}

public sealed class ExtractInterface : IExtensionDocumentMessageHandler<InterfaceExtraction, Refactored>
{
    public Task<Refactored> ExecuteAsync(InterfaceExtraction message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        Interfaces.ExtractAsync(document, message, cancellationToken);
}

public sealed class DescribeType : IExtensionDocumentMessageHandler<Caret, TypeSource>
{
    public Task<TypeSource> ExecuteAsync(Caret message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        TypeMoves.DescribeAsync(document, message.At, cancellationToken);
}

public sealed class MoveTypeToFile : IExtensionDocumentMessageHandler<TypeToFile, Refactored>
{
    public Task<Refactored> ExecuteAsync(TypeToFile message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        TypeMoves.ToFileAsync(document, message, cancellationToken);
}

public sealed class MoveTypeToNamespace : IExtensionDocumentMessageHandler<TypeToNamespace, Refactored>
{
    public Task<Refactored> ExecuteAsync(TypeToNamespace message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        TypeMoves.ToNamespaceAsync(document, message, cancellationToken);
}

public sealed class DescribeHierarchy : IExtensionDocumentMessageHandler<Caret, Hierarchy>
{
    public Task<Hierarchy> ExecuteAsync(Caret message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        MemberMoves.DescribeAsync(document, message.At, cancellationToken);
}

public sealed class PullMembersUp : IExtensionDocumentMessageHandler<MembersMove, Refactored>
{
    public Task<Refactored> ExecuteAsync(MembersMove message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        MemberMoves.PullUpAsync(document, message, cancellationToken);
}

public sealed class PushMembersDown : IExtensionDocumentMessageHandler<MembersMove, Refactored>
{
    public Task<Refactored> ExecuteAsync(MembersMove message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken) =>
        MemberMoves.PushDownAsync(document, message, cancellationToken);
}

/// <summary>The files' texts as the server holds them, by their fingerprints: a file changed on disk that the server has
/// not read again since differs from the disk, and an edit placed by the server's lines would land elsewhere in it.</summary>
public sealed class DocumentTexts : IExtensionWorkspaceMessageHandler<TextsAsked, TextsHeld>
{
    public async Task<TextsHeld> ExecuteAsync(TextsAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        List<TextHeld> held = [];
        foreach (string path in message.Paths)
        {
            Solution solution = context.Solution;
            TextDocument? document = solution.GetDocumentIdsWithFilePath(path)
                .Select(id => (TextDocument?)solution.GetDocument(id) ?? solution.GetAdditionalDocument(id) ?? solution.GetAnalyzerConfigDocument(id))
                .FirstOrDefault(d => d is not null);
            held.Add(new TextHeld(path, document is null ? null : TextHash.Of((await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString())));
        }
        return new TextsHeld(held);
    }
}

/// <summary>The solution's documents written on disk since a time, or gone from it since, the nearest folder above that is
/// still there written then: those the server may hold as they were.</summary>
public sealed class DocumentsWritten : IExtensionWorkspaceMessageHandler<WrittenAsked, WrittenFound>
{
    public Task<WrittenFound> ExecuteAsync(WrittenAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        List<DocumentWritten> found = [];
        HashSet<string> looked = new(StringComparer.Ordinal);
        foreach (Project project in context.Solution.Projects)
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
                    if (file.Exists ? file.LastWriteTimeUtc >= message.Since : FolderWritten(path) >= message.Since)
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
        return Task.FromResult(new WrittenFound(found));
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
