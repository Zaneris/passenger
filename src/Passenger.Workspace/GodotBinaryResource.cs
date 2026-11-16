using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Passenger.Workspace;

/// <summary>A resource or scene Godot writes as bytes (a .res, a .scn), read as far as its uid and the resources it names, as
/// Godot's binary format keeps them: <c>RSRC</c> then the resource's stream, or <c>RSCC</c> then that stream cut into blocks
/// each compressed alone (by Zstandard, as Godot writes them). The stream holds the engine's and the format's versions, the
/// type, the uid, a table of strings, the external resources (each a type, a path and a uid) and the internal ones (each a
/// path and the place in the stream where it starts). Written again with another uid or other external resources as Godot
/// rewrites one in place, everything else as it was. Pure.</summary>
public sealed class GodotBinaryResource
{
    // The format versions Godot can rewrite in place (from 1) up to the newest Passenger reads (Godot 4.3's), and the newest engine.
    private const uint OldestFormat = 1;
    private const uint NewestFormat = 6;
    private const uint NewestEngine = 4;
    private const uint UsesUids = 2;
    private const uint HasScriptClass = 8;
    private const int ReservedFields = 11;
    private const uint Zstandard = 2;
    private const int GodotLevel = 3;

    private static ReadOnlySpan<byte> Plain => "RSRC"u8;
    private static ReadOnlySpan<byte> Compressed => "RSCC"u8;

    private readonly byte[] file;
    // The resource's stream: the file itself where it is plain (its offsets count its magic), else its blocks decompressed.
    private readonly byte[] stream;
    private readonly int blockSize;
    private readonly bool big;
    private readonly int importAt;
    private readonly int uidAt;
    private readonly int externalsAt;
    private readonly IReadOnlyList<(int Start, int PathAt, int UidAt, int End)> externalPlaces;
    private readonly int internalsAt;
    private readonly IReadOnlyList<int> offsetsAt;
    private readonly int internalsEnd;

    private GodotBinaryResource(byte[] file, byte[] stream, int blockSize, Reader reader, int importAt, int uidAt, string? uid, int externalsAt,
        IReadOnlyList<(int, int, int, int)> externalPlaces, IReadOnlyList<GodotBinaryExternal> externals, int internalsAt, IReadOnlyList<int> offsetsAt)
    {
        this.file = file;
        this.stream = stream;
        this.blockSize = blockSize;
        big = reader.Big;
        this.importAt = importAt;
        this.uidAt = uidAt;
        Uid = uid;
        this.externalsAt = externalsAt;
        this.externalPlaces = externalPlaces;
        Externals = externals;
        this.internalsAt = internalsAt;
        this.offsetsAt = offsetsAt;
        internalsEnd = reader.At;
    }

    /// <summary>Its uid (uid://…), null where it keeps none.</summary>
    public string? Uid { get; }

    /// <summary>The resources it names outside itself, in its order.</summary>
    public IReadOnlyList<GodotBinaryExternal> Externals { get; }

    /// <summary>Whether a file's first bytes are those of Godot's binary format, plain or compressed.</summary>
    public static bool Starts(ReadOnlySpan<byte> start) => start.StartsWith(Plain) || start.StartsWith(Compressed);

    /// <summary>A file of Godot's binary format read; null where it is none, or where it is one Passenger cannot rewrite as
    /// Godot does, <paramref name="unread"/> then saying why (a newer format, another compression, a file cut short).</summary>
    public static GodotBinaryResource? Read(byte[] file, out string? unread)
    {
        unread = null;
        int blockSize = 0;
        byte[] stream = file;
        int start = Plain.Length;
        if (file.AsSpan().StartsWith(Compressed))
        {
            if (Decompressed(file, out blockSize, out unread) is not { } inflated)
            {
                return null;
            }
            stream = inflated;
            start = 0;
        }
        else if (!file.AsSpan().StartsWith(Plain))
        {
            return null;
        }
        try
        {
            // Whether it is big-endian, and whether its reals are doubles, are written little-endian before the rest.
            Reader reader = new(stream, start, big: false);
            bool big = reader.UInt32() != 0;
            reader.UInt32();
            reader = new Reader(stream, reader.At, big);
            uint major = reader.UInt32();
            uint minor = reader.UInt32();
            uint format = reader.UInt32();
            if (format is < OldestFormat or > NewestFormat || major > NewestEngine)
            {
                unread = $"Godot {major}.{minor} wrote it in the binary format {format}, which Passenger doesn't rewrite";
                return null;
            }
            reader.Skip();
            int importAt = reader.At;
            reader.UInt64();
            uint flags = reader.UInt32();
            int uidAt = reader.At;
            string? uid = (flags & UsesUids) != 0 ? GodotUids.Text((long)reader.UInt64()) : Skipped(reader);
            if ((flags & HasScriptClass) != 0)
            {
                reader.Skip();
            }
            for (int i = 0; i < ReservedFields; i++)
            {
                reader.UInt32();
            }
            for (uint i = 0, strings = reader.UInt32(); i < strings; i++)
            {
                reader.Skip();
            }
            int externalsAt = reader.At;
            List<(int, int, int, int)> places = [];
            List<GodotBinaryExternal> externals = [];
            for (uint i = 0, count = reader.UInt32(); i < count; i++)
            {
                int at = reader.At;
                string type = reader.String();
                int pathAt = reader.At;
                string path = reader.String();
                int named = (flags & UsesUids) != 0 ? reader.At : -1;
                string? by = named >= 0 ? GodotUids.Text((long)reader.UInt64()) : null;
                places.Add((at, pathAt, named, reader.At));
                externals.Add(new GodotBinaryExternal(type, path, by));
            }
            int internalsAt = reader.At;
            List<int> offsets = [];
            for (uint i = 0, count = reader.UInt32(); i < count; i++)
            {
                reader.Skip();
                offsets.Add(reader.At);
                reader.UInt64();
            }
            return new GodotBinaryResource(file, stream, blockSize, reader, importAt, (flags & UsesUids) != 0 ? uidAt : -1, uid, externalsAt, places,
                externals, internalsAt, offsets);
        }
        catch (EndOfStreamException)
        {
            unread = "it ends before Godot's binary format says it does";
            return null;
        }

        static string? Skipped(Reader reader)
        {
            reader.UInt64();
            return null;
        }
    }

