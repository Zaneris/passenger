using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which Chromium browser a Blazor app's WebAssembly half is debugged in: the program a setting names, alone where it
/// names one; else where each browser's installer puts it, Chrome before Edge before the rest, then the PATH; and what is
/// said where none is.</summary>
public sealed class ChromiumBrowsersTests
{
    private static Func<string, bool> Present(params string[] files) => path => files.Contains(path, StringComparer.Ordinal);

    [Fact]
    public void A_browser_a_setting_names_is_taken_with_its_arguments_and_one_not_there_finds_none_rather_than_another()
    {
        string chosen = Path.Combine("/opt", "thorium", "thorium");
        string chrome = Path.Combine("/Applications", "Google Chrome.app/Contents/MacOS/Google Chrome");
        ChromiumFound? found = ChromiumBrowsers.Find((chosen, ["--lang=fr"]), [], ["/Applications"], windows: false, Present(chosen, chrome));
        Assert.True(found is { Name: "thorium" } && found.Program == chosen && found.Arguments.SequenceEqual(["--lang=fr"]),
            $"The setting's browser gave {found}, so Debug would open another than the one the person chose");
        Assert.Null(ChromiumBrowsers.Find((Path.Combine("/opt", "gone"), []), [], ["/Applications"], windows: false, Present(chrome)));
    }

    [Fact]
    public void Chrome_comes_before_Edge_in_Applications_and_Edge_is_found_in_the_users_own_Applications()
    {
        string home = Path.Combine("/Users", "someone", "Applications");
        string edge = Path.Combine(home, "Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
        string chrome = Path.Combine("/Applications", "Google Chrome.app/Contents/MacOS/Google Chrome");
        Assert.Equal("Google Chrome", ChromiumBrowsers.Find(null, [], ["/Applications", home], windows: false, Present(edge, chrome))?.Name);
        ChromiumFound? alone = ChromiumBrowsers.Find(null, [], ["/Applications", home], windows: false, Present(edge));
        Assert.True(alone is { Name: "Microsoft Edge" } && alone.Program == edge, $"Edge in ~/Applications alone gave {alone}");
    }

    [Fact]
    public void On_Windows_Edge_under_Program_Files_is_found_and_on_the_PATH_Chrome_anywhere_comes_before_Chromium()
    {
        string edge = Path.Combine(@"C:\Program Files (x86)", @"Microsoft\Edge\Application\msedge.exe");
        Assert.Equal(edge, ChromiumBrowsers.Find(null, [], [@"C:\Program Files", @"C:\Program Files (x86)"], windows: true, Present(edge))?.Program);
        string chromium = Path.Combine("/usr/bin", "chromium");
        string chrome = Path.Combine("/opt/google/bin", "google-chrome");
        ChromiumFound? found = ChromiumBrowsers.Find(null, ["/usr/bin", "/opt/google/bin"], [], windows: false, Present(chromium, chrome));
        Assert.True(found?.Program == chrome, $"Chromium first on the PATH and Chrome after gave {found}");
        Assert.Equal(chromium, ChromiumBrowsers.Find(null, ["/usr/bin"], [], windows: false, Present(chromium))?.Program);
    }

    [Fact]
    public void No_browser_found_says_how_to_get_one_and_a_setting_naming_nothing_says_which_it_names()
    {
        Assert.Null(ChromiumBrowsers.Find(null, ["/usr/bin"], ["/Applications"], windows: false, Present()));
        Assert.Equal("No Chromium browser (Chrome, Edge, Chromium or Brave) was found to debug them in: install Google Chrome (google.com/chrome) or Microsoft Edge "
            + "(microsoft.com/edge), or name one in Settings | Run", ChromiumBrowsers.NoneFound(null, "Settings | Run"));
        Assert.Equal("Settings | Run names /opt/gone as the browser to debug them in, and there is no program there: name a Chromium browser that is, or install "
            + "Google Chrome (google.com/chrome) or Microsoft Edge (microsoft.com/edge)", ChromiumBrowsers.NoneFound(" /opt/gone ", "Settings | Run"));
    }
}
