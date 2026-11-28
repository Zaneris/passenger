using System.Runtime.CompilerServices;

namespace Passenger.Debugger;

/// <summary>A type's signature as a module writes it, each type parameter it names (a class's !n, a method's !!n) replaced by
/// the signature given for that parameter's argument, so a reader with no instantiation of its own (the runtime's read of a
/// value at a frame's address) types it as the frame's instantiation does.</summary>
internal static class InstantiatedSignature
{
    private const byte Var = 0x13;
    private const byte MethodVar = 0x1E;

    /// <summary>The signature with each type parameter written as <paramref name="argument"/> gives it, asked whether it is a
    /// method's and its number: the signature itself where it names none, never asking.</summary>
    public static byte[] Of(byte[] signature, Func<bool, int, byte[]> argument)
    {
        List<(int Start, int End, bool Method, int Number)> named = [];
        if (Skip(signature, 0, named) != signature.Length)
        {
            throw new BadImageFormatException("A local's signature goes on past its type");
        }
        if (named.Count == 0)
        {
            return signature;
        }
        List<byte> written = new(signature.Length + (named.Count * 4));
        int from = 0;
        foreach ((int start, int end, bool method, int number) in named)
        {
            written.AddRange(signature.AsSpan(from, start - from));
            written.AddRange(argument(method, number));
            from = end;
        }
        written.AddRange(signature.AsSpan(from));
        return [.. written];
    }

    /// <summary>Where the type at <paramref name="at"/> ends, each type parameter it names added to <paramref name="named"/>.</summary>
    private static int Skip(byte[] signature, int at, List<(int Start, int End, bool Method, int Number)> named)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new BadImageFormatException("A local's signature nests its types too deeply to read");
        }
        int start = at;
        byte code = ByteAt(signature, at++);
        switch (code)
        {
            case >= 0x01 and <= 0x0E or 0x16 or 0x18 or 0x19 or 0x1C:
                return at;
            case 0x0F or 0x10 or 0x1D or 0x41 or 0x45:
                return Skip(signature, at, named);
            case 0x11 or 0x12:
                return Past(signature, at);
            case Var or MethodVar:
            {
                int end = Past(signature, at);
                named.Add((start, end, code == MethodVar, Number(signature, at)));
                return end;
            }
            case 0x14:
            {
                at = Skip(signature, at, named);
                at = Past(signature, at);
                for (int lists = 0; lists < 2; lists++)
                {
                    int count = Number(signature, at);
                    at = Past(signature, at);
                    for (int i = 0; i < count; i++)
                    {
                        at = Past(signature, at);
                    }
                }
                return at;
            }
            case 0x15:
            {
                at = Past(signature, at + 1);
                int count = Number(signature, at);
                at = Past(signature, at);
                for (int i = 0; i < count; i++)
                {
                    at = Skip(signature, at, named);
                }
                return at;
            }
            case 0x1B:
            {
                // A method's signature: its calling convention, its generic arity where it is generic, its count, its return.
                bool generic = (ByteAt(signature, at++) & 0x10) != 0;
                at = generic ? Past(signature, at) : at;
                int count = Number(signature, at);
                at = Skip(signature, Past(signature, at), named);
                for (int i = 0; i < count; i++)
                {
                    at = Skip(signature, at, named);
                }
                return at;
            }
            case 0x1F or 0x20:
                return Skip(signature, Past(signature, at), named);
            default:
                throw new BadImageFormatException($"A local's signature has a type of kind 0x{code:x2}, which no signature writes");
        }
    }

    private static byte ByteAt(byte[] signature, int at) =>
        at < signature.Length ? signature[at] : throw new BadImageFormatException("A local's signature ends inside its type");

    /// <summary>Where the compressed number at a place ends.</summary>
    private static int Past(byte[] signature, int at) => (ByteAt(signature, at) & 0x80) == 0 ? at + 1 : (signature[at] & 0x40) == 0 ? at + 2 : at + 4;

    /// <summary>The compressed number at a place.</summary>
    private static int Number(byte[] signature, int at)
    {
        byte first = ByteAt(signature, at);
        return (first & 0x80) == 0 ? first
            : (first & 0x40) == 0 ? ((first & 0x3F) << 8) | ByteAt(signature, at + 1)
            : ((first & 0x1F) << 24) | (ByteAt(signature, at + 1) << 16) | (ByteAt(signature, at + 2) << 8) | ByteAt(signature, at + 3);
    }
}
