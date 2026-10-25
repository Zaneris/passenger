using System.Text;

namespace Passenger.Text;

/// <summary>An encoding a file is read or written in, by the name the status bar's encoding widget gives it: those it
/// lists first, then under More the common ones of other scripts that .NET has here, its code pages (the shared
/// framework's System.Text.Encoding.CodePages) included. UTF-16 is always written with its BOM, the only way it is read
/// back as UTF-16.</summary>
public sealed record FileEncoding(string Name, int CodePage, bool Bom = false)
{
    private static IReadOnlyList<FileEncoding>? more;

    static FileEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static FileEncoding Utf8 { get; } = new("UTF-8", 65001);
    public static FileEncoding Utf8Bom { get; } = new("UTF-8 BOM", 65001, Bom: true);
    public static FileEncoding Utf16 { get; } = new("UTF-16 LE", 1200, Bom: true);
    public static FileEncoding Utf16BigEndian { get; } = new("UTF-16 BE", 1201, Bom: true);
    public static FileEncoding Latin1 { get; } = new("ISO-8859-1", 28591);
    public static FileEncoding Windows1252 { get; } = new("windows-1252", 1252);

    /// <summary>What the widget lists first: the Unicode encodings and the common Western ones.</summary>
    public static IReadOnlyList<FileEncoding> First { get; } =
        [Utf8, Utf8Bom, Utf16, Utf16BigEndian, Latin1, Windows1252, new("US-ASCII", 20127)];

    private static FileEncoding? eightBit;

    /// <summary>What a file whose bytes are not UTF-8 is read in when nothing names another: the system's ANSI code page
    /// on Windows (a single-byte one), else windows-1252, as Roslyn reads such a source file. Never Latin-1, which reads
    /// 1252's quotes, dashes and € (0x80–0x9F) as control characters, written back as UTF-8 controls once the file can
    /// no longer be Latin-1.</summary>
    public static FileEncoding EightBit => eightBit ??= SystemEightBit() ?? Windows1252;

    private static FileEncoding? SystemEightBit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            // Code page 0 is the system's ANSI code page, which the code pages' provider knows on Windows alone.
            Encoding system = Encoding.GetEncoding(0);
            return system.IsSingleByte && system.CodePage is not (65001 or 20127) ? Of(system, false) : null;
        }
        catch (Exception missing) when (missing is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What the widget's More lists, by name: those of other scripts that this system's .NET has.</summary>
    public static IReadOnlyList<FileEncoding> More => more ??= [.. Others.Where(e => e.Available).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    private static readonly FileEncoding[] Others =
    [
        new("windows-1250", 1250), new("windows-1251", 1251), new("windows-1253", 1253), new("windows-1254", 1254),
        new("windows-1255", 1255), new("windows-1256", 1256), new("windows-1257", 1257), new("windows-1258", 1258),
        new("ISO-8859-2", 28592), new("ISO-8859-5", 28595), new("ISO-8859-7", 28597), new("ISO-8859-9", 28599),
        new("ISO-8859-15", 28605), new("KOI8-R", 20866), new("KOI8-U", 21866), new("IBM437", 437), new("IBM850", 850),
        new("IBM866", 866), new("x-MacRoman", 10000), new("Shift_JIS", 932), new("EUC-JP", 51932), new("GBK", 936),
        new("GB18030", 54936), new("Big5", 950), new("EUC-KR", 51949),
    ];

    /// <summary>The encoding itself, which writes no BOM of its own: the BOM is written apart, when <see cref="Bom"/>.</summary>
    public Encoding Encoding => CodePage switch
    {
        65001 => new UTF8Encoding(Bom),
        1200 => Encoding.Unicode,
        1201 => Encoding.BigEndianUnicode,
        28591 => Encoding.Latin1,
        20127 => Encoding.ASCII,
        _ => Encoding.GetEncoding(CodePage),
    };

    /// <summary>Whether it is a Unicode encoding, which holds every character.</summary>
    public bool IsUnicode => CodePage is 65001 or 1200 or 1201;

    private bool Available
    {
        get
        {
            try
            {
                return Encoding.CodePage == CodePage;
            }
            catch (Exception missing) when (missing is NotSupportedException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The listed encoding a file stored so is in, or one named by .NET's name for it.</summary>
    public static FileEncoding Of(Encoding encoding, bool bom)
    {
        bool Stores(FileEncoding e) => e.CodePage == encoding.CodePage && (e.CodePage != 65001 || e.Bom == bom);
        // More is read only for a file in none of the first, which most are.
        return First.FirstOrDefault(Stores) ?? More.FirstOrDefault(Stores) ?? new FileEncoding(encoding.WebName, encoding.CodePage, bom);
    }
}
