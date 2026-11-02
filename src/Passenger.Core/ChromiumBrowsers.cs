namespace Passenger.Core;

/// <summary>A Chromium browser found on the machine: its program, what it is called, and the arguments a setting gives each
/// start of it.</summary>
public sealed record ChromiumFound(string Program, string Name)
{
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>The Chromium browsers (Chrome, Edge, Chromium, Brave) a Blazor app's WebAssembly half is debugged in, found as
/// their installers put them: the one a setting names, else on macOS each app bundle in /Applications and ~/Applications,
/// on Windows each under Program Files and the user's local app data, then the first on the PATH as the login shell gives
/// it (<see cref="ShellEnvironment"/>), Chrome before Edge before the rest. Firefox is no Chromium, and its debugging
/// protocol is another.</summary>
public static class ChromiumBrowsers
{
    /// <summary>Where a person gets one, in words that end a sentence saying none was found.</summary>
    public const string HowToGetOne = "install Google Chrome (google.com/chrome) or Microsoft Edge (microsoft.com/edge)";

    /// <summary>The browsers by their app bundle on macOS, their folder under a Windows root, and their names on a PATH.</summary>
    private static readonly (string Name, string Bundle, string Windows, string[] OnPath)[] Known =
    [
        ("Google Chrome", "Google Chrome.app/Contents/MacOS/Google Chrome", @"Google\Chrome\Application\chrome.exe", ["google-chrome", "google-chrome-stable", "chrome"]),
        ("Microsoft Edge", "Microsoft Edge.app/Contents/MacOS/Microsoft Edge", @"Microsoft\Edge\Application\msedge.exe", ["microsoft-edge", "microsoft-edge-stable", "msedge"]),
        ("Chromium", "Chromium.app/Contents/MacOS/Chromium", @"Chromium\Application\chrome.exe", ["chromium", "chromium-browser"]),
        ("Brave", "Brave Browser.app/Contents/MacOS/Brave Browser", @"BraveSoftware\Brave-Browser\Application\brave.exe", ["brave-browser", "brave"]),
    ];

    /// <summary>The browser to debug in on this machine: <paramref name="chosen"/>, the program a setting names and the
    /// arguments it gives it, where it names one; else the first found. Null where none is.</summary>
    public static ChromiumFound? Find((string Program, IReadOnlyList<string> Arguments)? chosen)
    {
        ShellEnvironment.Ready();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IReadOnlyList<string> roots = OperatingSystem.IsWindows()
            ? [.. new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
                .Select(Environment.GetFolderPath).Where(f => f.Length > 0).Distinct(PathNames.Comparer)]
            : OperatingSystem.IsMacOS() ? ["/Applications", Path.Combine(home, "Applications")] : [];
        return Find(chosen, (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
            roots, OperatingSystem.IsWindows(), File.Exists);
    }

    /// <summary>The browser among these places: <paramref name="chosen"/> where it names one that is there (null where it is
    /// not); else each known browser in <paramref name="roots"/> (app bundle folders, or on <paramref name="windows"/> its
    /// program folders), then on the PATH's folders.</summary>
    public static ChromiumFound? Find((string Program, IReadOnlyList<string> Arguments)? chosen, IReadOnlyList<string> pathFolders, IReadOnlyList<string> roots, bool windows,
        Func<string, bool> exists)
    {
        if (chosen is ({ } named, { } arguments))
        {
            return exists(named) ? new ChromiumFound(named, Path.GetFileNameWithoutExtension(named)) { Arguments = arguments } : null;
        }
        foreach ((string name, string bundle, string inWindows, _) in Known)
        {
            foreach (string root in roots)
            {
                string program = Path.Combine(root, windows ? inWindows : bundle);
                if (exists(program))
                {
                    return new ChromiumFound(program, name);
                }
            }
        }
        foreach ((string name, _, _, string[] onPath) in Known)
        {
            foreach (string folder in pathFolders)
            {
                foreach (string word in onPath)
                {
                    string program = Path.Combine(folder, windows ? word + ".exe" : word);
                    if (exists(program))
                    {
                        return new ChromiumFound(program, name);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Chromium's page on a browser the system refuses its sandbox, and the AppArmor profile that lets it run.</summary>
    public const string SandboxPage = "https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md";

    /// <summary>Whether a line a Chromium browser wrote is its refusal to run without the sandbox the system refused it.</summary>
    public static bool RefusesSandbox(string line) => line.Contains("No usable sandbox!", StringComparison.Ordinal);

    /// <summary>Why a browser ended as it started when the system refused its sandbox, which browser it was, and the ways
    /// to one that runs.</summary>
    public static string SandboxRefused(ChromiumFound browser) =>
        $"{browser.Name} ({browser.Program}) ended as it started: the system refused it its sandbox (\"No usable sandbox!\"), as Ubuntu 23.10 "
        + "and later do for a Chromium browser with no AppArmor profile of its own. Install Google Chrome or Microsoft Edge from their own packages "
        + $"(google.com/chrome, microsoft.com/edge), which carry their profile, or give this browser an AppArmor profile that allows it user namespaces: {SandboxPage}";

    /// <summary>Why no browser is there to debug in, and how to get one: the program a setting (<paramref name="where"/>)
    /// names is not there, or none was found.</summary>
    public static string NoneFound(string? chosen, string where) => chosen is { Length: > 0 } named
        ? $"{where} names {named.Trim()} as the browser to debug them in, and there is no program there: name a Chromium browser that is, or {HowToGetOne}"
        : $"No Chromium browser (Chrome, Edge, Chromium or Brave) was found to debug them in: {HowToGetOne}, or name one in {where}";
}
