using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>The documents of a server's solution it may hold as they were, found for a command a moment after the command
/// asked it: those written on disk since a time or gone since, and those heard changed that no command has yet seen it
/// hold as the disk has them (<see cref="WatchedFiles.Changing"/>), however long ago, each with the fingerprint of the text
/// it holds. The command settles those (<see cref="Settle"/>) once it has judged them.</summary>
public sealed class WrittenLately
{
    private readonly WatchedFiles watched;
    private readonly IReadOnlyList<string> asked;
    private readonly long asOf;

    private WrittenLately(IReadOnlyList<DocumentWritten> written, IReadOnlyList<TextHeld> changed, WatchedFiles watched, IReadOnlyList<string> asked, long asOf)
    {
        Written = written;
        Changed = changed;
        this.watched = watched;
        this.asked = asked;
        this.asOf = asOf;
    }

    /// <summary>The documents written on disk since the time asked, or gone since.</summary>
    public IReadOnlyList<DocumentWritten> Written { get; }

    /// <summary>The documents heard changed, with the fingerprint of each text the server holds.</summary>
    public IReadOnlyList<TextHeld> Changed { get; }

    /// <summary>Lets go of the files heard changed but those the command waits for (<paramref name="behind"/>), those the
    /// server was found holding otherwise than the disk among them (<paramref name="unread"/>) only after a while
    /// (<see cref="WatchedFiles.Settle"/>).</summary>
    public void Settle(IReadOnlyCollection<string> behind, IReadOnlyCollection<string> unread) => watched.Settle(asked, asOf, behind, unread);

    internal static async Task<WrittenLately> AskAsync(LanguageServer server, DateTime since, CancellationToken cancel)
    {
        (IReadOnlyList<string> changing, long asOf) = server.WatchedFiles.Changing();
        WrittenFound found = await RoslynExtension.AskWorkspaceAsync<WrittenFound>(server, Handlers.DocumentsWritten, new WrittenAsked(since, changing), cancel)
            .ConfigureAwait(false);
        return new WrittenLately(found.Documents, found.Changed, server.WatchedFiles, changing, asOf);
    }
}

/// <summary>Whether a text the server holds, by its fingerprint (<see cref="TextHash"/>), is a file's on disk.</summary>
public static class HeldTexts
{
    /// <summary>Whether a file's text on disk is the one of this fingerprint, read as Passenger reads it or as the server
    /// may: as UTF-8 whatever the bytes (Roslyn's fallback off Windows). Null when it cannot be read.</summary>
    public static bool? OnDisk(string path, string hash)
    {
        byte[] bytes;
        try
        {
            bytes = FileText.Read(path);
        }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return FileText.FromBytes(bytes, null, () => EditorConfig.For(path).Charset) is not { } disk || TextHash.Of(disk.Text) == hash
            || TextHash.Of(new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('﻿')) == hash;
    }
}
