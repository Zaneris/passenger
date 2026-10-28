namespace Passenger.Core;

/// <summary>A file written whole or not at all: its bytes go to a temporary file beside it, made new under a name of its
/// own (on macOS and Linux with its mode from the moment it is made, so a secret is never another user's to read), flushed
/// to disk and renamed over it, so a crash, a full disk or a write cut short leaves the file as it was and no reader finds
/// half of the new one. On macOS and Linux the file it replaces gives it first what it keeps (<see cref="UnixFileMetadata"/>),
/// and one another hard link also names is written in place once the temporary file holds its bytes, so the link goes on
/// naming it.</summary>
public static class AtomicFile
{
    /// <summary>What a temporary file's name ends with unless its writer names its own: one left behind is a write that
    /// never finished.</summary>
    public const string Temporary = ".tmp";

    /// <summary>Writes a file whole, the folder it goes in already there. <paramref name="mode"/> is its mode on macOS and
    /// Linux from its making (none: the system's default for a new file); Windows has none.</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) =>
        Write(path, bytes, mode, Temporary, static (temporary, target) => File.Move(temporary, target, overwrite: true));

    /// <summary>Writes a file whole, its temporary file's name ending with <paramref name="suffix"/> and put in the file's
    /// place by <paramref name="put"/> (given the temporary file and the file); a temporary file left by a failure is
    /// deleted, and the failure thrown.</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode, string suffix, Action<string, string> put) =>
        Write(path, bytes, static (stream, bytes) => stream.Write(bytes), mode, suffix, put);

    /// <summary>Writes a file whole as its bytes are made, <paramref name="fill"/> writing them into the temporary file's
    /// stream, so a large one is never held whole; a failure of the fill's is thrown, its temporary file deleted.</summary>
    public static void Write(string path, Action<Stream> fill, UnixFileMode? mode, string suffix, Action<string, string> put) =>
        Write(path, fill, static (stream, fill) => fill(stream), mode, suffix, put);

    private delegate void Filling<TState>(FileStream stream, TState state) where TState : allows ref struct;

    private static void Write<TState>(string path, TState state, Filling<TState> fill, UnixFileMode? mode, string suffix, Action<string, string> put)
        where TState : allows ref struct
    {
        // A name of its own, made new: one already there by a known name (a link a cloned repository holds) took the write wherever it led.
        string full = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(full)!, Guid.NewGuid().ToString("N") + suffix);
        UnixFile? replaced = OperatingSystem.IsWindows() ? null : UnixFileMetadata.Of(full);
        bool made = false;
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (mode is { } given && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = given;
            }
            using (FileStream stream = new(temporary, options))
            {
                made = true;
                if (replaced is { Links: 1 } kept)
                {
                    UnixFileMetadata.Give(full, kept, stream.SafeFileHandle, temporary);
                }
                if (mode is { } exact && !OperatingSystem.IsWindows())
                {
                    // The umask, an ACL given or an owner changed may have taken bits the file is to have: its mode exactly, before any byte is in.
                    File.SetUnixFileMode(stream.SafeFileHandle, exact);
                }
                fill(stream, state);
                stream.Flush(flushToDisk: true);
            }
            if (replaced is { Links: > 1 })
            {
                InPlace(temporary, full, mode);
                Delete(temporary);
            }
            else
            {
                put(temporary, path);
            }
        }
        catch
        {
            if (made)
            {
                Delete(temporary);
            }
            throw;
        }
    }

    /// <summary>The temporary file's bytes written into the file itself, which another hard link names as well: its mode set
    /// first where it is to have another (a secret's), then its bytes, its length and the disk.</summary>
    private static void InPlace(string temporary, string path, UnixFileMode? mode)
    {
        using FileStream from = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream to = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (mode is { } exact && !OperatingSystem.IsWindows() && File.GetUnixFileMode(to.SafeFileHandle) != exact)
        {
            File.SetUnixFileMode(to.SafeFileHandle, exact);
        }
        from.CopyTo(to);
        to.SetLength(to.Position);
        to.Flush(flushToDisk: true);
    }

    private static void Delete(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Held a moment (Windows): left beside the file.
        }
    }
}
