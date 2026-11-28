using System.Diagnostics;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>A browser a person names for a run's page, as a command line (<see cref="Line"/>), and where they named it, in
/// words that begin a sentence (<see cref="Where"/>).</summary>
public sealed record PageBrowser(string Line, string Where);

/// <summary>The browser a run's page opens in, and Debug drives to debug a Blazor app's WebAssembly half: the one its run
/// configuration names (<see cref="RunConfiguration.Browser"/>), else Settings' (<see cref="AppSettings.Browser"/>), else
/// none, the system's default browser. Each is the person's, never an agent's (<see cref="AppSettings.NamingPrograms"/>),
/// started apart from Passenger, as the browser is theirs and outlives it.</summary>
public static class PageBrowsers
{
    /// <summary>The row naming it, in Settings and in a run configuration.</summary>
    public const string Row = "Browser";

    /// <summary>Where Settings names it, in words.</summary>
    public static string SettingPlace => SettingsDialog.PlaceOfRow(SettingsDialog.RunPage, Row);

    /// <summary>Where a run configuration names its own, in words.</summary>
    public static string ConfigurationPlace(string configuration) => $"Run › Edit Configurations › {configuration} › {Row}";

    /// <summary>What a browser row left empty says: the system's own opens the page.</summary>
    public const string SystemDefault = "The system's default browser";

    /// <summary>The browser of the run of <paramref name="configuration"/>: the line it names (<paramref name="own"/>), else
    /// Settings' (<paramref name="setting"/>); null for the system's default.</summary>
    public static PageBrowser? Of(string? own, string configuration, string? setting) =>
        !string.IsNullOrWhiteSpace(own) ? new PageBrowser(own.Trim(), ConfigurationPlace(configuration))
        : !string.IsNullOrWhiteSpace(setting) ? new PageBrowser(setting.Trim(), SettingPlace)
        : null;

    /// <summary>The browser Settings names (<paramref name="setting"/>), for a page no run of a configuration opens; null
    /// for the system's default.</summary>
    public static PageBrowser? Of(string? setting) => Of(null, string.Empty, setting);

    /// <summary>The program and the arguments a browser's command line gives: the whole line when it is a path that is there
    /// (one with spaces needs no quotes; on macOS an app bundle too), else its first word, found on the PATH where it names
    /// no folder, and those after it; null for none.</summary>
    public static (string Program, IReadOnlyList<string> Arguments)? Named(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        string whole = line.Trim();
        if (File.Exists(whole) || IsBundle(whole))
        {
            return (whole, []);
        }
        IReadOnlyList<string> words = CommandLine.Split(whole);
        if (words is not [{ Length: > 0 } program, ..])
        {
            return null;
        }
        bool bare = Path.GetFileName(program) == program;
        return (bare && Programs.Command(program) is { } found ? found : program, [.. words.Skip(1)]);
    }

    /// <summary>Whether a path is a macOS app bundle that is there, which opens as the Finder opens it.</summary>
    private static bool IsBundle(string path) => OperatingSystem.IsMacOS() && MacBundles.Named(path) && Directory.Exists(path);

    /// <summary>What opens <paramref name="page"/> in the browser, and whether it is the system's open of an app bundle,
    /// which ends once the app has the page; or why nothing can, its words after "Unable to open … in the browser: ".</summary>
    public static (ProcessStartInfo? Start, bool Bundle, string? WhyNot) Opening(PageBrowser browser, Uri page)
    {
        if (Named(browser.Line) is not ({ } program, { } arguments))
        {
            return (null, false, $"{browser.Where} names no program to start");
        }
        bool bundle = IsBundle(program);
        if (!bundle && !File.Exists(program))
        {
            return (null, false, $"{browser.Where} names {browser.Line}, and there is no program there");
        }
        (string started, IReadOnlyList<string> given) = BrowserPrograms.Opening(program, arguments, page, bundle);
        return (ChildProcesses.Command(started, given), bundle, null);
    }

    /// <summary>What is said when a page could not open in the browser.</summary>
    public static string NotOpened(Uri page, string why) => $"Unable to open {page} in the browser: {why}";
}
