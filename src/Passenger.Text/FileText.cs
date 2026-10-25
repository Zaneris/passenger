using System.Text;
using Passenger.Core;

namespace Passenger.Text;

/// <summary>A file's text and how it was stored, so saving writes it back the same way: the same encoding (or UTF-8,
/// when that cannot hold what was typed), a byte-order mark only if it had one, and its own line endings (kept by the
/// document, but for a CR file's lone carriage returns, held as '\n' and written back as they were). A binary file's is only
/// its bytes to look at (<see cref="Bytes"/>), never written back.</summary>
public sealed record FileText(string Text, Encoding Encoding, bool HasBom, bool CrBreaks = false, bool IsBinary = false)
{
    /// <summary>How much of a file is checked for a NUL byte before it is called binary.</summary>
    private const int BinaryProbe = 8000;

    /// <summary>Windows' ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as an IOException's HResult.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>How many times a write another program holds the file against is made, 50 ms apart: two seconds.</summary>
    private const int HeldAttempts = 40;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string EncodingName => FileEncoding.Of(Encoding, HasBom).Name;

    /// <summary>Reads a file as text, or returns null for one that looks binary. A BOM says its encoding; without one
    /// it is read in <paramref name="chosen"/> (one the person chose for it) when that reads every byte, else as UTF-8
    /// if it decodes as UTF-8, and otherwise in the 8-bit encoding its .editorconfig's <c>charset</c> names (latin1),
    /// else the system's (<see cref="FileEncoding.EightBit"/>).</summary>
    public static FileText? Load(string path, Encoding? chosen = null) => FromBytes(Read(path), chosen, () => EditorConfig.For(path).Charset);

    /// <summary>A file's bytes as text, as <see cref="Load"/> reads them (Local History's versions of a file):
    /// <paramref name="configured"/> is the encoding its .editorconfig names, asked only of bytes that are not UTF-8.</summary>
    public static FileText? FromBytes(byte[] bytes, Encoding? chosen = null, Func<FileEncoding?>? configured = null)
    {
        if (Bom(bytes) is { } bom)
        {
            return Decode(bytes, bom);
        }
        if (LooksBinary(bytes))
        {
            return null;
        }
        if (chosen is not null and not UnicodeEncoding && Strict(chosen, bytes) is { } read)
        {
            return Held(read, chosen, false);
        }
        try
        {
            return Held(StrictUtf8.GetString(bytes), new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            Encoding eightBit = configured?.Invoke() is { IsUnicode: false } named ? named.Encoding : FileEncoding.EightBit.Encoding;
            // Latin-1 reads every byte, for one the 8-bit encoding has no character for.
            return Strict(eightBit, bytes) is { } text ? Held(text, eightBit, false) : Held(Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
        }
    }

    /// <summary>A binary file's bytes as text to look at: each byte its Latin-1 character, and a C0 control but tab and
    /// line feed its Control Pictures sign (NUL as ␀, DEL as ␡) and a C1 control a middle dot, so every byte shows and
    /// only a line feed breaks a line.</summary>
    public static FileText Bytes(string path)
    {
        byte[] bytes = Read(path);
        string text = string.Create(bytes.Length, bytes, static (chars, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i] = b switch
                {
                    (byte)'\t' or (byte)'\n' => (char)b,
                    < 0x20 => (char)(0x2400 + b),
                    0x7F => '\u2421',
                    >= 0x80 and < 0xA0 => '\u00B7',
                    _ => (char)b,
                };
            }
        });
        return new FileText(text, Encoding.Latin1, false, IsBinary: true);
    }

    /// <summary>Whether a file's bytes are binary, the one rule every reader of the folder's files keeps (the editor,
    /// Find in Files, Find Usages, Local History): no BOM, and a NUL byte in the first few kilobytes. A UTF-16 file's
    /// BOM makes it text, NULs and all.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes) =>
        Bom(bytes) is null && bytes[..Math.Min(bytes.Length, BinaryProbe)].Contains((byte)0);

    /// <summary>The encoding a byte-order mark at the start of the bytes is, or null without one.</summary>
    public static Encoding? Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? new UTF8Encoding(true)
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? Encoding.Unicode
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode
        : null;

    /// <summary>A file's bytes read in an encoding, leaving out the BOM they start with when it is that encoding's; a
    /// byte it does not read becomes its replacement character.</summary>
    public static FileText Decode(byte[] bytes, Encoding encoding)
    {
        byte[] preamble = Preamble(encoding);
        bool bom = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
        int skip = bom ? preamble.Length : 0;
        return Held(encoding.GetString(bytes, skip, bytes.Length - skip), encoding, bom);
    }

    /// <summary>The bytes of an encoding's BOM, whether or not it writes one itself.</summary>
    public static byte[] Preamble(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        _ => encoding.GetPreamble(),
    };

