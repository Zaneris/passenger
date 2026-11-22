using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which Chromium browser a Blazor app's WebAssembly half is debugged in: the program a setting names, alone where it
/// names one; else where each browser's installer puts it, Chrome before Edge before the rest, then the PATH; and what is
/// said where none is, and where the system refuses one its sandbox.</summary>
public sealed class ChromiumBrowsersTests
{
    private static Func<string, bool> Present(params string[] files) => path => files.Contains(path, StringComparer.Ordinal);

    /// <summary>A Chromium browser is found where the setting or system has it, and its fatal words heard.</summary>
    [Fact]
    public void A_chromium_browser_is_found_and_heard_as_it_says() => Cases.All(
        (nameof(A_browser_a_setting_names_is_taken_with_its_arguments_and_one_not_there_finds_none_rather_than_another),
            A_browser_a_setting_names_is_taken_with_its_arguments_and_one_not_there_finds_none_rather_than_another),
        (nameof(Firefox_Safari_and_their_kin_are_known_to_be_no_chromium_and_chromium_browsers_or_unknown_ones_are_not_said_to_be),
            Firefox_Safari_and_their_kin_are_known_to_be_no_chromium_and_chromium_browsers_or_unknown_ones_are_not_said_to_be),
        (nameof(Chrome_comes_before_Edge_in_Applications_and_Edge_is_found_in_the_users_own_Applications),
            Chrome_comes_before_Edge_in_Applications_and_Edge_is_found_in_the_users_own_Applications),
        (nameof(On_Windows_Edge_under_Program_Files_is_found_and_on_the_PATH_Chrome_anywhere_comes_before_Chromium),
            On_Windows_Edge_under_Program_Files_is_found_and_on_the_PATH_Chrome_anywhere_comes_before_Chromium),
        (nameof(No_browser_found_says_how_to_get_one_and_a_setting_naming_nothing_says_which_it_names),
            No_browser_found_says_how_to_get_one_and_a_setting_naming_nothing_says_which_it_names),
        (nameof(Chromiums_fatal_word_that_it_has_no_usable_sandbox_is_its_refusal_and_its_words_name_the_browser_and_the_fixes),
            Chromiums_fatal_word_that_it_has_no_usable_sandbox_is_its_refusal_and_its_words_name_the_browser_and_the_fixes),
        (nameof(Chromiums_word_that_its_network_service_crashed_or_was_terminated_is_its_network_services_end_and_its_other_errors_are_not),
            Chromiums_word_that_its_network_service_crashed_or_was_terminated_is_its_network_services_end_and_its_other_errors_are_not),
        (nameof(A_macs_browsers_are_looked_for_in_the_accounts_own_applications_whatever_HOME_says),
            A_macs_browsers_are_looked_for_in_the_accounts_own_applications_whatever_HOME_says),
        (nameof(The_accounts_home_is_the_systems_own_for_this_user), The_accounts_home_is_the_systems_own_for_this_user));

    private void A_browser_a_setting_names_is_taken_with_its_arguments_and_one_not_there_finds_none_rather_than_another()
    {
        string chosen = Path.Combine("/opt", "thorium", "thorium");
        string chrome = Path.Combine("/Applications", "Google Chrome.app/Contents/MacOS/Google Chrome");
        ChromiumFound? found = ChromiumBrowsers.Find((chosen, ["--lang=fr"]), [], ["/Applications"], windows: false, Present(chosen, chrome));
        Assert.True(found is { Name: "thorium" } && found.Program == chosen && found.Arguments.SequenceEqual(["--lang=fr"]),
            $"The setting's browser gave {found}, so Debug would open another than the one the person chose");
        Assert.Null(ChromiumBrowsers.Find((Path.Combine("/opt", "gone"), []), [], ["/Applications"], windows: false, Present(chrome)));
    }

    private void Firefox_Safari_and_their_kin_are_known_to_be_no_chromium_and_chromium_browsers_or_unknown_ones_are_not_said_to_be()
    {
        string[] others =
        [
            Path.Combine("/usr", "bin", "firefox"), Path.Combine("/usr", "bin", "firefox-esr"), Path.Combine("/Applications", "Firefox.app"),
            Path.Combine("/Applications", "Firefox Developer Edition.app", "Contents", "MacOS", "firefox"), Path.Combine("/Applications", "Safari.app"),
            Path.Combine("/Applications", "Safari.app", "Contents", "MacOS", "Safari"), Path.Combine("/opt", "librewolf", "librewolf"), "firefox.exe",
        ];
        Assert.True(others.All(ChromiumBrowsers.NotChromium), $"Not known as no Chromium: {string.Join(", ", others.Where(o => !ChromiumBrowsers.NotChromium(o)))}");
        string[] chromiums =
        [
            Path.Combine("/Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome"), Path.Combine("/Applications", "Microsoft Edge.app"),
            Path.Combine("/usr", "bin", "chromium"), Path.Combine("/opt", "vivaldi", "vivaldi"), "msedge.exe", "chrome.exe", Path.Combine("/usr", "local", "bin", "my-browser"),
        ];
        Assert.True(!chromiums.Any(ChromiumBrowsers.NotChromium), $"Said to be no Chromium: {string.Join(", ", chromiums.Where(ChromiumBrowsers.NotChromium))}");
    }

