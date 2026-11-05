using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>What each closed C# file of a folder's test projects declared for testing when last read, kept across
/// openings in the state's cache (cache/tests/, a file per folder), so an opening parses only the files changed since. An
/// entry stands while its file's length, write time and content hash, and what it was read with (its project's symbols
/// and derived test attributes), are as kept, a file keeping its last few readings, each read with other symbols or
/// attributes; one gone from the disk and from every test project's listing is dropped as the cache is written. A file another version of Passenger or another reading of tests
/// (<see cref="Schema"/>) wrote is passed over whole. Any thread.</summary>
internal sealed class TestsCache(string path)
{
    /// <summary>The reading of tests the entries hold: raised whenever <see cref="TestsAtCaret"/> reads a file otherwise, or
    /// the entries are written otherwise.</summary>
    public const int Schema = 2;

    private const string Magic = "passenger-tests";

    /// <summary>The most readings kept of a file, each with other symbols or attributes.</summary>
    private const int MostReadings = 3;

    private readonly Lazy<Held> held = new(() => Load(path), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lock writing = new();
    private int changes;

    /// <summary>A file as it was read: its length, last write and the hash of its bytes.</summary>
    public readonly record struct Stamp(long Length, long Written, ulong Hash, ulong MoreHash);

    private sealed record Entry(Stamp Stamp, string ReadWith, FileTests Tests);

    /// <summary>The entries by file, newest first, and the attributes each test project's files were last read with, by its
    /// path.</summary>
    private sealed record Held(ConcurrentDictionary<string, Entry[]> Files, ConcurrentDictionary<string, IReadOnlySet<string>> Attributes);

    /// <summary>Where the cache of a folder's tests lives under a state root.</summary>
    public static string PathFor(string root, string folder) =>
        Path.Combine(root, "cache", "tests", Path.GetFileName(State.ProjectState.FolderOf(root, folder)) + ".bin");

    /// <summary>A file's stamp from the bytes just read of it.</summary>
    public static Stamp StampOf(string file, ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return new Stamp(bytes.Length, File.GetLastWriteTimeUtc(file).Ticks, BitConverter.ToUInt64(hash), BitConverter.ToUInt64(hash[8..]));
    }

    /// <summary>What a file is read with, as an entry holds it: its project's language version and symbols, and the test
    /// attributes its project derives.</summary>
    public static string ReadWith(CSharpParseOptions symbols, IReadOnlySet<string> attributes) =>
        $"{symbols.LanguageVersion}|{string.Join(';', symbols.PreprocessorSymbolNames.Order(StringComparer.Ordinal))}|{string.Join(';', attributes.Order(StringComparer.Ordinal))}";

    /// <summary>What a file declared when it was last read as it stands now, read so; null where it was not.</summary>
    public FileTests? Find(string file, Stamp stamp, string readWith) =>
        held.Value.Files.TryGetValue(file, out Entry[]? entries) && entries.FirstOrDefault(e => e.Stamp == stamp && e.ReadWith == readWith) is { } entry ? entry.Tests : null;

    public void Keep(string file, Stamp stamp, string readWith, FileTests tests)
    {
        Entry made = new(stamp, readWith, tests);
        held.Value.Files.AddOrUpdate(file, static (_, made) => [made],
            static (_, kept, made) => [made, .. kept.Where(e => e.ReadWith != made.ReadWith && e.Stamp == made.Stamp).Take(MostReadings - 1)], made);
        Interlocked.Increment(ref changes);
    }

    /// <summary>The test attributes a test project's files were last read with; null for a project never read.</summary>
    public IReadOnlySet<string>? AttributesOf(string project) => held.Value.Attributes.GetValueOrDefault(project);

    public void KeepAttributes(string project, IReadOnlySet<string> attributes)
    {
        if (AttributesOf(project) is not { } kept || !kept.SetEquals(attributes))
        {
            held.Value.Attributes[project] = attributes;
            Interlocked.Increment(ref changes);
        }
    }

    /// <summary>Writes what changed since the last write; given every test project's files and the projects, leaving out the
    /// entries of files and the attributes of projects they no longer name that are gone from the disk.</summary>
    public void Save(IReadOnlySet<string>? every = null, IReadOnlySet<string>? projects = null)
    {
        lock (writing)
        {
            Held kept = held.Value;
            int dropped = 0;
            if (every is not null)
            {
                foreach (string file in kept.Files.Keys.Where(f => !every.Contains(f) && !File.Exists(f)).ToList())
                {
                    dropped += kept.Files.TryRemove(file, out _) ? 1 : 0;
                }
            }
            if (projects is not null)
            {
                foreach (string project in kept.Attributes.Keys.Where(p => !projects.Contains(p) && !File.Exists(p)).ToList())
                {
                    dropped += kept.Attributes.TryRemove(project, out _) ? 1 : 0;
                }
            }
            if (Interlocked.Exchange(ref changes, 0) + dropped == 0)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, stream => Write(stream, kept), mode: null, AtomicFile.Temporary, static (temporary, target) => File.Move(temporary, target, overwrite: true));
        }
    }

