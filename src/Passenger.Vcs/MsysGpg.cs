using System.Diagnostics;

namespace Passenger.Vcs;

/// <summary>A GnuPG program of Git for Windows' own (its usr\bin, the MSYS runtime beside it), which reads the folder its
/// GNUPGHOME names only in MSYS's form: GnuPG built for MSYS has no drive letters (make_absfilename without
/// HAVE_DRIVE_LETTERS), so a path is absolute only where it begins with '/', its drives under / (/c/Users/…), and a
/// Windows one (C:\…, C:/…) is joined to its working folder, its keyring and agent then not found; MSYS converts HOME and
/// PATH as such a program starts, never GNUPGHOME. What runs one gives it GNUPGHOME in that form.</summary>
public static class MsysGpg
{
    /// <summary>The variable naming GnuPG's home folder.</summary>
    private const string Home = "GNUPGHOME";

    /// <summary>Whether a program runs on the MSYS runtime: its msys-2.0.dll beside it.</summary>
    public static bool Is(string program, Func<string, bool> exists) =>
        Path.GetDirectoryName(program) is { Length: > 0 } folder && exists(Path.Combine(folder, "msys-2.0.dll"));

    /// <summary>A Windows path in MSYS's form: <c>C:\Users\x</c> as <c>/c/Users/x</c>, <c>\\server\share\x</c> as
    /// <c>//server/share/x</c>; any other as it is.</summary>
    public static string Posix(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return $"/{char.ToLowerInvariant(path[0])}{path[2..].Replace('\\', '/')}".TrimEnd('/') is { Length: > 0 } posix ? posix : "/";
        }
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? path.Replace('\\', '/') : path;
    }

    /// <summary>A start of <paramref name="program"/> given GNUPGHOME in MSYS's form where the program is an MSYS one and the
    /// environment names the home in Windows' form; untouched otherwise, and anywhere but Windows.</summary>
    public static void Fit(ProcessStartInfo start, string program)
    {
        if (OperatingSystem.IsWindows() && start.Environment.TryGetValue(Home, out string? home) && home is { Length: > 0 } && !home.StartsWith('/')
            && Is(program, File.Exists))
        {
            start.Environment[Home] = Posix(home);
        }
    }

    /// <summary>Whether the gpg git runs where its config names none is an MSYS one (Git for Windows' own, no gpg on the
    /// PATH before it), read once.</summary>
    private static readonly Lazy<bool> GitsGpg = new(() => GpgPrograms.Gpg(null) is { } gpg && Is(gpg, File.Exists));

    /// <summary>A start of git given GNUPGHOME in MSYS's form where the gpg it runs to sign and verify is an MSYS one and
    /// the environment names the home in Windows' form; untouched otherwise, and anywhere but Windows.</summary>
    public static void FitGit(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows() && start.Environment.TryGetValue(Home, out string? home) && home is { Length: > 0 } && !home.StartsWith('/') && GitsGpg.Value)
        {
            start.Environment[Home] = Posix(home);
        }
    }
}