    /// <summary>A text as the editor holds it: a CR file's (lone carriage returns, and no '\n') with each as '\n', since
    /// the editor's lines end only there.</summary>
    private static FileText Held(string text, Encoding encoding, bool bom) =>
        text.Contains('\r', StringComparison.Ordinal) && !text.Contains('\n', StringComparison.Ordinal)
            ? new FileText(text.Replace('\r', '\n'), encoding, bom, CrBreaks: true)
            : new FileText(text, encoding, bom);

    /// <summary>Bytes read in an encoding that reads every one of them, or null when one is not in it.</summary>
    private static string? Strict(Encoding encoding, byte[] bytes)
    {
        Encoding strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        try
        {
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A file's bytes, read sharing writes and deletes, so the read is never in another program's way (a
    /// checkout that deletes the file first).</summary>
    public static byte[] Read(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>A file about to be written by a save, so a folder watcher takes it for the change it is rather than a
    /// new file (a save writes a temporary file beside it and moves it over). The process's, as saves come from any
    /// window: each window's watcher and each folder's Local History tell their own by its path.</summary>
    public static event Action<string>? Writing;

    /// <summary>A file a save wrote, once it holds the bytes written: its path as given, what it held before (null when
    /// there was no file, or it was too large to keep or could not be read) and the bytes. Local History keeps both.</summary>
    public static event Action<string, byte[]?, byte[]>? Wrote;

    /// <summary>How large a file's content Local History keeps, and so reads before a save writes over it.</summary>
    public const int KeptLimit = 20 * 1024 * 1024;

    /// <summary>The temporary file a save writes first, which nothing but the save should see.</summary>
    public const string SaveSuffix = ".passenger-save";

    /// <summary>Whether a path is a save's temporary file, there beside its file for the moment the save writes: what
    /// reads the folder meanwhile (the folder watcher, Git's status, Go to File's index, the Explorer) passes it over
    /// rather than take it for a new file.</summary>
    public static bool IsSaving(string path) => path.EndsWith(SaveSuffix, StringComparison.Ordinal);

    /// <summary>Writes through a temporary file beside the target, so a failed write never leaves a half-written one; a
    /// symlink is written through to its target, the file keeps what it was (<see cref="Over"/>), one deleted since it
    /// was opened is written anew, a read-only one refused. A text the encoding cannot hold is written as UTF-8, the
    /// result saying so; a CR file (<paramref name="crBreaks"/>) has each break written as '\r'.</summary>
    /// <param name="makeFolder">Whether a gone folder is made again for it (<see cref="FolderGoneException"/>).</param>
    public static (FileText Written, string? Converted) Save(string path, string text, Encoding encoding, bool bom, bool crBreaks = false, bool makeFolder = true)
    {
        string? unheld = Unheld(encoding, text);
        FileText written = unheld is null ? new FileText(text, encoding, bom, crBreaks) : new FileText(text, new UTF8Encoding(false), false, crBreaks);
        Writing?.Invoke(path);
        byte[] preamble = written.HasBom ? Preamble(written.Encoding) : [];
        byte[] body = written.Encoding.GetBytes(crBreaks ? LineEndings.In(text, "\r") : text);
        Write(path, preamble, body, written.Encoding, makeFolder);
        return (written, unheld is null ? null : $"{Path.GetFileName(path)} was saved as UTF-8: {FileEncoding.Of(encoding, bom).Name} cannot hold '{unheld}'");
    }

    /// <summary>Writes a file's bytes as they were, as a save writes: Local History's revert of a file not open, or of one
    /// deleted since.</summary>
    public static void Restore(string path, byte[] bytes)
    {
        Writing?.Invoke(path);
        Write(path, [], bytes, null, makeFolder: true);
    }

    /// <summary>Whether a file (through a symlink, its target) is there and marked read-only, as Perforce and TFVC mark a
    /// file not checked out: by its read-only attribute on Windows, by its write permission for this user elsewhere. No
    /// write of Passenger's replaces one (<see cref="ReadOnlyFileException"/>).</summary>
    public static bool IsReadOnly(string path)
    {
        try
        {
            return new FileInfo(Target(path)) is { Exists: true, IsReadOnly: true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Clear Read-Only Status using the file system: the attribute cleared on Windows, and elsewhere the owner
    /// given write permission.</summary>
    public static void MakeWritable(string path)
    {
        string target = Target(path);
        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
    }

    private static string Target(string path) =>
        new FileInfo(path).LinkTarget is not null ? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path : path;

    /// <param name="encoding">What the text was written in; null for bytes put back as a file had them.</param>
    private static void Write(string path, byte[] preamble, byte[] body, Encoding? encoding, bool makeFolder)
    {
        bool there = File.Exists(path) || new FileInfo(path).LinkTarget is not null;
        string target = there ? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path : path;
        if (!there)
        {
            string folder = Path.GetDirectoryName(target)!;
            if (!makeFolder && !Directory.Exists(folder))
            {
                throw new FolderGoneException(folder);
            }
            Directory.CreateDirectory(folder);
        }
        else if (new FileInfo(target) is { Exists: true, IsReadOnly: true })
        {
            // A move over it would replace it on macOS and Linux, where only its folder's permissions are asked.
            throw new ReadOnlyFileException(path);
        }
        byte[]? before = Wrote is not null && there ? Before(target) : null;
        byte[] bytes = preamble.Length == 0 ? body : [.. preamble, .. body];
        UnixFileMode? mode = !OperatingSystem.IsWindows() && File.Exists(target) ? File.GetUnixFileMode(target) : null;
        // A failed save leaves the old file as it was, and nothing of its own beside it.
        AtomicFile.Write(target, bytes, mode, SaveSuffix, (temporary, at) =>
        {
            if (mode is not null && OperatingSystem.IsMacOS())
            {
                MacFileMetadata.Copy(at, temporary, encoding);
            }
            Replace(temporary, at, bytes);
        });
        Wrote?.Invoke(path, before, bytes);
    }

    /// <summary>What a file about to be written over holds, when Local History would keep it.</summary>
    private static byte[]? Before(string target)
    {
        try
        {
            return new FileInfo(target).Length <= KeptLimit ? Read(target) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves the written file over the target. Windows refuses that while another program holds the target
    /// without sharing its deletion (a virus scanner, a build reading it): the move is tried again for a moment, then the
    /// target is written in place, which such a program allows, so no save (nor the one quitting makes) fails for it; one
    /// sharing no writes either is waited out (<see cref="WhileHeld"/>).</summary>
    private static void Replace(string temporary, string target, byte[] bytes)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Over(temporary, target);
                return;
            }
            catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException && File.Exists(target))
            {
                if (attempt == 3)
                {
                    break;
                }
                Thread.Sleep(50);
            }
        }
        WhileHeld(() =>
        {
            using FileStream stream = new(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
            stream.SetLength(stream.Position);
            stream.Flush(flushToDisk: true);
        });
        File.Delete(temporary);
    }

    /// <summary>Puts the written file in the target's place. On Windows that is ReplaceFile's, which keeps what the target
    /// was (its attributes, Hidden and System among them, its explicit ACL, its alternate data streams, Zone.Identifier's
    /// too, its creation date), where a move gives it the new file's attributes and the folder's inherited ACL.</summary>
    private static void Over(string temporary, string target)
    {
        if (OperatingSystem.IsWindows() && File.Exists(target))
        {
            try
            {
                File.Replace(temporary, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException && !File.Exists(target) && File.Exists(temporary))
            {
                // ERROR_UNABLE_TO_MOVE_REPLACEMENT: the old file went but the new one could not take its name; the move gives it.
            }
        }
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>A write made again while Windows refuses it because another program has the file open sharing no writes,
    /// as a build or a project load reading it does for a moment; any other failure, or that one past two seconds, is
    /// thrown.</summary>
    internal static void WhileHeld(Action write)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (IOException held) when (OperatingSystem.IsWindows() && attempt < HeldAttempts && held.HResult is SharingViolation or LockViolation)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>The first character of a text that an encoding cannot hold (Latin-1 has no € and no emoji), which its
    /// encoder would write as a question mark; null when it holds them all, as every Unicode encoding does.</summary>
    public static string? Unheld(Encoding encoding, string text)
    {
        if (encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding)
        {
            return null;
        }
        Encoding strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            strict.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException unheld)
        {
            return unheld.IsUnknownSurrogate() ? new string([unheld.CharUnknownHigh, unheld.CharUnknownLow]) : unheld.CharUnknown.ToString();
        }
    }

    /// <summary>Whether writing this text in that encoding, with a BOM or without, makes the bytes this file has. ASCII
    /// is the same bytes in UTF-8 and Latin-1, so a Latin-1 file left with only ASCII (read as UTF-8) has not changed.</summary>
    public bool IsStoredAs(Encoding encoding, bool bom) =>
        (bom == HasBom && encoding.CodePage == Encoding.CodePage) || Bytes(encoding, bom).AsSpan().SequenceEqual(Bytes(Encoding, HasBom));

    private byte[] Bytes(Encoding encoding, bool bom) => [.. bom ? Preamble(encoding) : [], .. encoding.GetBytes(Text)];
}

/// <summary>A save refused because the file's folder is no longer there (moved, renamed or deleted by another program),
/// which only a save the person asked for makes again, so an autosave never brings back a folder moved away: an
/// IOException, so every writer's failure says it.</summary>
public sealed class FolderGoneException(string folder) : IOException($"its folder {Path.GetFileName(folder)} was moved or deleted, and only Save makes it again");

/// <summary>A write refused because the file is read-only (<see cref="FileText.IsReadOnly"/>), which only the person's
/// Clear Read-Only Status makes writable: an IOException, so every writer's failure says it.</summary>
public sealed class ReadOnlyFileException(string path) : IOException($"{Path.GetFileName(path)} is read-only");
