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

    /// <summary>How many times a write another program holds the file against is made, <see cref="HeldPause"/> apart: two
    /// seconds.</summary>
    private const int HeldAttempts = 40;

    /// <summary>How long a save another program holds its file against waits before it tries again.</summary>
    private static readonly TimeSpan HeldPause = TimeSpan.FromMilliseconds(50);

    /// <summary>Saves still landing, by the file each writes (a link's target): one whose file another program held
    /// (Windows), then each save of that file made meanwhile, which lands after it. Each has the last one's task, how many
    /// are left, and the bytes the last one lands, which a read of the file gets meanwhile.</summary>
    private static readonly Dictionary<string, (Task<Exception?> Last, int Count, byte[] Bytes)> Landing = new(PathNames.Comparer);

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

    /// <summary>The encoding a byte-order mark at the start of the bytes is, or null without one. UTF-32 LE's mark begins
    /// with UTF-16 LE's, so it is asked first.</summary>
    public static Encoding? Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? new UTF8Encoding(true)
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]) ? new UTF32Encoding(bigEndian: false, byteOrderMark: true)
        : bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]) ? new UTF32Encoding(bigEndian: true, byteOrderMark: true)
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
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
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
    /// checkout that deletes the file first); while a save of it is still landing, the bytes it lands, which the file is
    /// about to hold (<see cref="Landed(string)"/>).</summary>
    public static byte[] Read(string path) => LandingBytes(path) ?? FromDisk(path);

    /// <summary>The bytes the last save of a file still landing lands, or null when none is.</summary>
    private static byte[]? LandingBytes(string path)
    {
        lock (Landing)
        {
            if (Landing.Count == 0)
            {
                return null;
            }
        }
        string target = Path.GetFullPath(Target(path));
        lock (Landing)
        {
            return Landing.TryGetValue(target, out (Task<Exception?> Last, int Count, byte[] Bytes) landing) ? landing.Bytes : null;
        }
    }

    /// <summary>The save of a file still landing last, which ends once it has, with what kept it from landing (or null);
    /// null when none is. A save Windows refuses while another program holds the file lands off the caller's thread, the
    /// saves of that file made meanwhile after it, in order.</summary>
    public static Task<Exception?>? Landed(string path)
    {
        lock (Landing)
        {
            if (Landing.Count == 0)
            {
                return null;
            }
        }
        string target = Path.GetFullPath(Target(path));
        lock (Landing)
        {
            return Landing.TryGetValue(target, out (Task<Exception?> Last, int Count, byte[] Bytes) landing) ? landing.Last : null;
        }
    }

    /// <summary>Each file with a save still landing (a link's target), and the last of its saves (<see cref="Landed(string)"/>).</summary>
    public static IReadOnlyList<(string Path, Task<Exception?> Last)> Landings()
    {
        lock (Landing)
        {
            return [.. Landing.Select(l => (l.Key, l.Value.Last))];
        }
    }

    /// <summary>Every save still landing, ended: the process's last wait, so none is cut short.</summary>
    public static Task AllLanded()
    {
        lock (Landing)
        {
            return Task.WhenAll(Landing.Values.Select(l => l.Last));
        }
    }

    private static byte[] FromDisk(string path)
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
    /// there was no file, or it was too large to keep or could not be read) and the bytes. Local History keeps both.
    /// Raised on the saving thread, or the pool's for a save that landed later (<see cref="Landed(string)"/>).</summary>
    public static event Action<string, byte[]?, byte[]>? Wrote;

    /// <summary>How large a file's content Local History keeps, and so reads before a save writes over it.</summary>
    public const int KeptLimit = 20 * 1024 * 1024;

    /// <summary>The temporary file a save writes first, and the old file a replace sets aside, which nothing but the save
    /// should see.</summary>
    public const string SaveSuffix = ".passenger-save";

    /// <summary>Whether a path is a save's temporary file, there beside its file for the moment the save writes: what
    /// reads the folder meanwhile (the folder watcher, Git's status, Go to File's index, the Explorer) passes it over
    /// rather than take it for a new file.</summary>
    public static bool IsSaving(string path) => IsSaving(path.AsSpan());

    /// <inheritdoc cref="IsSaving(string)"/>
    public static bool IsSaving(ReadOnlySpan<char> path) => path.EndsWith(SaveSuffix, StringComparison.Ordinal);

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
        Write(path, Encoded(text, written.Encoding, written.HasBom, crBreaks), written.Encoding, makeFolder);
        return (written, unheld is null ? null : $"{Path.GetFileName(path)} was saved as UTF-8: {FileEncoding.Of(encoding, bom).Name} cannot hold '{unheld}'");
    }

    /// <summary>A text's bytes as a save writes them: in the encoding, after its BOM when it has one, a CR file's breaks
    /// as '\r'.</summary>
    public static byte[] Encoded(string text, Encoding encoding, bool bom, bool crBreaks = false)
    {
        byte[] body = encoding.GetBytes(crBreaks ? LineEndings.In(text, "\r") : text);
        return bom ? [.. Preamble(encoding), .. body] : body;
    }

    /// <summary>Writes a file's bytes as they were, as a save writes: Local History's revert of a file not open, or of one
    /// deleted since.</summary>
    public static void Restore(string path, byte[] bytes)
    {
        Writing?.Invoke(path);
        Write(path, bytes, null, makeFolder: true);
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
    private static void Write(string path, byte[] bytes, Encoding? encoding, bool makeFolder)
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
        UnixFileMode? mode = !OperatingSystem.IsWindows() && File.Exists(target) ? File.GetUnixFileMode(target) : null;
        target = Path.GetFullPath(target);
        lock (Landing)
        {
            if (Landing.TryGetValue(target, out (Task<Exception?> Last, int Count, byte[] Bytes) earlier))
            {
                // After the save still landing, so the file takes them in order and Local History each one's old bytes.
                Task<Exception?> after = BackgroundWork.Run(() => LandAsync(path, target, bytes, mode, encoding, earlier.Last, null));
                Landing[target] = (after, earlier.Count + 1, bytes);
                return;
            }
        }
        byte[]? before = Wrote is not null && there ? Before(target) : null;
        if (Put(target, bytes, mode, encoding) is { } held)
        {
            lock (Landing)
            {
                int racing = Landing.TryGetValue(target, out (Task<Exception?> Last, int Count, byte[] Bytes) other) ? other.Count : 0;
                Landing[target] = (BackgroundWork.Run(() => LandAsync(path, target, bytes, mode, encoding, null, (held, before))), racing + 1, bytes);
            }
            return;
        }
        Wrote?.Invoke(path, before, bytes);
    }

    /// <summary>Writes the temporary file and puts it in the target's place: null once it is there, else the temporary
    /// file, left for a later try, Windows having refused it while another program holds the target (a virus scanner, a
    /// build reading it). A failed write leaves the old file as it was, and nothing of its own beside it. A target another
    /// hard link names is written in place on macOS and Linux (<see cref="AtomicFile"/>), keeping all it had.</summary>
    private static string? Put(string target, byte[] bytes, UnixFileMode? mode, Encoding? encoding)
    {
        string? held = null;
        bool moved = false;
        AtomicFile.Write(target, bytes, mode, SaveSuffix, (temporary, at) =>
        {
            moved = true;
            if (mode is not null && OperatingSystem.IsMacOS())
            {
                MacFileMetadata.Copy(at, temporary, encoding);
            }
            held = Placed(temporary, at) ? null : temporary;
        });
        if (!moved && OperatingSystem.IsMacOS())
        {
            MacFileMetadata.Encoded(target, encoding);
        }
        return held;
    }

    /// <summary>A save landing off the caller's thread: after the <paramref name="earlier"/> one, written whole (its old
    /// bytes read then), or one whose temporary file is <paramref name="held"/>. The move is tried again for a moment,
    /// then the target written in place, which a program holding it without sharing its deletion allows; one sharing no
    /// writes either is waited out (<see cref="WhileHeldAsync"/>). What kept it from landing, or null.</summary>
    private static async Task<Exception?> LandAsync(string path, string target, byte[] bytes, UnixFileMode? mode, Encoding? encoding,
        Task<Exception?>? earlier, (string Temporary, byte[]? Before)? held)
    {
        string? temporary = held?.Temporary;
        try
        {
            byte[]? before = held?.Before;
            if (earlier is not null)
            {
                await earlier;
                Writing?.Invoke(path);
                before = Wrote is not null && File.Exists(target) ? Before(target) : null;
                temporary = Put(target, bytes, mode, encoding);
            }
            for (int attempt = 2; temporary is not null && attempt <= 3; attempt++)
            {
                await Task.Delay(HeldPause);
                temporary = Placed(temporary, target) ? null : temporary;
            }
            if (temporary is not null)
            {
                await WhileHeldAsync(() =>
                {
                    using FileStream stream = new(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(bytes);
                    stream.SetLength(stream.Position);
                    stream.Flush(flushToDisk: true);
                });
                File.Delete(temporary);
                temporary = null;
            }
            Wrote?.Invoke(path, before, bytes);
            return null;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            if (temporary is not null)
            {
                Forget(temporary);
            }
            PassengerLog.Write($"A save of {target} did not land: {refused.Message}");
            return refused;
        }
        finally
        {
            lock (Landing)
            {
                if (Landing.TryGetValue(target, out (Task<Exception?> Last, int Count, byte[] Bytes) left))
                {
                    if (left.Count == 1)
                    {
                        Landing.Remove(target);
                    }
                    else
                    {
                        Landing[target] = left with { Count = left.Count - 1 };
                    }
                }
            }
        }
    }

    /// <summary>What a file about to be written over holds, when Local History would keep it.</summary>
    private static byte[]? Before(string target)
    {
        try
        {
            return new FileInfo(target).Length <= KeptLimit ? FromDisk(target) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves the written file over the target, once; false when Windows refuses that while another program
    /// holds the target without sharing its deletion, the written file left where it is.</summary>
    private static bool Placed(string temporary, string target)
    {
        try
        {
            Over(temporary, target);
            return true;
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException && File.Exists(target))
        {
            return false;
        }
    }

    /// <summary>Puts the written file in the target's place: a move, but on Windows over a target with what a move would
    /// lose (<see cref="WindowsFileMetadata.MoveKeeps"/>: its attributes, its own ACL, its streams, Zone.Identifier's too),
    /// where ReplaceFile keeps them. The old file it sets aside meanwhile is named as a save's own
    /// (<see cref="IsSaving(string)"/>), never as a file come and gone, and deleted once the new one has its place.</summary>
    private static void Over(string temporary, string target)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(target) || WindowsFileMetadata.MoveKeeps(target, temporary))
        {
            File.Move(temporary, target, overwrite: true);
            return;
        }
        string setAside = Path.Join(Path.GetDirectoryName(target), Guid.NewGuid().ToString("N") + SaveSuffix);
        try
        {
            File.Replace(temporary, target, setAside, ignoreMetadataErrors: true);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException && !File.Exists(target) && File.Exists(temporary))
        {
            // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the old file is set aside but the new one could not take its name; the move gives it.
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            // Kept while it is the only copy left.
            if (File.Exists(target))
            {
                Forget(setAside);
            }
        }
    }

    /// <summary>Deletes the file a replace set aside; one held a moment (Windows) is left beside the file.</summary>
    private static void Forget(string setAside)
    {
        try
        {
            File.Delete(setAside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A write made again while Windows refuses it because another program has the file open sharing no writes,
    /// as a build or a project load reading it does for a moment; any other failure, or that one past two seconds, is
    /// thrown. Off the caller's thread, which never waits for it.</summary>
    internal static async Task WhileHeldAsync(Action write)
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
            }
            await Task.Delay(HeldPause);
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