    private void Chrome_comes_before_Edge_in_Applications_and_Edge_is_found_in_the_users_own_Applications()
    {
        string home = Path.Combine("/Users", "someone", "Applications");
        string edge = Path.Combine(home, "Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
        string chrome = Path.Combine("/Applications", "Google Chrome.app/Contents/MacOS/Google Chrome");
        Assert.Equal("Google Chrome", ChromiumBrowsers.Find(null, [], ["/Applications", home], windows: false, Present(edge, chrome))?.Name);
        ChromiumFound? alone = ChromiumBrowsers.Find(null, [], ["/Applications", home], windows: false, Present(edge));
        Assert.True(alone is { Name: "Microsoft Edge" } && alone.Program == edge, $"Edge in ~/Applications alone gave {alone}");
    }

    private void On_Windows_Edge_under_Program_Files_is_found_and_on_the_PATH_Chrome_anywhere_comes_before_Chromium()
    {
        string edge = Path.Combine(@"C:\Program Files (x86)", @"Microsoft\Edge\Application\msedge.exe");
        Assert.Equal(edge, ChromiumBrowsers.Find(null, [], [@"C:\Program Files", @"C:\Program Files (x86)"], windows: true, Present(edge))?.Program);
        string chromium = Path.Combine("/usr/bin", "chromium");
        string chrome = Path.Combine("/opt/google/bin", "google-chrome");
        ChromiumFound? found = ChromiumBrowsers.Find(null, ["/usr/bin", "/opt/google/bin"], [], windows: false, Present(chromium, chrome));
        Assert.True(found?.Program == chrome, $"Chromium first on the PATH and Chrome after gave {found}");
        Assert.Equal(chromium, ChromiumBrowsers.Find(null, ["/usr/bin"], [], windows: false, Present(chromium))?.Program);
    }

    private void No_browser_found_says_how_to_get_one_and_a_setting_naming_nothing_says_which_it_names()
    {
        Assert.Null(ChromiumBrowsers.Find(null, ["/usr/bin"], ["/Applications"], windows: false, Present()));
        Assert.Equal("No Chromium browser (Chrome, Edge, Chromium or Brave) was found to debug them in: install Google Chrome (google.com/chrome) or Microsoft Edge "
            + "(microsoft.com/edge), or name one in Settings | Run", ChromiumBrowsers.NoneFound(null, "Settings | Run"));
        Assert.Equal("Settings | Run names /opt/gone as the browser to debug them in, and there is no program there: name a Chromium browser that is, or install "
            + "Google Chrome (google.com/chrome) or Microsoft Edge (microsoft.com/edge)", ChromiumBrowsers.NoneFound(" /opt/gone ", "Settings | Run"));
    }

    private void Chromiums_fatal_word_that_it_has_no_usable_sandbox_is_its_refusal_and_its_words_name_the_browser_and_the_fixes()
    {
        Assert.True(ChromiumBrowsers.RefusesSandbox("[2817:2817:1102/101500.123456:FATAL:zygote_host_impl_linux.cc(128)] No usable sandbox! If you are running "
            + "on Ubuntu 23.10+ or another Linux distro that has disabled unprivileged user namespaces with AppArmor, see https://chromium.googlesource.com/"));
        Assert.False(ChromiumBrowsers.RefusesSandbox("[2817:2817:1102/101500.123456:ERROR:bus.cc(407)] Failed to connect to the bus: no such file"));
        string said = ChromiumBrowsers.SandboxRefused(new ChromiumFound("/opt/chrome-linux64/chrome", "Chromium"));
        Assert.StartsWith("Chromium (/opt/chrome-linux64/chrome) ended as it started: the system refused it its sandbox", said, StringComparison.Ordinal);
        Assert.Contains("Install Google Chrome or Microsoft Edge from their own packages (google.com/chrome, microsoft.com/edge), which carry their profile", said, StringComparison.Ordinal);
        Assert.EndsWith($"an AppArmor profile that allows it user namespaces: {ChromiumBrowsers.SandboxPage}", said, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-sandbox", said, StringComparison.Ordinal);
    }

    private void Chromiums_word_that_its_network_service_crashed_or_was_terminated_is_its_network_services_end_and_its_other_errors_are_not()
    {
        Assert.True(ChromiumBrowsers.NetworkServiceEnded(
            "[5517:93912688:1115/103621.768538:ERROR:content/browser/network_service_instance_impl.cc:652] Network service crashed or was terminated, restarting service."));
        Assert.False(ChromiumBrowsers.NetworkServiceEnded("[77108:93787120:1115/102555.667304:ERROR:ui/display/mac/cv_display_link_mac.mm:188] CVDisplayLinkCreateWithCGDisplay failed."));
        Assert.False(ChromiumBrowsers.NetworkServiceEnded("DevTools listening on ws://127.0.0.1:61669/devtools/browser/4f00d654-428c-4e44-a469-4ccf3b36e780"));
    }

    private void A_macs_browsers_are_looked_for_in_the_accounts_own_applications_whatever_HOME_says()
    {
        Assert.Equal(["/Applications", Path.Combine("/Users/ada", "Applications")], ChromiumBrowsers.MacFolders("/Users/ada", "/private/tmp/run/home"));
        Assert.Equal(["/Applications", Path.Combine("/private/tmp/run/home", "Applications")], ChromiumBrowsers.MacFolders(null, "/private/tmp/run/home"));
        string chrome = Path.Combine("/Users/ada", "Applications", "Google Chrome.app/Contents/MacOS/Google Chrome");
        Assert.Equal(chrome, ChromiumBrowsers.Find(null, [], ChromiumBrowsers.MacFolders("/Users/ada", "/elsewhere"), windows: false, path => path == chrome)?.Program);
    }

    private void The_accounts_home_is_the_systems_own_for_this_user()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no account entry for its home: the profile folder is it");
        string? home = Libc.AccountHome;
        Assert.NotNull(home);
        Assert.True(Directory.Exists(home), $"The account's home {home} is no folder");
    }
}
