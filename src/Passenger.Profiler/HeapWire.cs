using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Passenger.Profiler;

/// <summary>A memory snapshot as it comes out of a container: the heap graph whole as the container read it (its
/// objects, references, roots, shortest paths and retained sizes) and how each earlier snapshot's objects are found in
/// it. A frame is a kind (a snapshot, or why there is none), the wire's version, the body's length and the body; a
/// snapshot's body is deflated, each array a column of 7-bit numbers (addresses as the steps between them, in their
/// order), which deflate shrinks well.</summary>
internal static class HeapWire
{
    /// <summary>Raised when what is written changes; a reader refuses another version rather than misread it.</summary>
    public const byte Version = 1;

    public const byte Snapshot = (byte)'S';

    public const byte Failure = (byte)'F';

    /// <summary>No snapshot, since only the program's runtime could write it, into the program's output: why, as a line.</summary>
    public const byte IntoOutput = (byte)'O';

    private const int HeaderLength = 10;

    /// <summary>The largest body read, past which the stream is taken for something other than the wire: the tools'
    /// deflated graph of a heap of tens of millions of objects.</summary>
    private const long LargestBody = 1L << 30;

    /// <summary>The largest failure read: a line of why.</summary>
    private const long LargestFailure = 64 * 1024;

    /// <summary>The most a snapshot's body inflates to. With every count held to the bytes left, no frame makes Passenger
    /// allocate more than the largest snapshot it takes would.</summary>
    private const int LargestInflated = 1 << 30;

    /// <summary>How much of a body is taken at once as it comes, so a length that promises more than comes allocates no more.</summary>
    private const int Chunk = 1 << 20;

    public static void WriteFailure(Stream output, string why) => WriteFrame(output, Failure, Encoding.UTF8.GetBytes(why));

    public static void WriteIntoOutput(Stream output, string why) => WriteFrame(output, IntoOutput, Encoding.UTF8.GetBytes(why));