    /// <summary>The file's bytes with the uid given (none: as it is; a file keeping no uid takes none) and its external
    /// resources each naming the path and uid given (a null uid: as written), as Godot's own rewrite in place leaves them:
    /// the internal resources' places moved by what the paths' lengths changed, a compressed file compressed again where
    /// a block changed. The bytes as they were where nothing changed.</summary>
    public byte[] With(string? uid, IReadOnlyList<GodotBinaryExternal> externals)
    {
        if (externals.Count != Externals.Count)
        {
            throw new ArgumentException($"{externals.Count} external resources given for the {Externals.Count} written", nameof(externals));
        }
        MemoryStream written = new(stream.Length + 64);
        written.Write(stream, 0, externalsAt + sizeof(uint));
        for (int i = 0; i < externals.Count; i++)
        {
            (int start, int pathAt, int uidAt, int end) = externalPlaces[i];
            written.Write(stream, start, pathAt - start);
            int pathEnd = uidAt >= 0 ? uidAt : end;
            if (externals[i].Path == Externals[i].Path)
            {
                written.Write(stream, pathAt, pathEnd - pathAt);
            }
            else
            {
                byte[] path = Encoding.UTF8.GetBytes(externals[i].Path + '\0');
                Write32(written, (uint)path.Length);
                written.Write(path);
            }
            if (uidAt >= 0)
            {
                if (externals[i].Uid is { } named && named != Externals[i].Uid && GodotUids.Id(named) is { } id)
                {
                    Write64(written, (ulong)id);
                }
                else
                {
                    written.Write(stream, uidAt, end - uidAt);
                }
            }
        }
        long moved = written.Length - internalsAt;
        written.Write(stream, internalsAt, internalsEnd - internalsAt);
        written.Write(stream, internalsEnd, stream.Length - internalsEnd);
        byte[] bytes = written.ToArray();
        foreach (int at in offsetsAt)
        {
            Put64(bytes, (int)(at + moved), Get64(stream, at) + (ulong)moved);
        }
        // An offset of 0 names no import metadata, which stays none.
        if (Get64(stream, importAt) is > 0 and var import)
        {
            Put64(bytes, importAt, import + (ulong)moved);
        }
        if (uidAt >= 0 && uid is not null && uid != Uid && GodotUids.Id(uid) is { } made)
        {
            Put64(bytes, uidAt, (ulong)made);
        }
        return bytes.AsSpan().SequenceEqual(stream) ? file : blockSize > 0 ? Compress(bytes) : bytes;
    }

