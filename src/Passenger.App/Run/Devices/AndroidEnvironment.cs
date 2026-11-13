using System.Text;

namespace Passenger.App.Run.Devices;

/// <summary>The environment file a Debug build's runtime host reads from its fast-deployment folder on the device: two
/// widths written as <c>0x%08x</c> and a NUL, then each variable's name and value, each NUL-padded to its width.</summary>
public static class AndroidEnvironment
{
    private const int Header = 22;

    /// <summary>The variables a file holds, in its order; none where it is too short to hold its widths, and throws a
    /// <see cref="FormatException"/> where its widths are no numbers.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < Header)
        {
            return [];
        }
        int names = Width(file[..10]);
        int values = Width(file[11..21]);
        if (names <= 0 || values <= 0)
        {
            throw new FormatException("The environment file's widths are not positive");
        }
        List<KeyValuePair<string, string>> read = [];
        for (int at = Header; at + names + values <= file.Length; at += names + values)
        {
            read.Add(new(Text(file.Slice(at, names)), Text(file.Slice(at + names, values))));
        }
        return read;
    }

    /// <summary>A file holding <paramref name="variables"/> in order, each name and value padded to the longest's width
    /// and one NUL more.</summary>
    public static byte[] Write(IReadOnlyList<KeyValuePair<string, string>> variables)
    {
        int names = variables.Count == 0 ? 1 : variables.Max(v => Encoding.UTF8.GetByteCount(v.Key)) + 1;
        int values = variables.Count == 0 ? 1 : variables.Max(v => Encoding.UTF8.GetByteCount(v.Value)) + 1;
        byte[] file = new byte[Header + variables.Count * (names + values)];
        Encoding.ASCII.GetBytes($"0x{names:x8}").CopyTo(file, 0);
        Encoding.ASCII.GetBytes($"0x{values:x8}").CopyTo(file, 11);
        int at = Header;
        foreach ((string name, string value) in variables)
        {
            Encoding.UTF8.GetBytes(name, file.AsSpan(at, names));
            Encoding.UTF8.GetBytes(value, file.AsSpan(at + names, values));
            at += names + values;
        }
        return file;
    }

    /// <summary>The build's own variables with <paramref name="added"/> set over them: one already named takes its new value
    /// in its place, the rest follow in their order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> With(IReadOnlyList<KeyValuePair<string, string>> own, IReadOnlyList<KeyValuePair<string, string>> added)
    {
        List<KeyValuePair<string, string>> merged = [.. own.Select(v => added.FirstOrDefault(a => a.Key == v.Key) is { Key: not null } over ? over : v)];
        merged.AddRange(added.Where(a => !own.Any(v => v.Key == a.Key)));
        return merged;
    }

    private static int Width(ReadOnlySpan<byte> written)
    {
        string text = Encoding.ASCII.GetString(written);
        return text.StartsWith("0x", StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int width)
            ? width : throw new FormatException($"The environment file's width {text} is no number");
    }

    private static string Text(ReadOnlySpan<byte> padded)
    {
        int end = padded.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? padded : padded[..end]);
    }
}