    public static void WriteSnapshot(Stream output, MemorySnapshot taken)
    {
        using MemoryStream body = new();
        using (DeflateStream deflated = new(body, CompressionLevel.Fastest, leaveOpen: true))
        using (BinaryWriter writer = new(deflated, Encoding.UTF8, leaveOpen: true))
        {
            Write(writer, taken);
        }
        WriteFrame(output, Snapshot, body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    private static void WriteFrame(Stream output, byte kind, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        header[0] = kind;
        header[1] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(header[2..], body.Length);
        output.Write(header);
        output.Write(body);
    }

    /// <summary>The next frame, or null once the stream has ended between frames. One of another version, or a stream that
    /// ends inside a frame, is an <see cref="InvalidDataException"/>.</summary>
    public static async Task<(byte Kind, byte[] Body)?> ReadFrameAsync(Stream input)
    {
        byte[] header = new byte[HeaderLength];
        int read = await input.ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < HeaderLength)
        {
            throw new InvalidDataException("The container tools' answer ended inside a frame");
        }
        if (header[1] != Version)
        {
            throw new InvalidDataException($"The container tools answer in version {header[1]} of the heap's wire, and the IDE reads version {Version}");
        }
        long length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(2));
        if (header[0] is not (Snapshot or Failure or IntoOutput) || length < 0)
        {
            throw new InvalidDataException("The container tools' answer is not a heap snapshot");
        }
        long largest = header[0] == Snapshot ? LargestBody : LargestFailure;
        if (length > largest)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The container tools' answer is {length} bytes long, more than the {largest} the IDE reads of one"));
        }
        byte[] body = new byte[Math.Min(length, Chunk)];
        int filled = 0;
        while (filled < length)
        {
            if (filled == body.Length)
            {
                Array.Resize(ref body, (int)Math.Min(length, (long)body.Length * 2));
            }
            int came = await input.ReadAsync(body.AsMemory(filled)).ConfigureAwait(false);
            if (came == 0)
            {
                throw new InvalidDataException("The container tools' answer ended inside a frame");
            }
            filled += came;
        }
        return (header[0], body);
    }

    /// <summary>A snapshot's body read back, of a run of this side's (<paramref name="run"/>, what snapshots are compared
    /// within), <paramref name="asked"/> the time from asking for it to its coming, the rest of which its sending took; its
    /// number, and those of the earlier ones it follows, after the run's <paramref name="before"/> (tools started again).
    /// What the container sent is never trusted: each count is held to the bytes left and each index to what it indexes, and
    /// a body that does not hold together is an <see cref="InvalidDataException"/>.</summary>
    public static MemorySnapshot ReadSnapshot(byte[] body, object run, TimeSpan asked, int before = 0)
    {
        using MemoryStream inflated = Inflate(body);
        using BinaryReader reader = new(inflated, Encoding.UTF8);
        int sentNumber = reader.ReadInt32();
        if (sentNumber < 1)
        {
            throw Unread();
        }
        int number = checked(before + sentNumber);
        DateTime taken = DateTime.FromBinary(reader.ReadInt64()).ToLocalTime();
        TimeSpan took = TimeSpan.FromTicks(reader.ReadInt64());
        string phases = reader.ReadString();
        HeapGraph graph = ReadGraph(reader, inflated);
        int earlierCount = Count(reader, inflated, 3);
        Dictionary<int, Identity> earlier = new(earlierCount);
        for (int i = 0; i < earlierCount; i++)
        {
            int older = reader.Read7BitEncodedInt();
            if (older < 1 || older >= sentNumber)
            {
                throw Unread();
            }
            string? inexact = reader.ReadBoolean() ? reader.ReadString() : null;
            earlier[before + older] = new Identity(Column(reader, inflated, 1, -1, graph.Count), inexact);
        }
        TimeSpan sent = asked > took ? asked - took : TimeSpan.Zero;
        return new MemorySnapshot(run, number, taken, graph, took + sent, $"{phases}, sent out of the container {sent.TotalSeconds:0.0} s", earlier);
    }

    /// <summary>A body inflated whole, refused past <see cref="LargestInflated"/>, so what is left of it is known.</summary>
    private static MemoryStream Inflate(byte[] body)
    {
        using MemoryStream packed = new(body, writable: false);
        using DeflateStream inflating = new(packed, CompressionMode.Decompress);
        MemoryStream inflated = new(Math.Min(body.Length, Chunk));
        byte[] buffer = new byte[81920];
        int read;
        while ((read = inflating.Read(buffer)) > 0)
        {
            if (inflated.Length + read > LargestInflated)
            {
                inflated.Dispose();
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"The container tools' heap snapshot inflates to more than the {LargestInflated} bytes the IDE reads of one"));
            }
            inflated.Write(buffer, 0, read);
        }
        inflated.Position = 0;
        return inflated;
    }

    private static InvalidDataException Unread() => new("The container tools' heap graph does not hold together");

    /// <summary>A count of things each written in at least <paramref name="least"/> bytes, refused when what is left of the
    /// body could not hold them.</summary>
    private static int Count(BinaryReader reader, MemoryStream inflated, int least)
    {
        int count = reader.Read7BitEncodedInt();
        return count >= 0 && count <= (inflated.Length - inflated.Position) / least ? count : throw Unread();
    }

    /// <summary>A column of numbers, each shifted back by what kept it from going below 0 and refused outside
    /// [<paramref name="lowest"/>, <paramref name="above"/>); of <paramref name="length"/> numbers, unless that is -1.</summary>
    private static int[] Column(BinaryReader reader, MemoryStream inflated, int shift, int lowest, int above, int length = -1)
    {
        int[] values = new int[Count(reader, inflated, 1)];
        if (length >= 0 && values.Length != length)
        {
            throw Unread();
        }
        for (int i = 0; i < values.Length; i++)
        {
            long value = (long)reader.Read7BitEncodedInt() - shift;
            values[i] = value >= lowest && value < above ? (int)value : throw Unread();
        }
        return values;
    }

    /// <summary>A column of wide numbers (sizes), <paramref name="length"/> of them.</summary>
    private static long[] Wide(BinaryReader reader, MemoryStream inflated, int length)
    {
        long[] values = new long[Count(reader, inflated, 1)];
        if (values.Length != length)
        {
            throw Unread();
        }
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = reader.Read7BitEncodedInt64();
        }
        return values;
    }

    private static void Write(BinaryWriter writer, MemorySnapshot taken)
    {
        writer.Write(taken.Number);
        writer.Write(taken.Taken.ToUniversalTime().ToBinary());
        writer.Write(taken.Took.Ticks);
        writer.Write(taken.Phases);
        HeapGraph graph = taken.Graph;
        writer.Write(graph.PointerSize);
        writer.Write(graph.Walked.Ticks);
        writer.Write(graph.Analysed.Ticks);
        writer.Write7BitEncodedInt(graph.Types.Length);
        foreach (HeapTypeInfo type in graph.Types)
        {
            writer.Write(type.Name);
            writer.Write(type.MethodTable);
            writer.Write7BitEncodedInt(type.ComponentSize);
            writer.Write(type.IsVector);
            writer.Write7BitEncodedInt(type.Fields.Count);
            foreach ((int offset, string name) in type.Fields)
            {
                WriteSigned(writer, offset);
                writer.Write(name);
            }
        }
        writer.Write7BitEncodedInt(graph.Roots.Length);
        foreach (GcRoot root in graph.Roots)
        {
            writer.Write((byte)root.Kind);
            writer.Write(root.Name);
        }
        int count = graph.Count;
        writer.Write7BitEncodedInt(count);
        ulong previous = 0;
        foreach (ulong address in graph.Address)
        {
            writer.Write7BitEncodedInt64((long)(address - previous));
            previous = address;
        }
        Column(graph.Type, 0);
        Wide(graph.Size);
        Column(graph.Root, 1);
        Column(graph.Parent, 1);
        foreach (int offset in graph.ParentOffset)
        {
            WriteSigned(writer, offset);
        }
        Column(graph.Depth, 0);
        Wide(graph.Retained);
        Column(graph.Enter, 0);
        Column(graph.Leave, 0);
        for (int i = 0; i < count; i++)
        {
            writer.Write7BitEncodedInt(graph.EdgeStart[i + 1] - graph.EdgeStart[i]);
        }
        Column(graph.EdgeTarget, 0);
        foreach (int offset in graph.EdgeOffset)
        {
            WriteSigned(writer, offset);
        }
        Wide(graph.TypeRetained);
        writer.Write7BitEncodedInt(taken.Earlier.Count);
        foreach ((int older, Identity identity) in taken.Earlier)
        {
            writer.Write7BitEncodedInt(older);
            writer.Write(identity.Inexact is not null);
            if (identity.Inexact is { } inexact)
            {
                writer.Write(inexact);
            }
            Column(identity.Later, 1);
        }

        // Each number shifted by what keeps it from going below 0 (-1 for none, where one is none).
        void Column(int[] values, int shift)
        {
            writer.Write7BitEncodedInt(values.Length);
            foreach (int value in values)
            {
                writer.Write7BitEncodedInt(value + shift);
            }
        }

        void Wide(long[] values)
        {
            writer.Write7BitEncodedInt(values.Length);
            foreach (long value in values)
            {
                writer.Write7BitEncodedInt64(value);
            }
        }
    }

    private static HeapGraph ReadGraph(BinaryReader reader, MemoryStream inflated)
    {
        int pointerSize = reader.ReadInt32();
        if (pointerSize is not (4 or 8))
        {
            throw Unread();
        }
        TimeSpan walked = TimeSpan.FromTicks(reader.ReadInt64());
        TimeSpan analysed = TimeSpan.FromTicks(reader.ReadInt64());
        // A type is at least its name's length, its method table, component size, whether it is a vector and its fields' count.
        HeapTypeInfo[] types = new HeapTypeInfo[Count(reader, inflated, 12)];
        for (int t = 0; t < types.Length; t++)
        {
            HeapTypeInfo type = new(reader.ReadString(), reader.ReadUInt64(), reader.Read7BitEncodedInt(), reader.ReadBoolean());
            int fields = Count(reader, inflated, 2);
            for (int f = 0; f < fields; f++)
            {
                int offset = ReadSigned(reader);
                type.Fields[offset] = reader.ReadString();
            }
            types[t] = type;
        }
        GcRoot[] roots = new GcRoot[Count(reader, inflated, 2)];
        for (int r = 0; r < roots.Length; r++)
        {
            RootKind kind = (RootKind)reader.ReadByte();
            roots[r] = Enum.IsDefined(kind) ? new GcRoot(kind, reader.ReadString()) : throw Unread();
        }
        // An object is at least a byte in each of its eleven columns.
        int count = Count(reader, inflated, 11);
        ulong[] address = new ulong[count];
        ulong previous = 0;
        for (int i = 0; i < count; i++)
        {
            // Each after the one before, as the lookup by address needs them.
            long step = reader.Read7BitEncodedInt64();
            if (step < 0 || (step == 0 && i > 0) || (ulong)step > ulong.MaxValue - previous)
            {
                throw Unread();
            }
            previous += (ulong)step;
            address[i] = previous;
        }
        int[] objectTypes = Column(reader, inflated, 0, 0, types.Length, count);
        long[] size = Wide(reader, inflated, count);
        int[] root = Column(reader, inflated, 1, 0, roots.Length, count);
        int[] parent = Column(reader, inflated, 1, -1, count, count);
        int[] parentOffset = new int[count];
        for (int i = 0; i < count; i++)
        {
            parentOffset[i] = ReadSigned(reader);
        }
        int[] depth = Column(reader, inflated, 0, 0, int.MaxValue, count);
        long[] retained = Wide(reader, inflated, count);
        int[] enter = Column(reader, inflated, 0, int.MinValue, int.MaxValue, count);
        int[] leave = Column(reader, inflated, 0, int.MinValue, int.MaxValue, count);
        int[] edgeStart = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            int references = reader.Read7BitEncodedInt();
            if (references < 0 || references > int.MaxValue - edgeStart[i])
            {
                throw Unread();
            }
            edgeStart[i + 1] = edgeStart[i] + references;
        }
        int[] edgeTarget = Column(reader, inflated, 0, 0, count, edgeStart[count]);
        int[] edgeOffset = new int[edgeTarget.Length];
        for (int e = 0; e < edgeOffset.Length; e++)
        {
            edgeOffset[e] = ReadSigned(reader);
        }
        long[] typeRetained = Wide(reader, inflated, types.Length);
        return new HeapGraph
        {
            Address = address,
            Type = objectTypes,
            Size = size,
            EdgeStart = edgeStart,
            EdgeTarget = edgeTarget,
            EdgeOffset = edgeOffset,
            Types = types,
            Roots = roots,
            Root = root,
            Parent = parent,
            ParentOffset = parentOffset,
            Depth = depth,
            Retained = retained,
            Enter = enter,
            Leave = leave,
            TypeRetained = typeRetained,
            PointerSize = pointerSize,
            Walked = walked,
            Analysed = analysed,
        };
    }

    /// <summary>A number that may be below 0 (a dependent handle's offset), zigzagged so a small one stays small.</summary>
    private static void WriteSigned(BinaryWriter writer, int value) => writer.Write7BitEncodedInt((value << 1) ^ (value >> 31));

    private static int ReadSigned(BinaryReader reader)
    {
        int zigzag = reader.Read7BitEncodedInt();
        return (int)((uint)zigzag >> 1) ^ -(zigzag & 1);
    }
}
