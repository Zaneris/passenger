using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Passenger.Core;

/// <summary>The languages the system asks programs to speak, most preferred first, read in this one place: macOS's preferred
/// languages, Windows' preferred display languages, elsewhere the locale variables (GNU's LANGUAGE list, then LC_ALL,
/// LC_MESSAGES and LANG). <see cref="Variable"/> stands in for the system, as a test run's does.</summary>
public static partial class SystemLanguages
{
    /// <summary>Names the system's languages in its place: tags apart by commas, most preferred first ("fr-CA,en-US").</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_SYSTEM_LANGUAGES";

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>MUI_LANGUAGE_NAME: languages by their tags.</summary>
    private const uint ByName = 0x8;

    /// <summary>The tags <see cref="Variable"/> names, or null where it names none; a malformed value is a
    /// <see cref="FormatException"/>, fatal as Passenger starts.</summary>
    public static IReadOnlyList<string>? Named
    {
        get
        {
            if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } value)
            {
                return null;
            }
            string[] tags = value.Split(',', StringSplitOptions.TrimEntries);
            return tags.All(t => t.Length > 0 && t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                ? tags
                : throw new FormatException($"{Variable} names \"{value}\", where it takes language tags apart by commas (fr-CA,en-US)");
        }
    }

    /// <summary>The system's languages as tags, most preferred first; none where it names none.</summary>
    public static IReadOnlyList<string> Read()
    {
        if (Named is { } named)
        {
            return named;
        }
        try
        {
            return OperatingSystem.IsMacOS() ? Mac() : OperatingSystem.IsWindows() ? Windows() : Locales(Environment.GetEnvironmentVariable);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The languages the locale variables name: LANGUAGE's list (GNU gettext's, read where a locale is set), then
    /// LC_ALL, LC_MESSAGES and LANG, each "fr_CA.UTF-8@euro" read as fr-CA; the C and POSIX locales are English.</summary>
    public static IReadOnlyList<string> Locales(Func<string, string?> variable)
    {
        string? locale = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }.Select(variable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        bool plain = locale is null || locale.Split('.', '@')[0] is "C" or "POSIX";
        IEnumerable<string> listed = plain || variable("LANGUAGE") is not { Length: > 0 } list ? [] : list.Split(':');
        return [.. listed.Append(locale ?? string.Empty).Select(Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string Tag(string locale)
    {
        string name = locale.Split('.', '@')[0];
        return name is "C" or "POSIX" ? "en" : name.Replace('_', '-');
    }

    [SupportedOSPlatform("macos")]
    private static List<string> Mac()
    {
        List<string> tags = [];
        IntPtr languages = CFLocaleCopyPreferredLanguages();
        if (languages == IntPtr.Zero)
        {
            return tags;
        }
        try
        {
            for (nint i = 0, count = CFArrayGetCount(languages); i < count; i++)
            {
                IntPtr text = CFArrayGetValueAtIndex(languages, i);
                tags.Add(string.Create((int)CFStringGetLength(text), text, (chars, from) =>
                {
                    unsafe
                    {
                        fixed (char* into = chars)
                        {
                            CFStringGetCharacters(from, new CFRange(0, chars.Length), into);
                        }
                    }
                }));
            }
        }
        finally
        {
            CFRelease(languages);
        }
        return tags;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe List<string> Windows()
    {
        uint size = 0;
        if (!GetUserPreferredUILanguages(ByName, out _, null, ref size) || size == 0)
        {
            return [];
        }
        char[] buffer = new char[size];
        fixed (char* into = buffer)
        {
            return GetUserPreferredUILanguages(ByName, out _, into, ref size)
                ? [.. new string(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries)]
                : [];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRange(nint Location, nint Length);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFLocaleCopyPreferredLanguages();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(IntPtr text);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(IntPtr text, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserPreferredUILanguages(uint flags, out uint count, char* buffer, ref uint size);
}