    /// <summary>How many bytes the cache's file holds; zero for none.</summary>
    public long Size => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>The version of Passenger the entries were read by: another's are passed over.</summary>
    private static string Writer => $"{Product.Version}+{Product.Commit}";

    private static Held Load(string path)
    {
        Held empty = new(new(PathNames.Comparer), new(PathNames.Comparer));
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using BinaryReader reader = new(stream, Encoding.UTF8);
            if (reader.ReadString() != Magic || reader.ReadInt32() != Schema || reader.ReadString() != Writer)
            {
                return empty;
            }
            Strings strings = new(reader, new string[Count(reader)]);
            for (int i = 0; i < strings.All.Length; i++)
            {
                strings.All[i] = reader.ReadString();
            }
            Held kept = new(new(PathNames.Comparer), new(PathNames.Comparer));
            for (int count = Count(reader); count > 0; count--)
            {
                string project = strings.Next();
                kept.Attributes[project] = Set(strings);
            }
            for (int count = Count(reader); count > 0; count--)
            {
                string file = strings.Next();
                Entry[] entries = new Entry[Count(reader)];
                for (int i = 0; i < entries.Length; i++)
                {
                    Stamp stamp = new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt64(), reader.ReadUInt64());
                    entries[i] = new Entry(stamp, strings.Next(), ReadTests(strings));
                }
                kept.Files[file] = entries;
            }
            return kept;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            // None yet, one cut short or another's: the files are read again and it is written anew.
            return empty;
        }
    }

    /// <summary>A count the file gives, refused where no file of its length could hold that many.</summary>
    private static int Count(BinaryReader reader)
    {
        int count = reader.Read7BitEncodedInt();
        return count >= 0 && count <= reader.BaseStream.Length ? count : throw new InvalidDataException($"A count of {count} in the cache of tests");
    }

    /// <summary>The table of strings a file holds, and the reader taking each by its number.</summary>
    private sealed record Strings(BinaryReader Reader, string[] All)
    {
        public string Next()
        {
            int number = Reader.Read7BitEncodedInt();
            return number >= 0 && number < All.Length ? All[number] : throw new InvalidDataException($"String {number} of {All.Length} in the cache of tests");
        }
    }

    private static FileTests ReadTests(Strings strings)
    {
        BinaryReader reader = strings.Reader;
        (string, int)[] types = new (string, int)[Count(reader)];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = (strings.Next(), reader.Read7BitEncodedInt());
        }
        DeclaredTest[] tests = new DeclaredTest[Count(reader)];
        for (int i = 0; i < tests.Length; i++)
        {
            string type = strings.Next();
            string method = strings.Next();
            int line = reader.Read7BitEncodedInt();
            bool inherited = reader.ReadBoolean();
            KeyValuePair<string, string>[] traits = new KeyValuePair<string, string>[Count(reader)];
            for (int t = 0; t < traits.Length; t++)
            {
                traits[t] = KeyValuePair.Create(strings.Next(), strings.Next());
            }
            string[] cases = new string[Count(reader)];
            for (int c = 0; c < cases.Length; c++)
            {
                cases[c] = strings.Next();
            }
            tests[i] = new DeclaredTest(type, method, line, traits, inherited) { Cases = cases, Code = reader.ReadUInt64() };
        }
        HashSet<string> methods = Set(strings);
        return new FileTests(types, tests, methods) { Attributes = Set(strings) };
    }

    private static HashSet<string> Set(Strings strings)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        for (int count = Count(strings.Reader); count > 0; count--)
        {
            set.Add(strings.Next());
        }
        return set;
    }

    /// <summary>The entries written after a table of the strings they hold, each string once: a type's name is every one of
    /// its tests'.</summary>
    private static void Write(Stream stream, Held kept)
    {
        Dictionary<string, int> numbers = new(StringComparer.Ordinal);
        List<string> strings = [];
        int Number(string text)
        {
            if (!numbers.TryGetValue(text, out int number))
            {
                numbers[text] = number = strings.Count;
                strings.Add(text);
            }
            return number;
        }
        KeyValuePair<string, IReadOnlySet<string>>[] attributes = [.. kept.Attributes];
        KeyValuePair<string, Entry[]>[] files = [.. kept.Files];
        using MemoryStream body = new();
        using (BinaryWriter entries = new(body, Encoding.UTF8, leaveOpen: true))
        {
            entries.Write7BitEncodedInt(attributes.Length);
            foreach ((string project, IReadOnlySet<string> set) in attributes)
            {
                entries.Write7BitEncodedInt(Number(project));
                WriteSet(entries, set, Number);
            }
            entries.Write7BitEncodedInt(files.Length);
            foreach ((string file, Entry[] readings) in files)
            {
                entries.Write7BitEncodedInt(Number(file));
                entries.Write7BitEncodedInt(readings.Length);
                foreach (Entry entry in readings)
                {
                    entries.Write(entry.Stamp.Length);
                    entries.Write(entry.Stamp.Written);
                    entries.Write(entry.Stamp.Hash);
                    entries.Write(entry.Stamp.MoreHash);
                    entries.Write7BitEncodedInt(Number(entry.ReadWith));
                    WriteTests(entries, entry.Tests, Number);
                }
            }
        }
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Schema);
        writer.Write(Writer);
        writer.Write7BitEncodedInt(strings.Count);
        foreach (string text in strings)
        {
            writer.Write(text);
        }
        writer.Flush();
        body.Position = 0;
        body.CopyTo(stream);
    }

    private static void WriteTests(BinaryWriter writer, FileTests read, Func<string, int> number)
    {
        writer.Write7BitEncodedInt(read.Types.Count);
        foreach ((string type, int line) in read.Types)
        {
            writer.Write7BitEncodedInt(number(type));
            writer.Write7BitEncodedInt(line);
        }
        writer.Write7BitEncodedInt(read.Tests.Count);
        foreach (DeclaredTest test in read.Tests)
        {
            writer.Write7BitEncodedInt(number(test.Type));
            writer.Write7BitEncodedInt(number(test.Method));
            writer.Write7BitEncodedInt(test.Line);
            writer.Write(test.Abstract);
            writer.Write7BitEncodedInt(test.Traits.Count);
            foreach ((string key, string value) in test.Traits)
            {
                writer.Write7BitEncodedInt(number(key));
                writer.Write7BitEncodedInt(number(value));
            }
            writer.Write7BitEncodedInt(test.Cases.Count);
            foreach (string name in test.Cases)
            {
                writer.Write7BitEncodedInt(number(name));
            }
            writer.Write(test.Code);
        }
        WriteSet(writer, read.Methods, number);
        WriteSet(writer, read.Attributes, number);
    }

    private static void WriteSet(BinaryWriter writer, IReadOnlySet<string> set, Func<string, int> number)
    {
        writer.Write7BitEncodedInt(set.Count);
        foreach (string text in set)
        {
            writer.Write7BitEncodedInt(number(text));
        }
    }
}