    /// <summary>A compressed file's stream: its blocks, each a Zstandard frame of the block size (the last of what is left),
    /// decompressed in turn. Null where it is compressed otherwise or its blocks are not whole, <paramref name="unread"/>
    /// then saying why.</summary>
    private static byte[]? Decompressed(byte[] file, out int blockSize, out string? unread)
    {
        blockSize = 0;
        unread = "its compressed blocks are not whole";
        ReadOnlySpan<byte> bytes = file;
        if (bytes.Length < 16)
        {
            return null;
        }
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (mode != Zstandard)
        {
            unread = "it is compressed by another method than Godot's Zstandard, which Passenger doesn't read";
            return null;
        }
        long count = size == 0 ? 0 : total / size + 1;
        if (size is 0 or > int.MaxValue || total > Array.MaxLength || 16 + count * 4 > bytes.Length)
        {
            return null;
        }
        byte[] stream = new byte[total];
        long at = 16 + count * 4;
        for (int i = 0; i < count; i++)
        {
            uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(16 + i * 4)..]);
            int expected = (int)(i == count - 1 ? total % size : size);
            if (compressed > bytes.Length - at || !ZstandardDecoder.TryDecompress(bytes.Slice((int)at, (int)compressed), stream.AsSpan((int)(i * size), expected), out int made)
                || made != expected)
            {
                return null;
            }
            at += compressed;
        }
        blockSize = (int)size;
        unread = null;
        return stream;
    }

    /// <summary>A stream compressed as Godot compresses a resource's: its magic, Zstandard, the block size, the stream's
    /// length and each block's compressed length, then the blocks and the magic again. A block the same as the file's keeps
    /// its compressed bytes.</summary>
    private byte[] Compress(byte[] bytes)
    {
        int count = bytes.Length / blockSize + 1;
        int oldCount = stream.Length / blockSize + 1;
        List<ReadOnlyMemory<byte>> blocks = [];
        long oldAt = 16 + oldCount * 4L;
        List<(long At, int Length)> old = [];
        for (int i = 0; i < oldCount; i++)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16 + i * 4));
            old.Add((oldAt, length));
            oldAt += length;
        }
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> block = bytes.AsSpan(i * blockSize, i == count - 1 ? bytes.Length % blockSize : blockSize);
            ReadOnlySpan<byte> was = i < oldCount ? stream.AsSpan(i * blockSize, i == oldCount - 1 ? stream.Length % blockSize : blockSize) : [];
            if (i < oldCount && block.SequenceEqual(was))
            {
                blocks.Add(file.AsMemory((int)old[i].At, old[i].Length));
                continue;
            }
            byte[] compressed = new byte[ZstandardEncoder.GetMaxCompressedLength(block.Length)];
            if (!ZstandardEncoder.TryCompress(block, compressed, out int made, GodotLevel, ZstandardCompressionOptions.DefaultWindowLog2))
            {
                throw new InvalidOperationException("Zstandard found no room for a block in its own bound");
            }
            blocks.Add(compressed.AsMemory(0, made));
        }
        MemoryStream written = new();
        written.Write(Compressed);
        foreach (uint value in (ReadOnlySpan<uint>)[Zstandard, (uint)blockSize, (uint)bytes.Length])
        {
            Write32(written, value, big: false);
        }
        foreach (ReadOnlyMemory<byte> block in blocks)
        {
            Write32(written, (uint)block.Length, big: false);
        }
        foreach (ReadOnlyMemory<byte> block in blocks)
        {
            written.Write(block.Span);
        }
        written.Write(Compressed);
        return written.ToArray();
    }

    private void Write32(Stream to, uint value) => Write32(to, value, big);

    private static void Write32(Stream to, uint value, bool big)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }
        to.Write(bytes);
    }

    private void Write64(Stream to, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Put64(bytes, value);
        to.Write(bytes);
    }

    private ulong Get64(byte[] bytes, int at) => big ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));

    private void Put64(byte[] bytes, int at, ulong value) => Put64(bytes.AsSpan(at), value);

    private void Put64(Span<byte> bytes, ulong value)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        }
    }

    /// <summary>The stream read from a place on, in the file's byte order; past its end, <see cref="EndOfStreamException"/>.</summary>
    private sealed class Reader(byte[] bytes, int at, bool big)
    {
        public bool Big => big;

        public int At => at;

        public uint UInt32() => big ? BinaryPrimitives.ReadUInt32BigEndian(Take(sizeof(uint))) : BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

        public ulong UInt64() => big ? BinaryPrimitives.ReadUInt64BigEndian(Take(sizeof(ulong))) : BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));

        /// <summary>A string as Godot writes one: its length, then its UTF-8 bytes ending with a NUL.</summary>
        public string String()
        {
            ReadOnlySpan<byte> text = Take(UInt32());
            int end = text.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end >= 0 ? text[..end] : text);
        }

        public void Skip() => Take(UInt32());

        private ReadOnlySpan<byte> Take(uint length)
        {
            if (length > bytes.Length - at)
            {
                throw new EndOfStreamException();
            }
            ReadOnlySpan<byte> taken = bytes.AsSpan(at, (int)length);
            at += (int)length;
            return taken;
        }

        private ReadOnlySpan<byte> Take(int length) => Take((uint)length);
    }
}

/// <summary>A resource a binary resource names outside itself: its type, its path (res://…, or relative to the file) and its
/// uid (uid://…), null where it names none.</summary>
public sealed record GodotBinaryExternal(string Type, string Path, string? Uid);
