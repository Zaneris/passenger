using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.Updates;
using Passenger.Core;
using Passenger.Terminal;
using Velopack.Locators;

namespace Passenger.Tests;

/// <summary>Passenger says when a newer release is out, and installs it: an installed copy downloads it, checks its
/// SHA-256 (refusing one that does not match, saying why), offers Restart to Update and installs it as it ends; any other
/// copy says once per version that it is out, never of an older one, a pre-release, a draft or a failure. Check for
/// Updates asks whenever a person wants; the setting off, nothing is asked or downloaded. A stand-in on a loopback port
/// answers for GitHub and its downloads, which no test reaches; each installed copy is in a test folder, and each
/// version's disk image is made once for the class (<see cref="DiskImages"/>), each Linux release's archive by the test
/// (<see cref="LinuxInstall"/>). Agents' relays running from an installed copy are the build's own, started as an agent
/// starts one (<see cref="McpRig"/>).</summary>
public sealed class UpdatesTests : McpRig, IClassFixture<UpdatesTests.DiskImages>
{
    /// <summary>The downloads the release workflow publishes with every release, by fixed names.</summary>
    private static readonly string[] Downloads =
    [
        "Passenger-macos-arm64.dmg", "Passenger-macos-x64.dmg", "Passenger-windows-x64.exe", "Passenger-windows-arm64.exe",
        "Passenger-linux-x64.tar.gz", "Passenger-linux-arm64.tar.gz",
    ];

    private readonly string settings = TestFolders.Create("updates-");
    private readonly StandIn github = new();
    private readonly DiskImages images;

    public UpdatesTests(DiskImages images) => this.images = images;

    public override void Dispose()
    {
        github.Dispose();
        TestFolders.Delete(settings);
        base.Dispose();
    }

    /// <summary>The gate's five checks for a newer release as one flow, to take their place once it has its record on
    /// CI: a second window asking none of its own, the same, older or pre-release version saying nothing, the check
    /// turned off, a failed check said and logged, and a newer release announced once.</summary>
    [AvaloniaFact]
    public void The_gates_update_checks_hold_as_one_flow()
    {
        Flow.StepApart("a second solution window asks for no release of its own and takes the watch over as the first closes", () => new UpdatesTests(images), t => t.A_second_solution_window_asks_for_no_release_of_its_own_and_takes_the_watch_over_as_the_first_closes());
        Flow.StepApart("the same version an older one a pre release or a draft says nothing and Check for Updates says up to date", () => new UpdatesTests(images), t => t.The_same_version_an_older_one_a_pre_release_or_a_draft_says_nothing_and_Check_for_Updates_says_up_to_date());
        Flow.StepApart("with Check for updates off in Settings nothing is asked until it is on again", () => new UpdatesTests(images), t => t.With_Check_for_updates_off_in_Settings_nothing_is_asked_until_it_is_on_again());
        Flow.StepApart("a failed or unanswered check says nothing and is logged and Check for Updates says why", () => new UpdatesTests(images), t => t.A_failed_or_unanswered_check_says_nothing_and_is_logged_and_Check_for_Updates_says_why());
        Flow.StepApart("a newer release is announced once naming its version with a link to this systems download", () => new UpdatesTests(images), t => t.A_newer_release_is_announced_once_naming_its_version_with_a_link_to_this_systems_download());
    }

    /// <summary>A newer release is announced as its answer comes, asked again a wall-clock day later, named a security
    /// release where its notes say so, downloaded once for every window and checked against its SHA-256, never by a
    /// copy no installer put in place or with checks off, and restarted into only by the person, as one flow, each step
    /// the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public async Task A_release_is_announced_downloaded_checked_and_restarted_into_by_the_person_alone()
    {
        Flow.StepApart("a release answered late is announced as its answer comes and the notice never shows without its words", () => new UpdatesTests(images), t => t.A_release_answered_late_is_announced_as_its_answer_comes_and_the_notice_never_shows_without_its_words());
        Flow.StepApart("a failed check is asked again at the next look and an answered one a day later by the wall clock however long the machine slept", () => new UpdatesTests(images), t => t.A_failed_check_is_asked_again_at_the_next_look_and_an_answered_one_a_day_later_by_the_wall_clock_however_long_the_machine_slept());
        Flow.StepApart("a release whose notes mark it a security release is named one saying what it fixes and one unmarked is said as before", () => new UpdatesTests(images), t => t.A_release_whose_notes_mark_it_a_security_release_is_named_one_saying_what_it_fixes_and_one_unmarked_is_said_as_before());
        Flow.StepApart("a release after a security release this version lacks is a security update in that releases words", () => new UpdatesTests(images), t => t.A_release_after_a_security_release_this_version_lacks_is_a_security_update_in_that_releases_words());
        await Flow.StepApartAsync("a 0 3 4 is told 0 4 1 is a security update in 0 4 0s words and a 0 4 0 that it is out", () => new UpdatesTests(images), t => t.A_0_3_4_is_told_0_4_1_is_a_security_update_in_0_4_0s_words_and_a_0_4_0_that_it_is_out());
        Flow.StepApart("an installed Passenger offers to restart into a security release at once when it is ready", () => new UpdatesTests(images), t => t.An_installed_Passenger_offers_to_restart_into_a_security_release_at_once_when_it_is_ready());
        Flow.StepApart("an agents click on Restart to Update in the persons window restarts nothing and is told why", () => new UpdatesTests(images), t => t.An_agents_click_on_Restart_to_Update_in_the_persons_window_restarts_nothing_and_is_told_why());
        Flow.StepApart("a newer release its download and Restart to Update are said in every window from one check and one download", () => new UpdatesTests(images), t => t.A_newer_release_its_download_and_Restart_to_Update_are_said_in_every_window_from_one_check_and_one_download());
        Flow.StepApart("a download that does not match the SHA 256 its release gives is refused and the person told why", () => new UpdatesTests(images), t => t.A_download_that_does_not_match_the_SHA_256_its_release_gives_is_refused_and_the_person_told_why());
        Flow.StepApart("with Check for updates off nothing is downloaded and turning it off drops a download made", () => new UpdatesTests(images), t => t.With_Check_for_updates_off_nothing_is_downloaded_and_turning_it_off_drops_a_download_made());
        Flow.StepApart("a copy its installer or disk image did not put in place never downloads an update", () => new UpdatesTests(images), t => t.A_copy_its_installer_or_disk_image_did_not_put_in_place_never_downloads_an_update());
        Flow.StepApart("an installed Passenger downloads a newer release in the background checks it and offers Restart to Update", () => new UpdatesTests(images), t => t.An_installed_Passenger_downloads_a_newer_release_in_the_background_checks_it_and_offers_Restart_to_Update());
    }

    /// <summary>On macOS Restart to Update puts the disk image's app in place of the old and starts it, a disk image
    /// the Mac refuses or holds opens once let go, another Passenger open holds the update back, an update that could
    /// not take the app's place or was cut between its renames is said, a state on another volume opens its image on
    /// the startup disk, and an agent's relay follows, as one flow, each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_Macs_update_puts_its_disk_images_app_in_place_or_says_where_each_was_left()
    {
        Flow.StepApart("restart to Update puts the new version in place of the old and starts it and closing installs it too (DiskImageTool.Hdiutil)", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.Restart_to_Update_puts_the_new_version_in_place_of_the_old_and_starts_it_and_closing_installs_it_too(DiskImageTool.Hdiutil);
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("a disk image the Mac refuses as it lands opens once let go and one never let go is refused in the tools words (DiskImageTool.Hdiutil, \"hdiutil: attach failed - Resource temporarily unavailable\", \"{0}\")", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.A_disk_image_the_Mac_refuses_as_it_lands_opens_once_let_go_and_one_never_let_go_is_refused_in_the_tools_words(DiskImageTool.Hdiutil, "hdiutil: attach failed - Resource temporarily unavailable", "{0}");
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("a disk image whose disk is held as it is let go goes once let go and one held throughout is said and opens again (DiskImageTool.Hdiutil)", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.A_disk_image_whose_disk_is_held_as_it_is_let_go_goes_once_let_go_and_one_held_throughout_is_said_and_opens_again(DiskImageTool.Hdiutil);
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("another Passenger open from the same app holds the update back until it closes and the next start names it", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.Another_Passenger_open_from_the_same_app_holds_the_update_back_until_it_closes_and_the_next_start_names_it();
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("an update that could not take the apps place is said at the next start and is still ready", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.An_update_that_could_not_take_the_apps_place_is_said_at_the_next_start_and_is_still_ready();
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("an app whose place is taken between its two renames says where each app was left", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.An_app_whose_place_is_taken_between_its_two_renames_says_where_each_app_was_left();
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("an update whose state is on another volume opens its disk image on the startup disk in a folder of the users alone", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.An_update_whose_state_is_on_another_volume_opens_its_disk_image_on_the_startup_disk_in_a_folder_of_the_users_alone();
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
        Flow.StepApart("an agents relay follows the update in its own process its calls meanwhile and after answered over the same connection", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsMacOS())
            {
                t.An_agents_relay_follows_the_update_in_its_own_process_its_calls_meanwhile_and_after_answered_over_the_same_connection();
            }
            else
            {
                Assert.Skip("This step runs on macOS alone");
            }
        });
    }

    /// <summary>On Windows another Passenger running from the install holds the update back, and an Update.exe that
    /// does not start leaves it ready, each said at the next start, as one flow, each step in an instance of its
    /// own.</summary>
    [AvaloniaFact]
    public void A_Windows_update_is_held_back_by_another_Passenger_or_left_ready_and_said()
    {
        Flow.StepApart("on Windows another Passenger running from the install holds the update back and the next start names it", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsWindows())
            {
                t.On_Windows_another_Passenger_running_from_the_install_holds_the_update_back_and_the_next_start_names_it();
            }
            else
            {
                Assert.Skip("This step runs on Windows alone");
            }
        });
        Flow.StepApart("on Windows an Update exe that does not start leaves the update ready and the next start says why", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsWindows())
            {
                t.On_Windows_an_Update_exe_that_does_not_start_leaves_the_update_ready_and_the_next_start_says_why();
            }
            else
            {
                Assert.Skip("This step runs on Windows alone");
            }
        });
    }

    /// <summary>On Linux an install of the person's own is put in place as Passenger restarts, one that could not be
    /// replaced is left as it was, one for everyone is made ready where only the person writes or names the command
    /// that installs it, and a package manager's copy downloads nothing, as one flow, each step in an instance of its
    /// own.</summary>
    [AvaloniaFact]
    public void A_Linux_update_replaces_its_own_install_or_leaves_it_as_it_was_and_says_why()
    {
        Flow.StepApart("a Linux install of ones own is put in place as Passenger restarts its agents relay following and its menu entry refreshed", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsLinux())
            {
                t.A_Linux_install_of_ones_own_is_put_in_place_as_Passenger_restarts_its_agents_relay_following_and_its_menu_entry_refreshed();
            }
            else
            {
                Assert.Skip("This step runs on Linux alone");
            }
        });
        Flow.StepApart("a Linux copy a package manager installed says its package manager updates it and downloads nothing", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsLinux())
            {
                t.A_Linux_copy_a_package_manager_installed_says_its_package_manager_updates_it_and_downloads_nothing();
            }
            else
            {
                Assert.Skip("This step runs on Linux alone");
            }
        });
        Flow.StepApart("a Linux install that could not be replaced is left as it was and the next start says why", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsLinux())
            {
                t.A_Linux_install_that_could_not_be_replaced_is_left_as_it_was_and_the_next_start_says_why();
            }
            else
            {
                Assert.Skip("This step runs on Linux alone");
            }
        });
        Flow.StepApart("a Linux install for everyone this user cannot write says the command that installs it and a click copies it", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsLinux())
            {
                t.A_Linux_install_for_everyone_this_user_cannot_write_says_the_command_that_installs_it_and_a_click_copies_it();
            }
            else
            {
                Assert.Skip("This step runs on Linux alone");
            }
        });
        Flow.StepApart("a Linux install for everyone is made ready where only the person writes and install sh takes only the archive Passenger checked", () => new UpdatesTests(images), t =>
        {
            if (OperatingSystem.IsLinux())
            {
                t.A_Linux_install_for_everyone_is_made_ready_where_only_the_person_writes_and_install_sh_takes_only_the_archive_Passenger_checked();
            }
            else
            {
                Assert.Skip("This step runs on Linux alone");
            }
        });
    }

    [Gate]
    [AvaloniaFact]
    public void A_newer_release_is_announced_once_naming_its_version_with_a_link_to_this_systems_download()
    {
        using IDisposable theme = TestsTheme();
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}");
        MainWindow window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, $"The window asked for the latest release {github.Requests.Count} times as it started, not once");

        Assert.True(Notice(window) == $"Passenger {newer} available", $"A newer release, v{newer}, is out, but {UpdatesSaid(window)}");
        string download = $"https://github.com/Zaneris/passenger/releases/download/v{newer}/{ThisSystemsDownload()}";
        Assert.True(window.UpdateShown?.Link.AbsoluteUri == download,
            $"The notice's link opens {window.UpdateShown?.Link}, not this system's download from the release, {download}");
        Assert.Contains(download, ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty);
        Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
        Assert.True(window.Browsed.LastOrDefault()?.AbsoluteUri == download, $"The notice's click opened [{string.Join(", ", window.Browsed)}], not {download} through Browse");
        Assert.True(window.Status == before, $"A check of the window's own said '{window.Status}' in the status bar's message, where it says nothing");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} is out (v{newer}; this is {Product.Version}): {download}", StringComparison.Ordinal));

        string[] head = github.Requests[0].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"GET {github.Listing.PathAndQuery} HTTP/1.1", head[0]);
        Assert.Contains($"User-Agent: Passenger/{Product.Version}", head);
        string[] headers = [.. head.Skip(1).Select(h => h.Split(':')[0]).Order(StringComparer.OrdinalIgnoreCase)];
        Assert.True(headers.SequenceEqual(["Accept", "Host", "User-Agent"], StringComparer.OrdinalIgnoreCase),
            $"The request sent more than the request itself: {string.Join(", ", headers)}");
        window.Close();

        window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 2, "The window started again did not ask for the latest release");
        Assert.True(Notice(window) is null, $"v{newer} was announced already, yet the window started again announces it once more: {UpdatesSaid(window)}");

        CheckForUpdates(window);
        Assert.True(Notice(window) == $"Passenger {newer} available", $"Check for Updates did not show the newer release the window had announced before: {UpdatesSaid(window)}");
        Assert.Equal($"Passenger {newer} is available", window.Status);
        window.Close();

        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}");
        window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {later} available", $"v{later} came out after v{newer} was announced, but {UpdatesSaid(window)}");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_second_solution_window_asks_for_no_release_of_its_own_and_takes_the_watch_over_as_the_first_closes()
    {
        using IDisposable theme = TestsTheme();
        github.Answer = Release($"v{Bumped(1, 0)}");
        string shop = TestFolders.Create("updates-shop-");
        string bank = TestFolders.Create("updates-bank-");
        MainWindow window = Started();
        window.OpenFolder(shop);
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, $"The window asked for the latest release {github.Requests.Count} times as it started, not once");

        // Another solution in a window of its own: the process has asked already.
        window.ApplySettings(window.Settings with { OpenProjectIn = Passenger.App.Settings.OpenProjectIn.NewWindow });
        Assert.Null(window.Open(bank));
        Ui.Settle();
        MainWindow second = Assert.Single(PassengerApp.Windows, w => w != window);
        Assert.True(github.Requests.Count == 1, $"Two windows of one Passenger asked for the latest release {github.Requests.Count} times, not once");

        window.Close();
        Ui.Settle();
        Assert.True(github.Requests.Count == 2, $"The window left did not take the watch for updates over: {github.Requests.Count} requests");
        second.Close();
        TestFolders.Delete(shop);
        TestFolders.Delete(bank);
    }

    [Gate]
    [AvaloniaFact]
    public void The_same_version_an_older_one_a_pre_release_or_a_draft_says_nothing_and_Check_for_Updates_says_up_to_date()
    {
        using IDisposable theme = TestsTheme();
        github.Answer = Release($"v{Product.Version}");
        MainWindow window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before,
            $"The latest release is the running version, yet {UpdatesSaid(window)}");

        // Asked for, each of these is nothing newer, in Rider's words.
        (string What, StandIn.Reply Answer)[] nothingNewer =
        [
            ("the running version", Release($"v{Product.Version}")),
            ("an older version", Release("v0.0.1")),
            ("a newer pre-release by its tag", Release($"v{Bumped(1, 0)}-beta.1")),
            ("a newer release GitHub marks a pre-release", Release($"v{Bumped(1, 0)}", prerelease: true)),
            ("a newer draft", Release($"v{Bumped(1, 0)}", draft: true)),
            ("no release at all", Listed()),
            ("no list of releases (404 Not Found)", new StandIn.Reply(404, """{"message":"Not Found"}""")),
        ];
        foreach ((string what, StandIn.Reply answer) in nothingNewer)
        {
            github.Answer = answer;
            int asked = github.Requests.Count;
            CheckForUpdates(window);
            Assert.True(github.Requests.Count == asked + 1, $"Check for Updates did not ask for the latest release with {what} out");
            Assert.True(window.Status == MainWindow.UpToDate, $"With {what} out, Check for Updates says '{window.Status}', not '{MainWindow.UpToDate}'");
            Assert.True(Notice(window) is null, $"With {what} out, {UpdatesSaid(window)}");
        }
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void With_Check_for_updates_off_in_Settings_nothing_is_asked_until_it_is_on_again()
    {
        using IDisposable theme = TestsTheme();
        github.Answer = Release($"v{Bumped(1, 0)}");
        MainWindow window = Started();
        Assert.True(window.Settings.CheckForUpdates, "Check for updates is not on by default");

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "updates";
        Ui.Settle();
        Assert.Equal(SettingsDialog.UpdatesPage, dialog.Page);
        Assert.Contains("Check for updates", dialog.Shown);
        dialog.Change(s => s with { CheckForUpdates = false });
        dialog.Ok();
        Ui.Settle();
        window.Close();

        window = Started();
        Assert.False(window.Settings.CheckForUpdates, "Check for updates turned off was on again in the next window");
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 0, $"With Check for updates off, the window asked for the latest release {github.Requests.Count} time(s)");
        Assert.True(Notice(window) is null, $"With Check for updates off, {UpdatesSaid(window)}");

        window.ApplySettings(window.Settings with { CheckForUpdates = true });
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, "Check for updates turned on again did not ask for the latest release");
        Assert.True(Notice(window) is not null, $"Check for updates turned on again did not announce the newer release: {UpdatesSaid(window)}");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_failed_or_unanswered_check_says_nothing_and_is_logged_and_Check_for_Updates_says_why()
    {
        using IDisposable theme = TestsTheme();
        Assert.True(ReleaseFeed.Default.Listing.IsLoopback, $"A test's window would ask {ReleaseFeed.Default.Listing} for the latest release, not a loopback address");

        github.Answer = new StandIn.Reply(500, """{"message":"Server Error"}""");
        MainWindow window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before, $"A check that failed was shown or said, where it is only logged: {UpdatesSaid(window)}");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Listing} for the latest release: {github.Listing} answers 500 Internal Server Error", StringComparison.Ordinal));
        window.Close();

        // A server that takes the request and never answers is given up on at the feed's timeout.
        github.Answer = null;
        window = Started(feedLimit: TimeSpan.FromSeconds(1));
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 2, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before, $"A check never answered was shown or said, where it is only logged: {UpdatesSaid(window)}");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Listing} for the latest release: no answer in 1 s", StringComparison.Ordinal));

        CheckForUpdates(window);
        Assert.Equal("Could not check for updates: no answer in 1 s", window.Status);
        window.Close();
    }

    /// <summary>A release answered late, as a cold start's first request on a busy machine may be, is announced once its
    /// answer comes within the feed's limit; until then the status bar shows nothing, and its notice comes with its words.</summary>
    private void A_release_answered_late_is_announced_as_its_answer_comes_and_the_notice_never_shows_without_its_words()
    {
        using IDisposable theme = TestsTheme();
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}");
        github.HoldAnswers();
        MainWindow window = Started();
        List<string?> shown = [];
        using IDisposable following = FollowNotice(window, shown);
        window.WatchForUpdates();
        Ui.PumpUntil(() => github.Requests.Count == 1, "the window to ask for the latest release as it started");
        Ui.Hold(TimeSpan.FromSeconds(2), "an answer later than a second, which a busy machine's first request may take");
        Assert.True(Notice(window) is null, $"The window shows a notice while its check still waits for the answer: {UpdatesSaid(window)}");

        github.Let();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {newer} available",
            $"v{newer} was answered 2 s after the window asked, well within its feed's limit, yet {UpdatesSaid(window)}");
        Assert.True(shown.SequenceEqual([$"Passenger {newer} available"]),
            $"The notice's button came into view {shown.Count} time(s), with [{string.Join(", ", shown.Select(words => words is null ? "no words" : $"'{words}'"))}], not once with its words");
        Assert.True(github.Requests.Count == 1, $"The window asked for the latest release {github.Requests.Count} times, not once");
        window.Close();
    }

    /// <summary>After the start a check comes a day after the last answer by the wall clock, which goes on while the
    /// machine sleeps where a window's timer waits, so a Passenger left open across nights still asks daily; one that
    /// failed (no network yet, GitHub refusing) is asked again at the next look rather than a day later.</summary>
    private void A_failed_check_is_asked_again_at_the_next_look_and_an_answered_one_a_day_later_by_the_wall_clock_however_long_the_machine_slept()
    {
        using IDisposable theme = TestsTheme();
        WallClock clock = new();
        TimeSpan look = TimeSpan.FromMilliseconds(400);
        github.Answer = new StandIn.Reply(500, """{"message":"Server Error"}""");
        MainWindow window = Started(clock: clock, look: look);
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count >= 1, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null, $"A check that failed was shown, where it is only logged: {UpdatesSaid(window)}");

        // GitHub answers again: the next look asks, where a day's wait would leave the release unsaid until tomorrow.
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}");
        Ui.WaitFor(() => Notice(window) == $"Passenger {newer} available",
            () => $"v{newer} is out and the check before failed, yet the window's next looks did not ask again: {UpdatesSaid(window)}");
        int asked = github.Requests.Count;

        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}");
        clock.Advance(TimeSpan.FromHours(23));
        Ui.Hold(look * 4, "several looks less than a day after the last answer");
        Assert.True(github.Requests.Count == asked,
            $"The window asked for the latest release {github.Requests.Count - asked} more time(s) within a day of its answer, where it asks once a day");

        // A night asleep: the wall clock passed the day while the window's timer counted nothing of it.
        clock.Advance(TimeSpan.FromHours(1));
        Ui.WaitFor(() => Notice(window) == $"Passenger {later} available",
            () => $"A day passed on the wall clock since the window's last answer, yet its next looks did not ask for v{later}: {UpdatesSaid(window)}");
        Assert.True(github.Requests.Count == asked + 1, $"The window asked {github.Requests.Count - asked} times a day after its answer, not once");
        window.Close();
    }

    /// <summary>A release's notes marked a security release's say what it fixes in their Security section's first line.</summary>
    private static string SecurityNotes(string version) =>
        $"{ReleaseNotes.SecurityMarker}\nPassenger {version} closes a hole.\n\n## Security\n\n"
        + "- **Agents reach only the open folder** ([#1](https://github.com/Zaneris/passenger/issues/1)): a path outside it is refused.\n- Another fix.\n\n## Fixed\n\n- Something else.\n";

    /// <summary>What <see cref="SecurityNotes"/> says the release fixes, in plain words.</summary>
    private const string SecurityFixes = "Agents reach only the open folder (#1): a path outside it is refused";

    private void A_release_whose_notes_mark_it_a_security_release_is_named_one_saying_what_it_fixes_and_one_unmarked_is_said_as_before()
    {
        using IDisposable theme = TestsTheme();
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", body: SecurityNotes(newer));
        MainWindow window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Security update: Passenger {newer} available", $"v{newer}'s notes mark it a security release, but {UpdatesSaid(window)}");
        string tip = ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty;
        Assert.StartsWith($"Passenger {newer} is a security update: {SecurityFixes}\nInstall it as soon as you can.\n", tip, StringComparison.Ordinal);
        Assert.Equal($"Passenger {newer} is a security update: {SecurityFixes}. Install it soon", window.Status);
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} is out (v{newer}; this is {Product.Version}), a security release: ", StringComparison.Ordinal));
        window.Close();

        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}", body: $"Passenger {later} fixes things.\n\n## Security\n\n- Not marked a security release.\n");
        window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {later} available", $"v{later}'s notes carry no mark, but {UpdatesSaid(window)}");
        Assert.DoesNotContain("security", ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, window.Status);
        window.Close();
    }

    /// <summary>A release after a security release this version lacks is a security update in that release's words, however
    /// many releases came between, a newer pre-release's or draft's mark passed over; once this version is that security
    /// release or newer, a release after it says nothing of security.</summary>
    private void A_release_after_a_security_release_this_version_lacks_is_a_security_update_in_that_releases_words()
    {
        using IDisposable theme = TestsTheme();
        string marked = Bumped(1, 0);
        string newer = Bumped(1, 1);
        string unreleased = "## Security\n\n- A fix not yet released.\n";
        github.Answer = Listed(
            Made($"v{Bumped(1, 3)}", draft: true, body: $"{ReleaseNotes.SecurityMarker}\n\n{unreleased}"),
            Made($"v{Bumped(1, 2)}-rc.1", body: $"{ReleaseNotes.SecurityMarker}\n\n{unreleased}"),
            Made($"v{newer}", body: $"Passenger {newer} fixes what {marked} left.\n\n## Fixed\n\n- A fix.\n"),
            Made($"v{marked}", body: SecurityNotes(marked)),
            Made($"v{Product.Version}", body: $"{ReleaseNotes.SecurityMarker}\n\n## Security\n\n- What this version fixed.\n"));
        MainWindow window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Security update: Passenger {newer} available",
            $"v{newer} follows v{marked}, a security release this version lacks, but {UpdatesSaid(window)}");
        string tip = ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty;
        Assert.True(tip.StartsWith($"Passenger {newer} is a security update: {SecurityFixes}\nInstall it as soon as you can.\n", StringComparison.Ordinal),
            $"The notice's tip does not say what v{marked} fixes, the newest security release after this version: {tip}");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} is out (v{newer}; this is {Product.Version}), a security update as v{marked} is a security release: ", StringComparison.Ordinal));
        window.Close();

        // This version is the newest security release: the one after it is an update like any other.
        string later = Bumped(1, 2);
        github.Answer = Listed(
            Made($"v{later}", body: $"Passenger {later} fixes things.\n"),
            Made($"v{Product.Version}", body: SecurityNotes(Product.Version)),
            Made("v0.0.1", body: SecurityNotes("0.0.1")));
        window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {later} available", $"Only this version and older ones are security releases, but {UpdatesSaid(window)}");
        Assert.DoesNotContain("security", ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, window.Status);
        window.Close();
    }

    /// <summary>GitHub's releases once 0.4.1 follows 0.4.0, a security release: a Passenger 0.3.4 lacks 0.4.0's fixes and is
    /// told 0.4.1 brings them, in 0.4.0's words, where a Passenger 0.4.0 has them and is told of 0.4.1 alone.</summary>
    private async Task A_0_3_4_is_told_0_4_1_is_a_security_update_in_0_4_0s_words_and_a_0_4_0_that_it_is_out()
    {
        github.Answer = Listed(
            Made("v0.4.1", body: "Passenger 0.4.1 fixes what 0.4.0 left.\n\n## Fixed\n\n- A fix.\n"),
            Made("v0.4.0", body: SecurityNotes("0.4.0")),
            Made("v0.3.4", body: "Passenger 0.3.4 fixes things.\n"));
        ReleaseFeed feed = new(github.Listing, ReleaseFeed.ShortTimeout.Scaled());

        ReleaseAnswer older = await Releases.AskAsync(feed, "0.3.4", TestContext.Current.CancellationToken);
        Assert.True(older is ReleaseAnswer.Newer { Release: { Version: "0.4.1", Security: SecurityFixes } },
            $"A Passenger 0.3.4, which lacks 0.4.0's security fixes, was not told 0.4.1 brings them: {older.Said}");
        Assert.True(github.Requests.Count == 1, $"Passenger asked {github.Requests.Count} times for the releases, not once");

        ReleaseAnswer current = await Releases.AskAsync(feed, "0.4.0", TestContext.Current.CancellationToken);
        Assert.True(current is ReleaseAnswer.Newer { Release: { Version: "0.4.1", Security: null } },
            $"A Passenger 0.4.0, which has 0.4.0's security fixes, was told 0.4.1 is other than an update with none: {current.Said}");
    }

    private void An_installed_Passenger_offers_to_restart_into_a_security_release_at_once_when_it_is_ready()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer), body: SecurityNotes(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to install security update Passenger {newer}", () => $"the status bar to offer the security update once v{newer} had downloaded: {UpdatesSaid(window)}");
        Assert.Equal($"Passenger {newer} is a security update: {SecurityFixes}, downloaded and checked", window.Status);
        Assert.Equal("restart into it now", window.StatusPlace?.Name);
        Assert.StartsWith($"Passenger {newer} is a security update: {SecurityFixes}\nRestart into it now rather than later.\n",
            ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>An attached agent's click on the status bar's Restart to Update in the person's window restarts nothing: it
    /// is told why, the status bar saying it too, the window stays open and the update still waits for the person.</summary>
    private void An_agents_click_on_Restart_to_Update_in_the_persons_window_restarts_nothing_and_is_told_why()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");
        string folder = PathNames.Canonical(Plain("restart-agent-"));
        window.OpenFolder(folder);
        window.ApplySettings(window.Settings with { McpServer = true });
        Ui.Settle();
        Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Task<CallToolResult> clicking = Waiting.Apart(() => Result(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#UpdateButton" }));
            Press(Question(window, "the question for the agent's click in Passenger's windows"), AgentToolDialog.AllowForSession);
            CallToolResult clicked = Await(clicking, "the agent's click on the status bar's Restart to Update");
            Assert.True(clicked.IsError == true && Text(clicked).Contains(Refusals.RestartIsThePersons, StringComparison.Ordinal),
                $"The agent's click on the status bar's Restart to Update was answered: {Text(clicked)}");
            Ui.Settle();
            Assert.True(window.IsVisible, "The agent's click on Restart to Update closed the person's window");
            Assert.Equal(Refusals.RestartIsThePersons, window.Status);
            Assert.True(Notice(window) == $"Restart to update to Passenger {newer}" && window.UpdateReady?.Version == newer,
                $"After the agent's click the update no longer waits for the person: {UpdatesSaid(window)}");
            Assert.True(copy.InstalledVersion is null || copy.InstalledVersion == Product.Version, $"The agent's click installed the update, as {copy.InstalledVersion}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    private void An_installed_Passenger_downloads_a_newer_release_in_the_background_checks_it_and_offers_Restart_to_Update()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        IReadOnlyDictionary<string, byte[]> update = copy.Update(newer);
        github.Answer = Release($"v{newer}", files: update);
        github.Hold(copy.Package(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();

        // The stand-in holds the download half way, so its progress shows.
        Ui.PumpUntil(() => Notice(window) is { } shown && shown.StartsWith($"Downloading Passenger {newer}: ", StringComparison.Ordinal) && shown != $"Downloading Passenger {newer}: 0%",
            $"the status bar to say how far v{newer}'s download has come");
        Assert.False(copy.Holds(newer), $"v{newer} was ready to install before its download had all come");
        github.Let();

        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update once v{newer} had downloaded");
        Assert.True(copy.Holds(newer), $"The status bar offers Restart to Update, but v{newer} is not where it installs from");
        Assert.True(window.UpdateReady?.Version == newer, $"The window offers Restart to Update with {window.UpdateReady?.Version ?? "nothing"} ready, not {newer}");
        Assert.True(copy.InstalledVersion is null || copy.InstalledVersion == Product.Version,
            $"The update was installed at once, as {copy.InstalledVersion}, before Passenger restarted");
        Assert.True(window.Actions.WhyNot(AppActions.RestartToUpdate, window.Actions.Context()) is null, "Restart to Update cannot run with the update ready");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} downloaded to ", StringComparison.Ordinal));

        foreach (string name in update.Keys)
        {
            string[] asked = github.RequestsFor(name);
            Assert.True(asked.Length == 1, $"{name} was downloaded {asked.Length} times, not once");
            string[] head = asked[0].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains($"User-Agent: Passenger/{Product.Version}", head);
            string[] headers = [.. head.Skip(1).Select(h => h.Split(':')[0]).Order(StringComparer.OrdinalIgnoreCase)];
            Assert.True(headers.SequenceEqual(["Host", "User-Agent"], StringComparer.OrdinalIgnoreCase),
                $"The download of {name} sent more than the request itself: {string.Join(", ", headers)}");
        }
        Assert.True(github.Requests.Count == update.Count + 1,
            $"The window made {github.Requests.Count} requests, not one for the release and one for each of the {update.Count} files of this system's update");
        window.Close();

        // Started again, as a person who closed Passenger with the update waiting: it is checked again, not downloaded again.
        window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the window started again to offer the update made ready before");
        Assert.True(github.RequestsFor(copy.Package(newer)).Length == 1, $"The window started again downloaded {copy.Package(newer)} once more");
        window.Close();
    }

    /// <summary>The window watching for updates may stand behind the person's others: what it finds, its download and
    /// Restart to Update are said in every window, the one in front and one opened afterwards, from its one check and
    /// one download; Check for Updates in another window is answered there, downloading nothing again.</summary>
    private void A_newer_release_its_download_and_Restart_to_Update_are_said_in_every_window_from_one_check_and_one_download()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        IReadOnlyDictionary<string, byte[]> update = copy.Update(newer);
        github.Answer = Release($"v{newer}", files: update);
        github.Hold(copy.Package(newer));
        string shop = TestFolders.Create("updates-shop-");
        string bank = TestFolders.Create("updates-bank-");
        string till = TestFolders.Create("updates-till-");
        MainWindow window = Started(copy.Updater());
        MainWindow? frontWindow = null;
        MainWindow? laterWindow = null;
        try
        {
            window.OpenFolder(shop);
            window.ApplySettings(window.Settings with { OpenProjectIn = Passenger.App.Settings.OpenProjectIn.NewWindow });
            // Another solution reopened as Passenger starts, in a window of its own in front of the one that watches.
            Assert.Null(window.Open(bank));
            Ui.Settle();
            MainWindow front = frontWindow = Assert.Single(PassengerApp.Windows, w => w != window);
            window.WatchForUpdates();

            Ui.PumpUntil(() => Notice(front) is { } shown && shown.StartsWith($"Downloading Passenger {newer}: ", StringComparison.Ordinal),
                $"the window in front, not the one watching, to say v{newer} is downloading");
            github.Let();
            Ui.WaitFor(() => Notice(front) == $"Restart to update to Passenger {newer}",
                () => $"the window in front to offer Restart to Update once v{newer} had downloaded, where {UpdatesSaid(front)}");
            Assert.True(Notice(window) == Notice(front), $"The window watching says '{Notice(window)}', the one in front '{Notice(front)}'");
            Assert.True(front.Actions.WhyNot(AppActions.RestartToUpdate, front.Actions.Context()) is null,
                "The window in front shows the update ready, but its Restart to Update cannot run");
            Assert.True(front.UpdateReady?.Version == newer, $"The window in front has {front.UpdateReady?.Version ?? "nothing"} ready, not {newer}");

            Assert.Null(window.Open(till));
            Ui.Settle();
            MainWindow later = laterWindow = Assert.Single(PassengerApp.Windows, w => w != window && w != front);
            Assert.True(Notice(later) == $"Restart to update to Passenger {newer}",
                $"A window opened after v{newer} was made ready does not offer Restart to Update: {UpdatesSaid(later)}");
            Assert.True(later.Actions.WhyNot(AppActions.RestartToUpdate, later.Actions.Context()) is null,
                "A window opened after the update was made ready cannot run Restart to Update");
            Assert.True(github.Requests.Count == update.Count + 1,
                $"Three windows made {github.Requests.Count} requests, not one for the release and one for each of the {update.Count} files of this system's update");

            later.Perform(AppActions.CheckForUpdates);
            Ui.WaitFor(() => later.Status == $"Passenger {newer} is ready: restart to update",
                () => $"Check for Updates in the window opened later to say there that v{newer} is ready, where {UpdatesSaid(later)}");
            Assert.True(github.Requests.Count == update.Count + 2,
                $"Check for Updates in a window that does not watch made {github.Requests.Count - update.Count - 1} requests, not one for the release alone");
            Assert.True(github.RequestsFor(copy.Package(newer)).Length == 1, $"{copy.Package(newer)} was downloaded again for another window");
        }
        finally
        {
            laterWindow?.Close();
            frontWindow?.Close();
            window.Close();
            TestFolders.Delete(shop);
            TestFolders.Delete(bank);
            TestFolders.Delete(till);
        }
    }

    private void A_download_that_does_not_match_the_SHA_256_its_release_gives_is_refused_and_the_person_told_why()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        // The package changed after its checksum was taken, as a corrupted download or a tampered release brings it.
        Dictionary<string, byte[]> update = new(copy.Update(newer));
        byte[] tampered = [.. update[copy.Package(newer)]];
        tampered[tampered.Length / 2] ^= 0xFF;
        update[copy.Package(newer)] = tampered;
        github.Answer = Release($"v{newer}", files: update);
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();

        Ui.WaitFor(() => Notice(window) == $"Passenger {newer} not installed", $"the notice to say v{newer} was not installed once its download was refused");
        string said = window.Status ?? string.Empty;
        Assert.True(said.StartsWith($"Passenger {newer} was not installed: {copy.Package(newer)}", StringComparison.Ordinal) && said.Contains("SHA", StringComparison.Ordinal),
            $"A package that does not match its SHA-256 was refused without the person being told why: the status bar says '{said}'");
        Assert.Contains(said, ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty);
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: {said}", StringComparison.Ordinal));
        string page = $"https://github.com/Zaneris/passenger/releases/tag/v{newer}";
        Assert.True(window.UpdateShown?.Link.AbsoluteUri == page, $"After a refusal the notice opens {window.UpdateShown?.Link}, not the release's page, {page}");

        Assert.Null(window.UpdateReady);
        Assert.False(copy.Holds(newer), $"The refused v{newer} is still there to install");
        Assert.True(copy.Downloaded().Count == 0, $"The refused download left {string.Join(", ", copy.Downloaded())} behind");
        Assert.Equal("No update is downloaded and ready to install", window.Actions.WhyNot(AppActions.RestartToUpdate, window.Actions.Context()));
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion is null || copy.InstalledVersion == Product.Version, $"The refused update was installed, as {copy.InstalledVersion}");
    }

    /// <summary>The Mac refusing the disk image just downloaded for a moment is waited out: hdiutil refuses one another
    /// program (a scanner) holds ("Resource temporarily unavailable"), as an exclusive lock taken on it here does, and
    /// diskutil image, which opens a held one, one it cannot read. The image, whose SHA-256 is the release's, is opened
    /// again once let go; one never let go is refused in the tool's words, the log naming what had it open, and one macOS
    /// refused access to also names the folder it was refused in and where else to get it.</summary>
    [SupportedOSPlatform("macos")]
    private void A_disk_image_the_Mac_refuses_as_it_lands_opens_once_let_go_and_one_never_let_go_is_refused_in_the_tools_words(DiskImageTool tool, string refused,
        string why)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Disk images are macOS's");
        Assert.SkipWhen(tool == DiskImageTool.Diskutil && !OperatingSystem.IsMacOSVersionAtLeast(26), "diskutil image is macOS 26's");
        using IDisposable theme = TestsTheme();
        ConcurrentQueue<int> tries = new();
        Held held = new(tool);
        try
        {
            AppBundle copy = new(images, TestFolders.Create("installed-")) { Images = tool };
            string newer = Bumped(1, 0);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            DiskImageUpdater lettingGo = copy.Updater((image, attempt) =>
            {
                tries.Enqueue(attempt);
                if (attempt == 1)
                {
                    held.Hold(image);
                }
                else
                {
                    held.Let();
                }
                return Task.CompletedTask;
            }, [TimeSpan.FromMilliseconds(200)]);
            MainWindow window = Started(lettingGo);
            window.WatchForUpdates();
            Ui.WaitFor(() => Settled(window, newer), () => $"the update refused as it landed to be made ready or refused, where {UpdatesSaid(window)}");
            Assert.True(Notice(window) == $"Restart to update to Passenger {newer}",
                $"A disk image the Mac refused a moment after it landed was not opened once let go: {UpdatesSaid(window)}");
            Assert.True(tries.SequenceEqual([1, 2]), $"The image was tried {string.Join(", ", tries)}, not once refused and once let go");
            Assert.True(copy.Holds(newer), $"The status bar offers Restart to Update, but v{newer} is not where it installs from");
            string[] log = Ui.IdeLog().Split('\n');
            Assert.Contains(log, line => line.Contains($"Updates: {copy.Package(newer)} did not open as a disk image at try 1 ({refused})", StringComparison.Ordinal));
            Assert.Contains(log, line => line.Contains($"Updates: {copy.Package(newer)} opened as a disk image at try 2", StringComparison.Ordinal));
            window.Close();

            AppBundle other = new(images, TestFolders.Create("installed-")) { Images = tool };
            string later = Bumped(1, 1);
            github.Answer = Release($"v{later}", files: other.Update(later));
            tries.Clear();
            DiskImageUpdater neverLettingGo = other.Updater((image, attempt) =>
            {
                tries.Enqueue(attempt);
                held.Hold(image);
                return Task.CompletedTask;
            }, [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)]);
            window = Started(neverLettingGo);
            window.WatchForUpdates();
            Ui.WaitFor(() => Settled(window, later), () => $"the update refused throughout to be refused, where {UpdatesSaid(window)}");
            string said = string.Format(System.Globalization.CultureInfo.InvariantCulture, why, refused, Path.Combine(other.Opened, later));
            string words = $"Passenger {later} was not installed: {other.Package(later)} does not open as a disk image: {said}";
            Assert.True(window.Status == words, $"An image the Mac never opened was not said in the tool's words: the status bar says '{window.Status}', not '{words}'");
            Assert.Contains(words, ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty);
            Assert.True(tries.SequenceEqual([1, 2, 3]), $"The image refused throughout was tried {string.Join(", ", tries)}, not three times");
            Assert.True(other.Downloaded().Count == 0, $"The image refused left {string.Join(", ", other.Downloaded())} behind");
            string holder = tool == DiskImageTool.Hdiutil ? $"(process {Environment.ProcessId}) on " : "nothing this user runs had it open";
            string[] refusals = [.. Ui.IdeLog().Split('\n').Where(line => line.Contains($"Updates: {words} (", StringComparison.Ordinal))];
            Assert.True(refusals.Any(line => line.Contains(holder, StringComparison.Ordinal)),
                $"The log does not say what held the image refused throughout ('{holder}'): {string.Join("\n", refusals)}");
            window.Close();
        }
        finally
        {
            held.Let();
        }

        static bool Settled(MainWindow window, string version) =>
            Notice(window) == $"Restart to update to Passenger {version}" || Notice(window) == $"Passenger {version} not installed";
    }

    /// <summary>A disk image whose disk another program holds open as the update lets it go (a scanner, a backup agent):
    /// its volume is unmounted, but its disk stays attached while held, which no detach takes, forced or not. The update
    /// tries again by the disk until hdiutil lists the image no more, saying what held it; held throughout, the app copied
    /// out is made ready all the same, the log naming what held the disk, and once let go the same release downloaded
    /// again opens and every attachment of it is let go.</summary>
    [SupportedOSPlatform("macos")]
    private void A_disk_image_whose_disk_is_held_as_it_is_let_go_goes_once_let_go_and_one_held_throughout_is_said_and_opens_again(DiskImageTool tool)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Disk images are macOS's");
        Assert.SkipWhen(tool == DiskImageTool.Diskutil && !OperatingSystem.IsMacOSVersionAtLeast(26), "diskutil image is macOS 26's");
        using IDisposable theme = TestsTheme();
        DiskHold hold = new();
        ConcurrentQueue<int> tries = new();
        AppBundle copy = new(images, TestFolders.Create("installed-")) { Images = tool };
        AppBundle other = new(images, TestFolders.Create("installed-")) { Images = tool };
        try
        {
            string newer = Bumped(1, 0);
            string folder = Path.Combine(copy.Opened, newer);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            DiskImageUpdater lettingGo = copy.LettingGoUpdater((attached, attempt) =>
            {
                tries.Enqueue(attempt);
                if (attempt == 1)
                {
                    hold.Hold(attached, Path.Combine(folder, "mount"));
                }
                else if (attempt == 3)
                {
                    hold.Let();
                }
                return Task.CompletedTask;
            }, [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)]);
            MainWindow window = Started(lettingGo);
            window.WatchForUpdates();
            Ui.WaitFor(() => Settled(window, newer), () => $"the update whose disk was held as it was let go to be made ready or refused, where {UpdatesSaid(window)}");
            Assert.True(Notice(window) == $"Restart to update to Passenger {newer}", $"An update whose image's disk was held a moment was not made ready: {UpdatesSaid(window)}");
            Assert.True(tries.Take(3).SequenceEqual([1, 2, 3]),
                $"The image was let go of at tries {string.Join(", ", tries)}, not refused twice while its disk was held and let go at the third");
            IReadOnlyList<string> left = AttachedUnder(copy.Opened);
            Assert.True(left.Count == 0, $"The image whose disk was held a moment is still attached once let go, as {string.Join("; ", left)}");
            string[] said = [.. Ui.IdeLog().Split('\n').Where(line => line.Contains($"Updates: the disk image in {folder} ", StringComparison.Ordinal))];
            Assert.True(said.Any(line => line.Contains("was let go of at try ", StringComparison.Ordinal) && line.Contains($"(process {Environment.ProcessId}) on /dev/r", StringComparison.Ordinal)),
                $"The log does not say the image was let go of once its disk was, naming this process as what held it: {string.Join("\n", said)}");
            window.Close();

            string later = Bumped(1, 1);
            folder = Path.Combine(other.Opened, later);
            github.Answer = Release($"v{later}", files: other.Update(later));
            tries.Clear();
            bool holding = true;
            DiskImageUpdater neverLettingGo = other.LettingGoUpdater((attached, attempt) =>
            {
                tries.Enqueue(attempt);
                if (Volatile.Read(ref holding))
                {
                    hold.Hold(attached, Path.Combine(folder, "mount"));
                }
                return Task.CompletedTask;
            }, [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)]);
            window = Started(neverLettingGo);
            window.WatchForUpdates();
            Ui.WaitFor(() => Settled(window, later), () => $"the update whose disk was held throughout to be made ready or refused, where {UpdatesSaid(window)}");
            Assert.True(Notice(window) == $"Restart to update to Passenger {later}",
                $"An update whose app was copied out and checked was not made ready because its image's disk stayed held: {UpdatesSaid(window)}");
            Assert.True(tries.SequenceEqual([1, 2, 3, 4, 5]), $"The image held throughout was let go of at tries {string.Join(", ", tries)}, not five");
            Assert.True(AttachedUnder(other.Opened).Count > 0, "The image whose disk was held throughout is not attached, so the test held nothing");
            said = [.. Ui.IdeLog().Split('\n').Where(line => line.Contains($"Updates: the disk image in {folder} ", StringComparison.Ordinal))];
            Assert.True(said.Any(line => line.Contains("is still attached after 5 tries to let it go", StringComparison.Ordinal)
                    && line.Contains($"(process {Environment.ProcessId}) on /dev/r", StringComparison.Ordinal)),
                $"The log does not say the image held throughout is still attached, naming this process as what held it: {string.Join("\n", said)}");

            // Dropped while still held, then let go: the same release is downloaded to the same file again and opened.
            window.ApplySettings(window.Settings with { CheckForUpdates = false });
            Ui.WaitFor(() => !other.Holds(later) && Notice(window) == $"Passenger {later} available", () => $"the update made ready to be dropped as Check for updates went off, where {UpdatesSaid(window)}");
            Volatile.Write(ref holding, false);
            hold.Let();
            window.ApplySettings(window.Settings with { CheckForUpdates = true });
            Ui.WaitFor(() => Settled(window, later), () => $"the same release downloaded again to be made ready or refused, where {UpdatesSaid(window)}");
            Assert.True(Notice(window) == $"Restart to update to Passenger {later}", $"Once its disk was let go, the same release's image did not open again: {UpdatesSaid(window)}");
            left = AttachedUnder(other.Opened);
            Assert.True(left.Count == 0, $"The same release opened again left its images attached, as {string.Join("; ", left)}");
            window.Close();
        }
        finally
        {
            hold.Let();
            TestVolumes.DetachUnder(copy.Opened);
            TestVolumes.DetachUnder(other.Opened);
        }

        static bool Settled(MainWindow window, string version) =>
            Notice(window) == $"Restart to update to Passenger {version}" || Notice(window) == $"Passenger {version} not installed";
    }

    /// <summary>A disk held open as another program may hold one (a scanner, a backup agent): its raw device, read.</summary>
    [SupportedOSPlatform("macos")]
    private sealed class DiskHold
    {
        private SafeFileHandle? held;

        /// <summary>Holds the disk of the attachment whose volume is at <paramref name="mount"/>, the update's own.</summary>
        public void Hold(IReadOnlyList<AttachedImages.Attachment> attached, string mount)
        {
            string at = PathNames.Canonical(mount);
            if (Volatile.Read(ref held) is null && attached.FirstOrDefault(a => a.Mounts.Any(m => PathNames.Same(m, at))) is { } ours)
            {
                Volatile.Write(ref held, File.OpenHandle($"/dev/r{Path.GetFileName(ours.Disk)}", FileMode.Open, FileAccess.Read));
            }
        }

        public void Let() => Interlocked.Exchange(ref held, null)?.Dispose();
    }

    /// <summary>The attachments hdiutil lists of an image under a folder, each as its disk and image.</summary>
    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<string> AttachedUnder(string folder)
    {
        string under = PathNames.Canonical(folder);
        (IReadOnlyList<AttachedImages.Attachment>? listed, string why) = Ui.AwaitPumped(Waiting.Apart(() => AttachedImages.ListAsync(CancellationToken.None)),
            "hdiutil's list of the images attached", TimeSpan.FromMinutes(1).Scaled());
        Assert.True(listed is not null, why);
        return [.. listed.Where(a => PathNames.IsUnder(PathNames.Canonical(a.Image), under)).Select(a => $"{a.Disk} of {a.Image}")];
    }

    /// <summary>A disk image the Mac refuses as hdiutil or diskutil image would find it: held by another program for
    /// hdiutil, an exclusive lock taken on it; unreadable for diskutil image, which opens a held one.</summary>
    [SupportedOSPlatform("macos")]
    private sealed class Held(DiskImageTool tool)
    {
        private FileStream? held;
        private string? unread;

        public void Hold(string image)
        {
            if (tool == DiskImageTool.Hdiutil)
            {
                held ??= new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(image, UnixFileMode.None);
                unread = image;
            }
        }

        public void Let()
        {
            Interlocked.Exchange(ref held, null)?.Dispose();
            if (Interlocked.Exchange(ref unread, null) is { } image && File.Exists(image))
            {
                File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private void With_Check_for_updates_off_nothing_is_downloaded_and_turning_it_off_drops_a_download_made()
    {
        using IDisposable theme = TestsTheme();
        InstalledCopy copy = InstalledCopy.Make(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        IReadOnlyDictionary<string, byte[]> update = copy.Update(newer);
        github.Answer = Release($"v{newer}", files: update);
        MainWindow window = Started(copy.Updater());
        window.ApplySettings(window.Settings with { CheckForUpdates = false });
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 0, $"With Check for updates off, the window asked GitHub {github.Requests.Count} time(s)");

        CheckForUpdates(window);
        Assert.True(Notice(window) == $"Passenger {newer} available", $"Check for Updates with the setting off did not show the newer release: {UpdatesSaid(window)}");
        Ui.Settle();
        string[] downloaded = [.. update.Keys.Where(name => github.RequestsFor(name).Length > 0)];
        Assert.True(downloaded.Length == 0, $"With Check for updates off, the window downloaded {string.Join(", ", downloaded)}");
        Assert.False(copy.Holds(newer), $"With Check for updates off, v{newer} is ready to install");

        window.ApplySettings(window.Settings with { CheckForUpdates = true });
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "Check for updates turned on to download the newer release");
        window.ApplySettings(window.Settings with { CheckForUpdates = false });
        Ui.WaitFor(() => !copy.Holds(newer) && copy.Downloaded().Count == 0, "the update downloaded to be dropped as Check for updates went off");
        Assert.True(Notice(window) == $"Passenger {newer} available", $"With the download dropped, {UpdatesSaid(window)}");
        Assert.Null(window.UpdateReady);
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion is null || copy.InstalledVersion == Product.Version,
            $"An update dropped as the setting went off was installed, as {copy.InstalledVersion}");
    }

    private void A_copy_its_installer_or_disk_image_did_not_put_in_place_never_downloads_an_update()
    {
        using IDisposable theme = TestsTheme();
        // The tests' own Passenger, a build from source as `just run` and a Debug build are, is one.
        Assert.True(Updater.OfThisCopy(settings, out string why) is null, "The tests' Passenger, a build from source, would update itself");
        Assert.False(string.IsNullOrWhiteSpace(why), "A copy that cannot update itself does not say why");

        string newer = Bumped(1, 0);
        Dictionary<string, byte[]> files = new()
        {
            [ThisSystemsDownload()] = [1, 2, 3],
            ["SHA256SUMS"] = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData((byte[])[1, 2, 3]))}  {ThisSystemsDownload()}\n"),
        };
        github.Answer = Release($"v{newer}", files: files);
        // As every window but the person's own: a test's, a screenshot's, an agent's.
        MainWindow window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {newer} available", $"A copy that cannot update itself does not show the newer release's link: {UpdatesSaid(window)}");
        Assert.True(github.Requests.Count == 1, $"A copy that cannot update itself made {github.Requests.Count} requests, not only the one for the latest release");
        CheckForUpdates(window);
        Assert.True(github.Requests.Count == 2 && files.Keys.All(name => github.RequestsFor(name).Length == 0),
            "Check for Updates had a copy that cannot update itself download the release");
        Assert.Equal("No update is downloaded and ready to install", window.Actions.WhyNot(AppActions.RestartToUpdate, window.Actions.Context()));
        window.Close();
    }

    /// <summary>The disk image is opened and let go of by diskutil image where macOS has it, hdiutil before; each puts the
    /// update in place.</summary>
    [SupportedOSPlatform("macos")]
    private void Restart_to_Update_puts_the_new_version_in_place_of_the_old_and_starts_it_and_closing_installs_it_too(DiskImageTool tool)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        Assert.SkipWhen(tool == DiskImageTool.Diskutil && !OperatingSystem.IsMacOSVersionAtLeast(26), "diskutil image is macOS 26's");
        using IDisposable theme = TestsTheme();
        AppBundle copy = new(images, TestFolders.Create("installed-")) { Images = tool };
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");

        Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
        Ui.Settle();
        Assert.False(window.IsVisible, "Restart to Update did not close the window");
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion == newer, $"Restart to Update left {copy.Bundle} at {copy.InstalledVersion}, not {newer}");
        Assert.True(copy.SignatureHolds(), $"The app put in place at {copy.Bundle} is not whole: its signature does not hold");
        Assert.True(Ui.Shared(copy.Started) == $"started {newer}\n", $"Restart to Update did not start the new version once: what started says '{Ui.Shared(copy.Started)}'");
        string[] beside = [.. Directory.EnumerateFileSystemEntries(copy.Applications).Select(p => Path.GetFileName(p))];
        Assert.True(beside.SequenceEqual(["Passenger.app"]), $"The update left {string.Join(", ", beside)} where the app is installed");
        Assert.True(copy.Downloaded().Count == 0, $"The update installed left {string.Join(", ", copy.Downloaded())} behind");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} installed in {copy.Bundle}, and started", StringComparison.Ordinal));

        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}", files: copy.Update(later));
        window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {later}", $"the status bar to offer Restart to Update to v{later}");
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion == later, $"Passenger closed with v{later} ready left {copy.Bundle} at {copy.InstalledVersion}");
        Assert.True(Ui.Shared(copy.Started) == $"started {newer}\n", "Closing Passenger started the version it installed, which only Restart to Update does");
    }

    [SupportedOSPlatform("macos")]
    private void An_update_that_could_not_take_the_apps_place_is_said_at_the_next_start_and_is_still_ready()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        using IDisposable theme = TestsTheme();
        AppBundle copy = new(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");

        // The folder the app is in turned read-only meanwhile: the app cannot be replaced, and stays as it was.
        UnixFileMode was = File.GetUnixFileMode(copy.Applications);
        File.SetUnixFileMode(copy.Applications, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            window.Close();
            Ended(window);
            window.InstallUpdate();
        }
        finally
        {
            File.SetUnixFileMode(copy.Applications, was);
        }
        Assert.True(copy.InstalledVersion == Product.Version, $"{copy.Bundle} is {copy.InstalledVersion} after an update that could not take its place");
        Assert.True(copy.SignatureHolds(), $"{copy.Bundle} is not whole after an update that could not take its place");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} was not installed in {copy.Bundle}: Permission denied", StringComparison.Ordinal));

        window = Started(copy.Updater());
        window.WatchForUpdates();
        Assert.Equal($"Passenger {newer} did not install: Permission denied", window.Status);
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update that did not install to be offered again");
        Assert.True(github.RequestsFor(copy.Package(newer)).Length == 1, $"{copy.Package(newer)} was downloaded again");
        window.Close();
    }

    [SupportedOSPlatform("macos")]
    private void Another_Passenger_open_from_the_same_app_holds_the_update_back_until_it_closes_and_the_next_start_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        using IDisposable theme = TestsTheme();
        AppBundle copy = new(images, TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");

        // Another Passenger runs from the same app, as `open -n` starts one: a program at its executable's path.
        string executable = Path.Combine(copy.Bundle, "Contents", "MacOS", "Passenger");
        File.Copy("/bin/sleep", executable, overwrite: true);
        // Signed again, as Make signs the app: a bundle whose seal no longer holds starts with macOS's "damaged" dialog.
        Tool("/usr/bin/codesign", "--force", "--options", "runtime", "--sign", "-", copy.Bundle);
        Assert.True(copy.SignatureHolds(), $"{copy.Bundle}'s signature does not hold once its executable was replaced and signed again");
        using System.Diagnostics.Process other = ChildProcesses.Start(ChildProcesses.Command(executable, ["120"]));
        try
        {
            string? before = window.Status;
            Ui.Click(window.FindControl<Button>("UpdateButton")!);
            Assert.True(window.IsVisible, "Restart to Update closed the window while another Passenger runs from the same app");
            Assert.True(Notice(window) == $"Passenger {newer} waits for 1 program to close",
                $"With another Passenger running from the app, Restart to Update does not say it waits for it: {UpdatesSaid(window)}");
            string tip = ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty;
            Assert.True(tip.Contains($"Passenger {newer} cannot be installed while this runs from {copy.Bundle}:", StringComparison.Ordinal)
                && tip.Contains($"process {other.Id}: ", StringComparison.Ordinal) && tip.Contains("Passenger 120", StringComparison.Ordinal)
                && tip.Contains("Quit it", StringComparison.Ordinal),
                $"The notice does not name what holds the update back in full, with what to do: '{tip}'");
            Assert.True(window.Status == before, $"What holds the update back was said in the status bar's cut-off message: '{window.Status}'");

            window.Close();
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == Product.Version, $"The app another Passenger runs from was replaced by {copy.InstalledVersion}");
            Assert.True(copy.Holds(newer), $"v{newer} is no longer ready once Passenger closed without installing it");
            Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} waits to install: another Passenger runs from {copy.Bundle}", StringComparison.Ordinal));

            window = Started(copy.Updater());
            window.WatchForUpdates();
            Assert.True(window.Status?.StartsWith($"Passenger {newer} did not install: another Passenger ran from {copy.Bundle} (process {other.Id}: ", StringComparison.Ordinal) == true
                && window.Status.Contains("Passenger 120", StringComparison.Ordinal),
                $"The next start does not say the Passenger running from the same app held the update back: '{window.Status}'");
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update held back to be offered again");
            window.Close();
        }
        finally
        {
            ChildProcesses.Kill(other);
        }
    }

    /// <summary>On Windows a Passenger running from the install's current\ holds the update back as Passenger closes, as
    /// Update.exe would find its files in use: the update stays ready, and the next start names what held it back.</summary>
    [SupportedOSPlatform("windows")]
    private void On_Windows_another_Passenger_running_from_the_install_holds_the_update_back_and_the_next_start_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Velopack's install is Windows'");
        using IDisposable theme = TestsTheme();
        VelopackInstall copy = new(TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        Updater updater = copy.Updater();
        MainWindow window = Started(updater);
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");

        // Another Passenger runs from current\: a program at its executable's path there, waiting.
        string executable = Path.Combine(copy.Current, "Passenger.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), executable);
        using System.Diagnostics.Process other = ChildProcesses.Start(ChildProcesses.Command(executable, ["-n", "600", "127.0.0.1"]));
        try
        {
            Assert.True(updater.Others().Any(running => running.ProcessId == other.Id),
                $"The program running from {executable} (process {other.Id}) is not found running from the install");
            window.Close();
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.Holds(newer), $"v{newer} is no longer ready once Passenger closed without installing it");
            Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} waits to install: another Passenger runs from {copy.Current}", StringComparison.Ordinal));

            window = Started(copy.Updater());
            window.WatchForUpdates();
            Assert.True(window.Status?.StartsWith($"Passenger {newer} did not install: another Passenger ran from {copy.Current} (process {other.Id}: ", StringComparison.Ordinal) == true
                && window.Status.Contains("Passenger.exe", StringComparison.OrdinalIgnoreCase),
                $"The next start does not say the Passenger running from the install held the update back: '{window.Status}'");
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update held back to be offered again");
            window.Close();
        }
        finally
        {
            ChildProcesses.Kill(other);
        }
    }

    /// <summary>On Windows an Update.exe that does not start as Passenger closes (the test's install has none) leaves the
    /// install as it was and the update ready, and the next start says why.</summary>
    [SupportedOSPlatform("windows")]
    private void On_Windows_an_Update_exe_that_does_not_start_leaves_the_update_ready_and_the_next_start_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Velopack's install is Windows'");
        using IDisposable theme = TestsTheme();
        VelopackInstall copy = new(TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.Holds(newer), $"v{newer} is no longer ready once Update.exe did not start");
        string started = $"Updates: Passenger {newer} was not installed: Update.exe did not start: ";
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains(started, StringComparison.Ordinal));

        window = Started(copy.Updater());
        window.WatchForUpdates();
        Assert.True(window.Status?.StartsWith($"Passenger {newer} did not install: Update.exe did not start: ", StringComparison.Ordinal) == true,
            $"The next start does not say Update.exe did not start: '{window.Status}'");
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update that did not install to be offered again");
        window.Close();
    }

    /// <summary>Passenger and its state on a volume of their own, as a home folder moved to an external drive takes them,
    /// where macOS's disk image tools may refuse to open an image or mount one: the release's disk image is downloaded to
    /// and opened in a folder of the user's alone on the startup disk, its app copied out into the state, and nothing of
    /// the image is left once the update is ready.</summary>
    [SupportedOSPlatform("macos")]
    private void An_update_whose_state_is_on_another_volume_opens_its_disk_image_on_the_startup_disk_in_a_folder_of_the_users_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Disk images are macOS's");
        using IDisposable theme = TestsTheme();
        string folder = TestFolders.Create("installed-");
        (string volume, string disk) = HfsVolume(folder);
        AppBundle copy = new(images, folder, volume, Path.Combine(volume, "Library", "Application Support", "Passenger")) { ImageFolder = null };
        string opened = copy.Opened;
        try
        {
            string newer = Bumped(1, 0);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            string? image = null;
            UnixFileMode? mode = null;
            ConcurrentQueue<string> mounts = new();
            MainWindow window = Started(copy.Updater(opening: (path, _) =>
            {
                image = path;
                mode = File.GetUnixFileMode(opened);
                return Task.CompletedTask;
            }, lettingGo: (attached, _) =>
            {
                foreach (string mount in attached.SelectMany(a => a.Mounts))
                {
                    mounts.Enqueue(mount);
                }
                return Task.CompletedTask;
            }));
            window.WatchForUpdates();
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}" || Notice(window) == $"Passenger {newer} not installed",
                () => $"the update whose state is on another volume to be made ready or refused, where {UpdatesSaid(window)}");
            Assert.True(Notice(window) == $"Restart to update to Passenger {newer}", $"An update whose state is on another volume was not made ready: {UpdatesSaid(window)}");
            string temporary = PathNames.Canonical(UserTemporary.Folder());
            Assert.True(image is not null && PathNames.IsUnder(PathNames.Canonical(image), temporary),
                $"The disk image was opened at {image}, not in the user's temporary folder on the startup disk, {temporary}");
            Assert.True(mounts.Count > 0 && mounts.All(mount => PathNames.IsUnder(PathNames.Canonical(mount), temporary)),
                $"The disk image was mounted at {string.Join(", ", mounts)}, not in the user's temporary folder on the startup disk, {temporary}");
            Assert.True(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                $"The folder the disk image was opened in, {opened}, is {mode}, not the user's alone");
            Assert.True(copy.Holds(newer), $"The status bar offers Restart to Update, but v{newer} is not in the state on the other volume, {copy.Staging}");
            Assert.False(Directory.Exists(opened), $"The update made ready left the folder its disk image was opened in, {opened}");
            window.Close();
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == newer, $"Passenger on another volume with its state closed with v{newer} ready, and is {copy.InstalledVersion}");
            Assert.True(copy.Downloaded().Count == 0, $"The update installed left {string.Join(", ", copy.Downloaded())} behind");
        }
        finally
        {
            TestVolumes.DetachUnder(opened);
            TestFolders.Delete(opened);
            TestVolumes.Detach(volume, disk);
        }
    }

    /// <summary>On a volume that cannot swap two folders (HFS+) the new app takes the installed one's place in two renames.
    /// Another program taking that place between them leaves neither app there: the install throws nothing, keeps both
    /// whole, and says where each was left, which the next start says too.</summary>
    [SupportedOSPlatform("macos")]
    private void An_app_whose_place_is_taken_between_its_two_renames_says_where_each_app_was_left()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "A volume that cannot swap two folders is a Mac's HFS+ disk image here");
        using IDisposable theme = TestsTheme();
        string folder = TestFolders.Create("installed-");
        (string volume, string disk) = HfsVolume(folder);
        try
        {
            AppBundle copy = new(images, folder, volume);
            string newer = Bumped(1, 0);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            // Something comes to the app's name just as the new app is to take it, and stays.
            MainWindow window = Started(copy.Updater(renaming: (_, to) =>
            {
                if (to == copy.Bundle && !File.Exists(copy.Bundle))
                {
                    File.WriteAllText(copy.Bundle, "another program's");
                }
            }));
            window.WatchForUpdates();
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");
            window.Close();
            Ended(window);
            window.InstallUpdate();

            string old = Path.Combine(volume, $".Passenger.app.{newer}.old");
            string beside = Path.Combine(volume, $".Passenger.app.{newer}");
            string notInstalled = $"Updates: Passenger {newer} was not installed in {copy.Bundle}: ";
            string? said = Ui.IdeLog().Split('\n').LastOrDefault(line => line.Contains(notInstalled, StringComparison.Ordinal));
            Assert.True(said is not null, $"An install whose app's place was taken did not say it was not installed: {UpdatesSaid(window)}");
            string why = said[(said.IndexOf(notInstalled, StringComparison.Ordinal) + notInstalled.Length)..].TrimEnd();
            Assert.True(why.Contains("could not be put back", StringComparison.Ordinal) && why.Contains(old, StringComparison.Ordinal)
                && why.Contains(beside, StringComparison.Ordinal), $"An install that could put neither app in place does not say where each was left: '{why}'");
            Assert.True(AppBundle.Version(old) == Product.Version, $"The installed app is not whole where the install says it left it, {old}");
            Assert.True(AppBundle.Version(beside) == newer, $"Passenger {newer} is not whole where the install says it left it, {beside}");

            // The person puts the app back where the words say it is.
            File.Delete(copy.Bundle);
            Directory.Move(old, copy.Bundle);
            Assert.True(copy.SignatureHolds(), $"The app the install left in {old} is not whole once put back");
            window = Started(copy.Updater());
            window.WatchForUpdates();
            Assert.Equal($"Passenger {newer} did not install: {why}", window.Status);
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update that did not install to be offered again");
            window.Close();
        }
        finally
        {
            TestVolumes.Detach(volume, disk);
        }
    }

    /// <summary>A volume of the test's own in <paramref name="folder"/>, as an external drive would be: HFS+, which cannot
    /// swap two folders, and unindexed. <see cref="TestVolumes.Detach"/> takes it off.</summary>
    [SupportedOSPlatform("macos")]
    private static (string Volume, string Disk) HfsVolume(string folder)
    {
        string volume = Directory.CreateDirectory(Path.Combine(folder, "volume")).FullName;
        string disk = Path.Combine(folder, "volume.dmg");
        Tool("/usr/bin/hdiutil", "create", "-size", "20m", "-fs", "HFS+", "-volname", "Passenger Tests", "-ov", disk, "-quiet");
        Tool("/usr/bin/hdiutil", "attach", disk, "-nobrowse", "-noautoopen", "-mountpoint", volume, "-quiet");
        // Spotlight indexes a volume whatever folder it is mounted in, unless it holds this marker (TestRun.Unindexed's why).
        File.WriteAllText(Path.Combine(volume, ".metadata_never_index"), string.Empty);
        return (volume, disk);
    }

    [SupportedOSPlatform("macos")]
    private void An_agents_relay_follows_the_update_in_its_own_process_its_calls_meanwhile_and_after_answered_over_the_same_connection()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        using IDisposable theme = TestsTheme();
        AppBundle copy = new(images, TestFolders.Create("installed-"));
        copy.Relaying();
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        string joining = PathNames.Canonical(Plain("relay-joined-"));
        string idle = PathNames.Canonical(Plain("relay-idle-"));
        MainWindow agents = AgentsWindow(joining, languageServer: false);
        // One agent, on the latest protocol, has called a tool; the other, on one with an initialize handshake, has called none.
        Process joinedRelay = StartPassenger(["--mcp-attach", joining], executable: copy.Executable);
        McpClient joined = Await(Waiting.Apart(() => Connect(joinedRelay, ["--mcp-attach", joining])), "the first agent's session starting");
        Unasked(agents, joined, McpTools.Workspace);
        Process idleRelay = StartPassenger(["--mcp-attach", idle], executable: copy.Executable);
        McpClient waiting = Await(Waiting.Apart(() => Connect(idleRelay, ["--mcp-attach", idle], protocol: "2025-06-18")), "the second agent's session starting");
        int relisted = 0;
        foreach (McpClient agent in (ReadOnlySpan<McpClient>)[joined, waiting])
        {
            agent.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            {
                Interlocked.Increment(ref relisted);
                return ValueTask.CompletedTask;
            });
        }

        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");
        // The notice says, as the pointer comes to it, that the agents' connections go on.
        Button notice = window.FindControl<Button>("UpdateButton")!;
        window.MouseMove(Avalonia.VisualExtensions.TranslatePoint(notice, new Avalonia.Point(notice.Bounds.Width / 2, notice.Bounds.Height / 2), window)!.Value, Avalonia.Input.RawInputModifiers.None);
        Ui.Settle();
        string tip = ToolTip.GetTip(notice) as string ?? string.Empty;
        Assert.True(tip.Contains("2 agents' connections to Passenger, on ", StringComparison.Ordinal) && tip.Contains("go on into the new version", StringComparison.Ordinal)
            && tip.Contains(joining, StringComparison.Ordinal) && tip.Contains(idle, StringComparison.Ordinal),
            $"Before Restart to Update, the notice does not say the two agents' connections, on {joining} and {idle}, go on into the new version: '{tip}'");

        Ui.Click(notice, settle: false);
        Ui.Settle();
        Assert.False(window.IsVisible, "Restart to Update did not close the window with only agents' relays running from the app");
        Assert.False(agents.IsVisible, "Restart to Update left the agents' window open");
        Ended(window);
        // A call while Passenger restarts finds no window on its folder, and waits for the new version's, starting none.
        Heard told = new();
        Task<CallToolResult> meanwhile = Waiting.Apart(() => Result(joined, McpTools.Workspace, heard: told));
        WaitFor(() => told.Told.Any(said => said.Message?.Contains("Waiting for Passenger to restart into its update", StringComparison.Ordinal) == true),
            $"the agent's call made while Passenger restarts to wait for it (told {told})");
        // The new version's windows, as its start opens them again.
        MainWindow restarted = AgentsWindow(joining, languageServer: false);
        MainWindow restartedIdle = AgentsWindow(idle, languageServer: false);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion == newer, $"Restart to Update with agents' relays running left {copy.Bundle} at {copy.InstalledVersion}, not {newer}");
        Assert.True(Ui.Shared(copy.Started) == $"started {newer}\n", $"Restart to Update did not start the new version once: what started says '{Ui.Shared(copy.Started)}'");

        CallToolResult answered = Await(meanwhile, "the call made while Passenger restarted");
        Assert.True(answered.IsError != true && Text(answered).Contains(joining, StringComparison.Ordinal),
            $"The agent's call made while Passenger restarted was answered '{Text(answered)}', not by the window on {joining}");
        Assert.True(DotnetProcesses.Runs(joinedRelay) && DotnetProcesses.Runs(idleRelay),
            $"An agent's relay ended with the update rather than following it: joined {(!DotnetProcesses.Runs(joinedRelay) ? "ended" : "runs")}, idle {(!DotnetProcesses.Runs(idleRelay) ? "ended" : "runs")}");
        // The updated app's executable started in each relay's own process, as each one's agent made its next call.
        Unasked(restarted, joined, McpTools.Workspace);
        Unasked(restartedIdle, waiting, McpTools.Workspace);
        foreach (Process relay in (ReadOnlySpan<Process>)[joinedRelay, idleRelay])
        {
            Assert.True(Ui.Shared(copy.Relayed).Split('\n').Contains($"relayed {newer} {relay.Id}"),
                $"The relay {relay.Id} did not start again from Passenger {newer} in its own process: the new version's relays say '{Ui.Shared(copy.Relayed)}'");
        }
        string[] log = Ui.IdeLog().Split('\n');
        foreach ((string folder, Process relay) in (ReadOnlySpan<(string, Process)>)[(joining, joinedRelay), (idle, idleRelay)])
        {
            Assert.Contains(log, line => line.Contains($"Updates: the agent's relay on {folder} (process {relay.Id}) let go of the app to install Passenger {newer}", StringComparison.Ordinal));
        }
        Assert.True(relisted == 0, $"The tools are the same in the new version, yet an agent was told {relisted} times that their list changed");
        Assert.True(!Errors().Any(line => line.StartsWith("passenger:", StringComparison.Ordinal)), $"A relay said it ended: {string.Join(" | ", Errors())}");
        Gone(joined);
        Gone(waiting);
        // The relay writes its log a moment after, in batches.
        WaitFor(() => Ui.IdeLog().Contains($"[{idleRelay.Id}] Agents: this relay's agent's session goes on as it was, in protocol version 2025-06-18", StringComparison.Ordinal),
            $"the relay {idleRelay.Id} from the new version to say it went on in the protocol version its agent agreed before the update, 2025-06-18; its agents' lines: "
            + string.Join(" | ", Ui.IdeLog().Split('\n').Where(line => line.Contains(" Agents: ", StringComparison.Ordinal))));
        restarted.Close();
        restartedIdle.Close();

        // Closed rather than restarted, with an agent's relay running: it follows all the same.
        copy.Relaying();
        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}", files: copy.Update(later));
        Process relayLeft = StartPassenger(["--mcp-attach", idle], executable: copy.Executable);
        McpClient left = Await(Waiting.Apart(() => Connect(relayLeft, ["--mcp-attach", idle])), "an agent's session starting again");
        window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {later}", $"the status bar to offer Restart to Update to v{later}");
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion == later, $"Passenger closed with v{later} ready and an agent's relay running left {copy.Bundle} at {copy.InstalledVersion}");
        MainWindow opened = AgentsWindow(idle, languageServer: false);
        Unasked(opened, left, McpTools.Workspace);
        Assert.True(DotnetProcesses.Runs(relayLeft) && Ui.Shared(copy.Relayed).Split('\n').Contains($"relayed {later} {relayLeft.Id}"),
            $"Closing Passenger to install the update did not have the agent's relay {relayLeft.Id} go on from Passenger {later}: '{Ui.Shared(copy.Relayed)}'");
        Gone(left);
        opened.Close();
    }

    /// <summary>Why the Linux install's tests skip elsewhere.</summary>
    private const string LinuxAlone = "A Linux install updates itself only on Linux, where its archive's app runs";

    [SupportedOSPlatform("linux")]
    private void A_Linux_install_of_ones_own_is_put_in_place_as_Passenger_restarts_its_agents_relay_following_and_its_menu_entry_refreshed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxAlone);
        using IDisposable theme = TestsTheme();
        LinuxInstall copy = new(TestFolders.Create("installed-"));
        Assert.True(File.ReadAllText(copy.Entry).Contains($"X-Passenger-Release={Product.Version}", StringComparison.Ordinal),
            $"install.sh did not make the menu entry of the release it installed: {File.ReadAllText(copy.Entry)}");
        copy.Relaying();
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        string joining = PathNames.Canonical(Plain("relay-joined-"));
        MainWindow agents = AgentsWindow(joining, languageServer: false);
        Process relay = StartPassenger(["--mcp-attach", joining], executable: copy.Executable);
        McpClient joined = Await(Waiting.Apart(() => Connect(relay, ["--mcp-attach", joining])), "the agent's session starting");
        Unasked(agents, joined, McpTools.Workspace);

        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}",
            () => $"the status bar to offer Restart to Update to v{newer}: {UpdatesSaid(window)}");
        Assert.True(copy.Holds(newer), $"Restart to Update is offered, but v{newer} is not unpacked beside {copy.Install}, where it installs from");
        Assert.True(copy.InstalledVersion == Product.Version, $"The update was put in place at once, as {copy.InstalledVersion}, before Passenger restarted");

        Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
        Ui.Settle();
        Assert.False(window.IsVisible, "Restart to Update did not close the window with only an agent's relay running from the install");
        Assert.False(agents.IsVisible, "Restart to Update left the agents' window open");
        Ended(window);
        // The new version's window, as its start opens it again.
        MainWindow restarted = AgentsWindow(joining, languageServer: false);
        window.InstallUpdate();
        string said = string.Join('\n', Ui.IdeLog().Split('\n').Where(l => l.Contains("Updates:", StringComparison.Ordinal)).TakeLast(4));
        Assert.True(copy.InstalledVersion == newer, $"Restart to Update left {copy.Install} at {copy.InstalledVersion}, not {newer}: {said}");
        Assert.True(Ui.Shared(copy.Started) == $"started {newer}\n", $"Restart to Update did not start the new version once: what started says '{Ui.Shared(copy.Started)}'");
        string entry = File.ReadAllText(copy.Entry);
        Assert.True(entry.Contains($"X-Passenger-Release={newer}", StringComparison.Ordinal) && entry.Contains($"Exec=\"{copy.Install}/passenger\" %f", StringComparison.Ordinal),
            $"The menu entry was not refreshed from v{newer}'s install.sh, naming the install's launcher: {entry}");
        Assert.True(File.ReadAllText(copy.Icon) == $"png {newer}", $"The menu's icon is still '{File.ReadAllText(copy.Icon)}', not v{newer}'s");
        Assert.True(new FileInfo(copy.Command).LinkTarget == Path.Combine(copy.Install, "passenger"),
            $"The command {copy.Command} leads to {new FileInfo(copy.Command).LinkTarget}, not the install's launcher");
        string replaced = Path.Combine(UpdateFolders.Beside(copy.Install, newer), "passenger");
        Assert.True(LinuxInstall.ReleaseOf(replaced) == Product.Version,
            $"The release replaced is not kept beside the install until the new one has started: {replaced} holds {LinuxInstall.ReleaseOf(replaced) ?? "nothing"}");

        // The agent's next call is answered by the updated app's relay, in the same process.
        Unasked(restarted, joined, McpTools.Workspace);
        Assert.True(DotnetProcesses.Runs(relay) && Ui.Shared(copy.Relayed).Split('\n').Contains($"relayed {newer} {relay.Id}"),
            $"The agent's relay {relay.Id} did not start again from Passenger {newer} in its own process: '{Ui.Shared(copy.Relayed)}'");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: the agent's relay on {joining} (process {relay.Id}) let go of the app to install Passenger {newer}", StringComparison.Ordinal));
        Assert.True(!Errors().Any(line => line.StartsWith("passenger:", StringComparison.Ordinal)), $"The relay said it ended: {string.Join(" | ", Errors())}");
        Gone(joined);
        restarted.Close();

        // The new release started, with nothing newer out: the release it replaced goes.
        github.Answer = Release($"v{Product.Version}");
        window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => !Directory.Exists(UpdateFolders.Beside(copy.Install, newer)), "the release replaced to be removed once the new one had started");
        Assert.True(copy.InstalledVersion == newer, $"The install is {copy.InstalledVersion} once the new release started, not {newer}");
        window.Close();
    }

    [SupportedOSPlatform("linux")]
    private void A_Linux_install_that_could_not_be_replaced_is_left_as_it_was_and_the_next_start_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxAlone);
        using IDisposable theme = TestsTheme();
        LinuxInstall copy = new(TestFolders.Create("installed-"));
        string newer = Bumped(1, 0);
        github.Answer = Release($"v{newer}", files: copy.Update(newer));
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");

        // The folder the install is in turned read-only meanwhile: the two folders cannot trade places.
        string opt = Path.GetDirectoryName(copy.Install)!;
        UnixFileMode was = File.GetUnixFileMode(opt);
        File.SetUnixFileMode(opt, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.SkipWhen(Libc.Access(opt, Libc.MayWrite) == 0, "The test runs as root, who writes a read-only folder all the same");
            window.Close();
            Ended(window);
            window.InstallUpdate();
        }
        finally
        {
            File.SetUnixFileMode(opt, was);
        }
        Assert.True(copy.InstalledVersion == Product.Version, $"{copy.Install} is {copy.InstalledVersion} after an update that could not take its place");
        Assert.True(File.Exists(Path.Combine(copy.Install, "app", "Passenger")), $"{copy.Install} is not whole after an update that could not take its place");
        Assert.True(copy.Holds(newer), $"v{newer} is no longer unpacked beside the install after it could not take its place");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} was not installed in {copy.Install}: Permission denied", StringComparison.Ordinal));

        window = Started(copy.Updater());
        window.WatchForUpdates();
        Assert.Equal($"Passenger {newer} did not install: Permission denied", window.Status);
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update that did not install to be offered again");
        Assert.True(github.RequestsFor(copy.Package(newer)).Length == 1, $"{copy.Package(newer)} was downloaded again");
        window.Close();
    }

    [SupportedOSPlatform("linux")]
    private void A_Linux_install_for_everyone_this_user_cannot_write_says_the_command_that_installs_it_and_a_click_copies_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxAlone);
        using IDisposable theme = TestsTheme();
        LinuxInstall copy = new(TestFolders.Create("installed-"), forEveryone: true);
        string opt = Path.GetDirectoryName(copy.Install)!;
        UnixFileMode was = File.GetUnixFileMode(opt);
        File.SetUnixFileMode(opt, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            Assert.SkipWhen(Libc.Access(opt, Libc.MayWrite) == 0, "The test runs as root, who can write any install");
            string newer = Bumped(1, 0);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            MainWindow window = Started(copy.Updater());
            window.WatchForUpdates();
            Ui.WaitFor(() => Notice(window) == $"Passenger {newer} ready to install",
                () => $"the status bar to say v{newer} is ready to install by hand: {UpdatesSaid(window)}");

            string archive = copy.Archive(newer);
            string command = $"sudo {ShellWords.Quoted(Path.Combine(copy.Install, "install.sh"), ShellKind.Posix)} --system --archive {ShellWords.Quoted(archive, ShellKind.Posix)} "
                + $"--sha256 {copy.Sum(newer)} --keep-replaced";
            Assert.True(window.UpdateByHand == command, $"The command that installs v{newer} is '{window.UpdateByHand}', not '{command}'");
            Assert.True(copy.Holds(newer), $"v{newer}'s archive is not at {archive}, which the command installs");
            string tip = ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty;
            Assert.True(tip.Contains($"this user cannot write {copy.Install}", StringComparison.Ordinal) && tip.Contains(command, StringComparison.Ordinal),
                $"The notice does not say why Passenger cannot install v{newer} and the command that does: '{tip}'");
            Assert.Equal("No update is downloaded and ready to install", window.Actions.WhyNot(AppActions.RestartToUpdate, window.Actions.Context()));

            Ui.Click(window.FindControl<Button>("UpdateButton")!);
            Ui.WaitFor(() => window.Status == $"Copied the command that installs Passenger {newer}: paste it into a terminal", "the notice's click to copy the command");
            Assert.True(Ui.Clipboard(window) == command, $"The notice's click put '{Ui.Clipboard(window)}' on the clipboard, not the command");
            Assert.True(window.Browsed.Count == 0, $"The notice's click opened {string.Join(", ", window.Browsed)}, where it copies the command");

            window.Close();
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == Product.Version, $"An install this user cannot write was changed, to {copy.InstalledVersion}");
        }
        finally
        {
            File.SetUnixFileMode(opt, was);
        }
    }

    [SupportedOSPlatform("linux")]
    private void A_Linux_install_for_everyone_is_made_ready_where_only_the_person_writes_and_install_sh_takes_only_the_archive_Passenger_checked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxAlone);
        using IDisposable theme = TestsTheme();
        LinuxInstall copy = new(TestFolders.Create("installed-"), forEveryone: true);
        string opt = Path.GetDirectoryName(copy.Install)!;
        UnixFileMode was = File.GetUnixFileMode(opt);
        File.SetUnixFileMode(opt, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string newer = Bumped(1, 0);
        try
        {
            Assert.SkipWhen(Libc.Access(opt, Libc.MayWrite) == 0, "The test runs as root, who can write any install");
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            MainWindow window = Started(copy.Updater());
            window.WatchForUpdates();
            Ui.WaitFor(() => Notice(window) == $"Passenger {newer} ready to install",
                () => $"the status bar to say v{newer} is ready to install by hand: {UpdatesSaid(window)}");
            window.Close();
            Ended(window);
        }
        finally
        {
            File.SetUnixFileMode(opt, was);
        }
        const UnixFileMode OwnersAlone = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        foreach (string folder in (string[])[copy.Staging, Path.Combine(copy.Staging, newer)])
        {
            Assert.True(File.GetUnixFileMode(folder) == OwnersAlone, $"{folder}, where v{newer} is made ready, is {File.GetUnixFileMode(folder)}: others may change it before it is installed");
        }
        Assert.False(Directory.Exists(Path.Combine(copy.Staging, newer, "passenger")), $"v{newer}'s passenger folder stays unpacked beside its archive, where install.sh never reads it");

        // The root step's check, run as the person into the test's home: the same install.sh, without --system.
        string archive = copy.Archive(newer);
        byte[] checkedBytes = File.ReadAllBytes(archive);
        string mine = Path.Combine(copy.Home, ".local", "opt", "passenger");
        File.AppendAllText(archive, "changed after Passenger checked it");
        ChildResult refused = copy.InstallArchive(archive, copy.Sum(newer));
        Assert.True(!refused.Succeeded && refused.Error.Contains($"{archive}'s SHA-256 is", StringComparison.Ordinal)
            && refused.Error.Contains($"not {copy.Sum(newer)}, the one its release gives", StringComparison.Ordinal),
            $"install.sh did not refuse an archive changed after Passenger checked it, saying its SHA-256 differs: exit {refused.ExitCode}, {refused.Error}{refused.Output}");
        Assert.False(Directory.Exists(mine), $"install.sh installed an archive changed after Passenger checked it, in {mine}");
        string[] left = Directory.Exists(Path.GetDirectoryName(mine)) ? Directory.GetFileSystemEntries(Path.GetDirectoryName(mine)!) : [];
        Assert.True(left.Length == 0, $"install.sh left what it unpacked beside {mine}: {string.Join(", ", left)}");

        File.Delete(archive);
        File.WriteAllBytes(archive, checkedBytes);
        ChildResult installed = copy.InstallArchive(archive, copy.Sum(newer));
        Assert.True(installed.Succeeded && LinuxInstall.ReleaseOf(mine) == newer,
            $"install.sh did not install the archive Passenger checked: exit {installed.ExitCode}, {installed.Error}{installed.Output}; {mine} holds {LinuxInstall.ReleaseOf(mine) ?? "nothing"}");
    }

    [SupportedOSPlatform("linux")]
    private void A_Linux_copy_a_package_manager_installed_says_its_package_manager_updates_it_and_downloads_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxAlone);
        using IDisposable theme = TestsTheme();
        LinuxInstall copy = new(TestFolders.Create("installed-"));
        // As a package says it is one, beside the app it installs.
        File.WriteAllText(Path.Combine(copy.Install, "app", LinuxUpdater.PackageMarker), "pacman\n");
        string newer = Bumped(1, 0);
        IReadOnlyDictionary<string, byte[]> update = copy.Update(newer);
        github.Answer = Release($"v{newer}", files: update);
        MainWindow window = Started(copy.Updater());
        window.WatchForUpdates();
        Ui.Settle();

        Assert.True(Notice(window) == $"Passenger {newer} available", $"A package manager's copy does not say v{newer} is out: {UpdatesSaid(window)}");
        string tip = ToolTip.GetTip(window.FindControl<Button>("UpdateButton")!) as string ?? string.Empty;
        Assert.True(tip.Contains("This copy of Passenger is a package pacman installed: the system's package manager updates it.", StringComparison.Ordinal),
            $"The notice does not say the package manager updates this copy: '{tip}'");
        string page = $"https://github.com/Zaneris/passenger/releases/tag/v{newer}";
        Assert.True(window.UpdateShown?.Link.AbsoluteUri == page, $"A package manager's copy links {window.UpdateShown?.Link}, not only the release's page, {page}");
        string[] downloaded = [.. update.Keys.Where(name => github.RequestsFor(name).Length > 0)];
        Assert.True(downloaded.Length == 0, $"A package manager's copy downloaded {string.Join(", ", downloaded)}");
        Assert.False(copy.Holds(newer), $"A package manager's copy made v{newer} ready to install");
        Assert.Equal("No update is downloaded and ready to install", window.Actions.WhyNot(AppActions.RestartToUpdate, window.Actions.Context()));
        Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
        Assert.True(window.Browsed.LastOrDefault()?.AbsoluteUri == page, $"The notice's click opened [{string.Join(", ", window.Browsed)}], not the release's page");
        window.Close();
        Ended(window);
        window.InstallUpdate();
        Assert.True(copy.InstalledVersion == Product.Version, $"A package manager's copy was changed, to {copy.InstalledVersion}");
    }

    /// <summary>The person's window as the app makes it, its settings kept in the test's state folder, asking the stand-in
    /// within the limit a person's Passenger gives GitHub, scaled to the run, or the test's own (a stand-in held to not
    /// answering); with the updater of a copy installed in a test folder, or, as every window but the person's own, none;
    /// counting a day between checks on the test's clock, looking whether one is due as often as the test says.</summary>
    private MainWindow Started(Updater? updater = null, TimeSpan? feedLimit = null, TimeProvider? clock = null, TimeSpan? look = null)
    {
        ReleaseFeed feed = new(github.Listing, feedLimit ?? ReleaseFeed.ShortTimeout.Scaled());
        MainWindow window = new()
        {
            Width = 1200, Height = 800, StateFolder = settings, Releases = feed, SelfUpdate = updater,
            UpdateClock = clock ?? TimeProvider.System, UpdateLook = look ?? TimeSpan.FromHours(1),
        };
        window.Show();
        window.KeepSettings();
        Ui.Settle();
        return window;
    }

    /// <summary>Help, Check for Updates as a person chooses it: the application menu's, after About, on macOS; elsewhere
    /// Help's, before About.</summary>
    private static void CheckForUpdates(MainWindow window)
    {
        NativeMenu help = Ui.Submenu(window.MenuBar, "Help");
        string[] helpItems = [.. help.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? string.Empty)];
        NativeMenuItem item;
        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain("Check for Updates…", helpItems);
            string[] application = [.. MainWindow.ApplicationMenu!.Items.Select(i => (i as NativeMenuItem)?.Header ?? "-")];
            Assert.Equal(["About Passenger", "Check for Updates…", "-", "Settings…"], application);
            item = MainWindow.ApplicationMenu!.Items.OfType<NativeMenuItem>().First(i => i.Header == "Check for Updates…");
        }
        else
        {
            Assert.Equal(["Check for Updates…", "About"], helpItems[^2..]);
            item = help.Items.OfType<NativeMenuItem>().First(i => i.Header == "Check for Updates…");
        }
        item.Command!.Execute(null);
        Ui.Settle();
    }

    /// <summary>The status bar's notice of a newer release, or null while it shows none.</summary>
    private static string? Notice(MainWindow window) =>
        window.FindControl<Button>("UpdateButton")!.IsVisible ? window.FindControl<TextBlock>("UpdateText")!.Text : null;

    /// <summary>What the window shows and said of updates, for a failure to name: its notice or that it shows none, never
    /// told apart by an empty quote, its status bar's message, and the log's last lines of its checks.</summary>
    private static string UpdatesSaid(MainWindow window)
    {
        string[] checks = [.. Ui.IdeLog().Split('\n').Where(line => line.Contains("Updates: ", StringComparison.Ordinal)).TakeLast(4)];
        string notice = Notice(window) is { } shown ? $"its notice says '{shown}'" : "it shows no notice";
        return $"{notice}, its message '{window.Status}', and the log's last checks say:\n{(checks.Length == 0 ? "nothing" : string.Join('\n', checks))}";
    }

    /// <summary>The notice's words each time its button comes into view, as a person watching it come saw them.</summary>
    private static IDisposable FollowNotice(MainWindow window, List<string?> shown)
    {
        TextBlock words = window.FindControl<TextBlock>("UpdateText")!;
        return Avalonia.AvaloniaObjectExtensions.GetObservable(window.FindControl<Button>("UpdateButton")!, Avalonia.Visual.IsVisibleProperty)
            .Subscribe(new Avalonia.Reactive.AnonymousObserver<bool>(visible =>
            {
                if (visible)
                {
                    shown.Add(words.Text);
                }
            }));
    }

    /// <summary>This system's download by the release's fixed names: its system and its processor's.</summary>
    private static string ThisSystemsDownload()
    {
        string system = OperatingSystem.IsMacOS() ? "macos-{0}.dmg" : OperatingSystem.IsWindows() ? "windows-{0}.exe" : "linux-{0}.tar.gz";
        string named = "Passenger-" + string.Format(System.Globalization.CultureInfo.InvariantCulture, system, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        Assert.Contains(named, Downloads);
        return named;
    }

    /// <summary>A version newer than the running one: its major version and minor ones raised.</summary>
    private static string Bumped(int major, int minor)
    {
        int[] parts = [.. Product.Version.Split('-')[0].Split('.').Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))];
        return $"{parts[0] + major}.{minor}.0";
    }

    /// <summary>GitHub's list of releases holding one release alone, as <see cref="Made"/> makes it.</summary>
    private StandIn.Reply Release(string tag, bool prerelease = false, bool draft = false, IReadOnlyDictionary<string, byte[]>? files = null, string? body = null) =>
        Listed(Made(tag, prerelease, draft, files, body));

    /// <summary>GitHub's list of releases, as its API lists them, newest first.</summary>
    private static StandIn.Reply Listed(params JsonObject[] releases) => new(200, new JsonArray([.. releases]).ToJsonString());

    /// <summary>A release as GitHub's API gives it, with every download a release publishes; the files given are the
    /// stand-in's to serve, by their names, and the rest GitHub's, which no test downloads.</summary>
    private JsonObject Made(string tag, bool prerelease = false, bool draft = false, IReadOnlyDictionary<string, byte[]>? files = null, string? body = null)
    {
        JsonArray assets = [];
        foreach (string name in Downloads.Union(files?.Keys ?? []))
        {
            byte[]? served = files?.GetValueOrDefault(name);
            assets.Add(new JsonObject
            {
                ["name"] = name,
                ["browser_download_url"] = served is null ? $"https://github.com/Zaneris/passenger/releases/download/{tag}/{name}" : github.Serve(tag, name, served).AbsoluteUri,
                ["size"] = served?.Length ?? 1000,
            });
        }
        JsonObject release = new()
        {
            ["tag_name"] = tag,
            ["name"] = $"Passenger {tag}",
            ["html_url"] = $"https://github.com/Zaneris/passenger/releases/tag/{tag}",
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["assets"] = assets,
            ["body"] = body,
        };
        return release;
    }

    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    /// <summary>A wall clock the test moves, as a night's sleep moves the machine's while the window's timers wait.</summary>
    private sealed class WallClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 11, 13, 4, 15, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }

    /// <summary>A system tool the fixtures run, as the IDE runs its own, failing the test naming it when it fails.</summary>
    private static ChildResult Tool(string tool, params string[] arguments)
    {
        ChildResult result = Ran(tool, arguments, TimeSpan.FromMinutes(2));
        Assert.True(result.Succeeded, $"The test's installed copy is not as it was to be made: {tool} {string.Join(' ', arguments)} exited with code {result.ExitCode}:\n{result.Error}{result.Output}");
        return result;
    }

    /// <summary>A system tool run on the thread pool and waited for on the UI thread, where the tests and their fixtures'
    /// copies ask for it, with the window's loop running, never for its work to land; its own limit fails it first.</summary>
    private static ChildResult Ran(string tool, IReadOnlyList<string> arguments, TimeSpan limit) => Ran(ChildProcesses.Command(tool, arguments), limit);

    private static ChildResult Ran(ProcessStartInfo start, TimeSpan limit) =>
        Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(start, limit: limit)), ChildProcesses.Display(start), limit + TestFolders.Spare);

    /// <summary>Waits for what a closed window started to end, as the IDE's process waits for it before installing an
    /// update, the loop running meanwhile; it ends within <see cref="MainWindow.EndLimit"/> of the close.</summary>
    private static void Ended(MainWindow window) =>
        Ui.AwaitPumped(window.Ended ?? Task.CompletedTask, "what the closed window started to end", MainWindow.EndLimit + TimeSpan.FromSeconds(5));

    /// <summary>A Passenger installed in a test folder as this system's disk image or installer puts it, at the running
    /// version, and the files a newer release carries for its update.</summary>
    private abstract class InstalledCopy
    {
        public static InstalledCopy Make(DiskImages images, string folder)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
                "Passenger installs updates itself on macOS, Windows and Linux alone");
            if (OperatingSystem.IsLinux())
            {
                return new LinuxInstall(folder);
            }
            return OperatingSystem.IsMacOS() ? new AppBundle(images, folder) : new VelopackInstall(folder);
        }

        /// <summary>The copy's updater, as a start of the copy makes it.</summary>
        public abstract Updater Updater();

        /// <summary>The files of a release of this version that this copy's update downloads, by name.</summary>
        public abstract IReadOnlyDictionary<string, byte[]> Update(string version);

        /// <summary>The package itself, of those files.</summary>
        public abstract string Package(string version);

        /// <summary>Whether this version is downloaded and waiting to install.</summary>
        public abstract bool Holds(string version);

        /// <summary>What is in the folder updates download to.</summary>
        public abstract IReadOnlyList<string> Downloaded();

        /// <summary>The version installed, where a test can see it change; null where it changes only once this process has
        /// ended (Windows).</summary>
        public abstract string? InstalledVersion { get; }
    }

    /// <summary>Passenger.app as the disk image puts it, in a test folder's Applications: its Info.plist and an executable
    /// that writes down that it started, signed ad hoc under the hardened runtime as tools/package-mac.sh signs it, and a
    /// bundle identifier of its own, so nothing takes it for the person's Passenger. Restart to Update runs its executable
    /// rather than asking LaunchServices to open it.</summary>
    [SupportedOSPlatform("macos")]
    private sealed class AppBundle : InstalledCopy
    {
        private readonly DiskImages images;
        private readonly string folder;

        /// <param name="images">The class's disk images, which the updates are.</param>
        /// <param name="folder">The test's folder, which holds Passenger's state and, unless another is given, its Applications.</param>
        /// <param name="applications">Where the app is installed, when not in the folder's Applications.</param>
        /// <param name="state">Passenger's state folder, when not the folder's state.</param>
        public AppBundle(DiskImages images, string folder, string? applications = null, string? state = null)
        {
            this.images = images;
            this.folder = folder;
            Applications = Directory.CreateDirectory(applications ?? Path.Combine(folder, "Applications")).FullName;
            Bundle = Path.Combine(Applications, "Passenger.app");
            Staging = Path.Combine(state ?? Path.Combine(folder, "state"), "updates");
            ImageFolder = Path.Combine(folder, "temporary", "updates");
            Make(Bundle, Product.Version);
        }

        public string Applications { get; }

        public string Bundle { get; }

        public string Executable => Path.Combine(Bundle, "Contents", "MacOS", "Passenger");

        public string Staging { get; }

        /// <summary>Where its updates download and open their disk images: a folder of the test's own, or the updater's own
        /// in the user's temporary folder where a test makes it null.</summary>
        public string? ImageFolder { get; init; }

        /// <summary>The folder its updates' disk images are opened in.</summary>
        public string Opened => ImageFolder ?? new DiskImageUpdater(Bundle, Staging).ImageFolder;

        /// <summary>Where each start of the app writes its version: in the folder above the one it is in, the test's own.</summary>
        public string Started => Path.Combine(folder, "started.txt");

        /// <summary>Where each start of the app as an agent's relay writes its version and process id, there too.</summary>
        public string Relayed => Path.Combine(folder, "relayed.txt");

        public override string? InstalledVersion => Version(Bundle);

        /// <summary>What its updates open their disk images with: this Mac's own unless a test names one.</summary>
        public DiskImageTool Images { get; init; } = DiskImageUpdater.ThisMacs;

        public override Updater Updater() => new DiskImageUpdater(Bundle, Staging, StartAsync) { Images = Images, ImageFolder = Opened };

        /// <summary>The copy's updater, <paramref name="renaming"/> run as each rename of the two that put the new app in
        /// place on a volume that cannot swap two folders begins.</summary>
        public DiskImageUpdater Updater(Action<string, string> renaming) =>
            new(Bundle, Staging, StartAsync) { Renaming = renaming, Images = Images, ImageFolder = Opened };

        /// <summary>The copy's updater, <paramref name="opening"/> run as each try to open the image begins, waiting
        /// <paramref name="pauses"/> between tries.</summary>
        public DiskImageUpdater Updater(Func<string, int, Task> opening, IReadOnlyList<TimeSpan> pauses) =>
            new(Bundle, Staging, StartAsync) { Opening = opening, OpenAgainAfter = pauses, Images = Images, ImageFolder = Opened };

        /// <summary>The copy's updater, <paramref name="lettingGo"/> run as each try to let its image go begins, waiting
        /// <paramref name="pauses"/> between tries.</summary>
        public DiskImageUpdater LettingGoUpdater(Func<IReadOnlyList<AttachedImages.Attachment>, int, Task> lettingGo, IReadOnlyList<TimeSpan> pauses) =>
            new(Bundle, Staging, StartAsync) { LettingGo = lettingGo, LetGoAgainAfter = pauses, Images = Images, ImageFolder = Opened };

        /// <summary>The copy's updater, <paramref name="opening"/> run as each try to open the image begins and
        /// <paramref name="lettingGo"/> as each try to let it go does.</summary>
        public DiskImageUpdater Updater(Func<string, int, Task> opening, Func<IReadOnlyList<AttachedImages.Attachment>, int, Task> lettingGo) =>
            new(Bundle, Staging, StartAsync) { Opening = opening, LettingGo = lettingGo, Images = Images, ImageFolder = Opened };

        public override string Package(string version) => ThisSystemsDownload();

        public override IReadOnlyDictionary<string, byte[]> Update(string version)
        {
            byte[] bytes = images.Of(version);
            string sums = $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {Package(version)}\n"
                + $"{Convert.ToHexStringLower(SHA256.HashData((byte[])[1, 2, 3]))}  Passenger-windows-x64.exe\n";
            return new Dictionary<string, byte[]> { [Package(version)] = bytes, ["SHA256SUMS"] = Encoding.UTF8.GetBytes(sums) };
        }

        /// <summary>An app of this version made ready under the folder downloads go to, whatever it is named there: not while
        /// the updater removes it, whose folder may go between a look's steps (a wait's look returns false, never throws).</summary>
        public override bool Holds(string version)
        {
            try
            {
                return Directory.EnumerateDirectories(Path.Combine(Staging, version)).Any(staged => Version(staged) == version);
            }
            catch (Exception gone) when (gone is DirectoryNotFoundException or FileNotFoundException)
            {
                return false;
            }
        }

        /// <summary>What its updates left where they download: in <see cref="Staging"/>, and <see cref="Opened"/> itself.</summary>
        public override IReadOnlyList<string> Downloaded()
        {
            List<string> left = Directory.Exists(Opened) ? [Opened] : [];
            try
            {
                left.AddRange(Directory.EnumerateFileSystemEntries(Staging).Select(p => Path.GetFileName(p)));
            }
            catch (DirectoryNotFoundException)
            {
            }
            return left;
        }

        /// <summary>The installed app made the build's own Passenger, its executable beside the build's assemblies, signed
        /// again as each file of Contents/MacOS is: an agent's relay runs from it.</summary>
        public void Relaying()
        {
            string macOS = Path.Combine(Bundle, "Contents", "MacOS");
            File.Delete(Executable);
            string[] build = [.. Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(file => Path.GetFileName(file) is "Passenger" or "Passenger.deps.json" or "Passenger.runtimeconfig.json" || file.EndsWith(".dll", StringComparison.Ordinal))];
            // A clone where the volume makes one, as fast as a link and as much the app's own as a copy.
            if (!Ran("/bin/cp", ["-c", .. build, macOS], TimeSpan.FromMinutes(1)).Succeeded)
            {
                Tool("/bin/cp", [.. build, macOS]);
            }
            Tool("/usr/bin/codesign", ["--force", "--sign", "-", .. Directory.EnumerateFiles(macOS).Where(file => file != Executable)]);
            Tool("/usr/bin/codesign", "--force", "--sign", "-", Bundle);
        }

        /// <summary>Whether the installed app's signature holds, as Gatekeeper checks it.</summary>
        public bool SignatureHolds() => Ran("/usr/bin/codesign", ["--verify", "--deep", "--strict", Bundle], TimeSpan.FromMinutes(2)).Succeeded;

        /// <summary>The app of a version at a bundle's path, signed; its start is written down in the folder above the one it
        /// is put in, so one disk image serves every test's folder. Started as an agent's relay, it says so with its process
        /// id, and becomes the build's Passenger in that process.</summary>
        public static void Make(string bundle, string version)
        {
            string macOS = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS")).FullName;
            File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>CFBundleName</key><string>Passenger</string>
                    <key>CFBundleIdentifier</key><string>io.github.zaneris.passenger.tests</string>
                    <key>CFBundleExecutable</key><string>Passenger</string>
                    <key>CFBundlePackageType</key><string>APPL</string>
                    <key>CFBundleShortVersionString</key><string>{version}</string>
                </dict>
                </plist>
                """);
            string executable = Path.Combine(macOS, "Passenger");
            File.WriteAllText(executable, $"""
                #!/bin/sh
                if [ "$1" = --mcp-attach ]; then
                  echo "relayed {version} $$" >> "$(dirname "$0")/../../../../relayed.txt"
                  exec "{Path.Combine(AppContext.BaseDirectory, "Passenger")}" "$@"
                fi
                echo started {version} >> "$(dirname "$0")/../../../../started.txt"

                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Tool("/usr/bin/codesign", "--force", "--options", "runtime", "--sign", "-", bundle);
        }

        /// <summary>The version of the app at <paramref name="bundle"/>, or null where none is there.</summary>
        public static string? Version(string bundle)
        {
            string plist = Path.Combine(bundle, "Contents", "Info.plist");
            if (!File.Exists(plist))
            {
                return null;
            }
            System.Text.RegularExpressions.Match version = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(plist),
                @"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>");
            return version.Success ? version.Groups[1].Value : null;
        }

        private static async Task StartAsync(string bundle) =>
            await ChildProcesses.RunAsync(ChildProcesses.Command(Path.Combine(bundle, "Contents", "MacOS", "Passenger"), []), limit: TimeSpan.FromSeconds(30));
    }

    /// <summary>The disk image of each version a test updates to, made the first time one asks for it and kept for the class:
    /// hdiutil takes seconds over each, and the app in it is the same for every test (<see cref="AppBundle.Make"/>).</summary>
    public sealed class DiskImages : IDisposable
    {
        private readonly string folder = TestFolders.Unowned("update-images-");
        private readonly Dictionary<string, byte[]> made = new(StringComparer.Ordinal);

        [SupportedOSPlatform("macos")]
        public byte[] Of(string version)
        {
            if (!made.TryGetValue(version, out byte[]? bytes))
            {
                string stage = Path.Combine(folder, $"image-{version}");
                AppBundle.Make(Path.Combine(stage, "Passenger.app"), version);
                string image = Path.Combine(folder, $"image-{version}.dmg");
                Tool("/usr/bin/hdiutil", "create", "-volname", $"Passenger {version}", "-srcfolder", stage, "-fs", "HFS+", "-format", "UDZO", "-ov", image, "-quiet");
                bytes = made[version] = File.ReadAllBytes(image);
            }
            return bytes;
        }

        public void Dispose() => TestFolders.Delete(folder);
    }

    /// <summary>Passenger as its Windows installer (Velopack's) puts it, in a test folder: the install's folder with
    /// current\ and packages\, on this processor's channel, as Velopack's own test locator describes it.</summary>
    private sealed class VelopackInstall : InstalledCopy
    {
        private readonly string root;
        private readonly string current;
        private readonly string packages;
        private readonly string channel = $"win-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

        public VelopackInstall(string folder)
        {
            root = Directory.CreateDirectory(Path.Combine(folder, "PassengerIDE")).FullName;
            current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        }

        public override string? InstalledVersion => null;

        /// <summary>The install's current\ folder, which the installed version runs from.</summary>
        public string Current => current;

        public override Updater Updater() =>
            new VelopackUpdater(new TestVelopackLocator("PassengerIDE", Product.Version, packages, current, root, Path.Combine(root, "Update.exe"), channel));

        public override string Package(string version) => $"PassengerIDE-{version}-{channel}-full.nupkg";

        public override IReadOnlyDictionary<string, byte[]> Update(string version)
        {
            byte[] package = Nupkg(version);
            JsonObject feed = new()
            {
                ["Assets"] = new JsonArray(new JsonObject
                {
                    ["PackageId"] = "PassengerIDE",
                    ["Version"] = version,
                    ["Type"] = "Full",
                    ["FileName"] = Package(version),
                    ["SHA256"] = Convert.ToHexString(SHA256.HashData(package)),
                    ["Size"] = package.Length,
                }),
            };
            return new Dictionary<string, byte[]> { [$"releases.{channel}.json"] = Encoding.UTF8.GetBytes(feed.ToJsonString()), [Package(version)] = package };
        }

        public override bool Holds(string version) => File.Exists(Path.Combine(packages, Package(version)));

        public override IReadOnlyList<string> Downloaded() =>
            [.. Directory.EnumerateFiles(packages).Select(p => Path.GetFileName(p)).Where(name => !name.StartsWith('.'))];

        /// <summary>A full package as vpk packs one, as small as can be: its manifest and the app's executable.</summary>
        private byte[] Nupkg(string version)
        {
            using MemoryStream bytes = new();
            using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (StreamWriter manifest = new(zip.CreateEntry("PassengerIDE.nuspec").Open()))
                {
                    manifest.Write($"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
                          <metadata>
                            <id>PassengerIDE</id>
                            <version>{version}</version>
                            <title>Passenger</title>
                            <authors>Passenger</authors>
                            <description>Passenger</description>
                            <channel>{channel}</channel>
                            <mainExe>Passenger.exe</mainExe>
                          </metadata>
                        </package>
                        """);
                }
                using StreamWriter executable = new(zip.CreateEntry("lib/app/Passenger.exe").Open());
                executable.Write($"Passenger {version}");
            }
            return bytes.ToArray();
        }
    }

    /// <summary>Passenger as the Linux archive's install.sh installs it for one person, over a home of the test's own
    /// (~/.local/opt/passenger, its command and its menu entry), or for everyone as a copy in an opt/ of the test's, from a
    /// passenger/ folder of tools/linux's own files whose app writes down that it started (and, as an agent's relay, its
    /// version and process id, becoming the build's Passenger in that process); each release's archive is made from such
    /// a folder of its version. Restart to Update runs the install's launcher and waits for it.</summary>
    [SupportedOSPlatform("linux")]
    private sealed class LinuxInstall : InstalledCopy
    {
        private const string App = "io.github.zaneris.passenger";

        private readonly string folder;
        private readonly bool forEveryone;
        private readonly Dictionary<string, byte[]> archives = new(StringComparer.Ordinal);

        public LinuxInstall(string folder, bool forEveryone = false)
        {
            this.folder = PathNames.Canonical(folder);
            this.forEveryone = forEveryone;
            Home = Directory.CreateDirectory(Path.Combine(this.folder, "home")).FullName;
            Staging = Path.Combine(this.folder, "state", "updates");
            string first = Unpacked(Path.Combine(this.folder, "first"), Product.Version);
            if (forEveryone)
            {
                Install = Path.Combine(Directory.CreateDirectory(Path.Combine(this.folder, "opt")).FullName, "passenger");
                Tool("/bin/cp", "-R", first, Install);
                return;
            }
            Install = Path.Combine(Home, ".local", "opt", "passenger");
            ProcessStartInfo install = ChildProcesses.Command("/bin/sh", [Path.Combine(first, "install.sh")], Home);
            foreach ((string name, string? value) in Variables())
            {
                install.Environment[name] = value;
            }
            install.Environment.Remove("SUDO_USER");
            ChildResult installed = Ran(install, TimeSpan.FromMinutes(1));
            Assert.True(installed.Succeeded, $"install.sh did not install the test's Passenger: {installed.Error}{installed.Output}");
        }

        public string Home { get; }

        public string Install { get; }

        public string Staging { get; }

        public string Executable => Path.Combine(Install, "app", "Passenger");

        /// <summary>The command install.sh links on the PATH.</summary>
        public string Command => Path.Combine(Home, ".local", "bin", "passenger-ide");

        public string Entry => Path.Combine(Home, ".local", "share", "applications", App + ".desktop");

        public string Icon => Path.Combine(Home, ".local", "share", "icons", "hicolor", "256x256", "apps", App + ".png");

        /// <summary>Where each start of the app writes its version, in the test's folder.</summary>
        public string Started => Path.Combine(folder, "started.txt");

        /// <summary>Where each start of the app as an agent's relay writes its version and process id, there too.</summary>
        public string Relayed => Path.Combine(folder, "relayed.txt");

        public override string? InstalledVersion => ReleaseOf(Install);

        public override Updater Updater() =>
            new LinuxUpdater(Install, Staging, forEveryone ? LinuxUpdater.InstallKind.System : LinuxUpdater.InstallKind.PerUser, mayAsk: false,
                running: InstalledVersion, environment: Variables(), start: StartAsync);

        public override string Package(string version) => ThisSystemsDownload();

        public override IReadOnlyDictionary<string, byte[]> Update(string version)
        {
            if (!archives.TryGetValue(version, out byte[]? archive))
            {
                string release = Unpacked(Path.Combine(folder, "releases", version), version);
                using MemoryStream bytes = new();
                using (GZipStream gzip = new(bytes, CompressionLevel.Fastest, leaveOpen: true))
                {
                    TarFile.CreateFromDirectory(release, gzip, includeBaseDirectory: true);
                }
                archive = archives[version] = bytes.ToArray();
            }
            string sums = $"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {Package(version)}\n"
                + $"{Convert.ToHexStringLower(SHA256.HashData((byte[])[1, 2, 3]))}  Passenger-windows-x64.exe\n";
            return new Dictionary<string, byte[]> { [Package(version)] = archive, ["SHA256SUMS"] = Encoding.UTF8.GetBytes(sums) };
        }

        /// <summary>A release of this version made ready where the updater makes it ready: unpacked beside the install, or
        /// for an install for everyone its archive in the state's folder; not while the updater removes it.</summary>
        public override bool Holds(string version)
        {
            try
            {
                return forEveryone
                    ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Archive(version)))) == Sum(version)
                    : ReleaseOf(Path.Combine(UpdateFolders.Beside(Install, version), "passenger")) == version;
            }
            catch (Exception gone) when (gone is DirectoryNotFoundException or FileNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Where an install for everyone keeps a release's archive for install.sh.</summary>
        public string Archive(string version) => Path.Combine(Staging, version, Package(version));

        /// <summary>The SHA-256 the release's SHA256SUMS gives its archive.</summary>
        public string Sum(string version) => Convert.ToHexStringLower(SHA256.HashData(archives[version]));

        /// <summary>install.sh of the install run as the test's person, into the test's home, on an archive and the SHA-256
        /// it is to have.</summary>
        public ChildResult InstallArchive(string archive, string sha256)
        {
            ProcessStartInfo install = ChildProcesses.Command("/bin/sh", [Path.Combine(Install, "install.sh"), "--archive", archive, "--sha256", sha256], Home);
            foreach ((string name, string? value) in Variables())
            {
                install.Environment[name] = value;
            }
            install.Environment.Remove("SUDO_USER");
            return Ran(install, TimeSpan.FromMinutes(1));
        }

        /// <summary>What is in the folder updates download to, and every release unpacked beside the install.</summary>
        public override IReadOnlyList<string> Downloaded()
        {
            List<string> found = [];
            if (Directory.Exists(Staging))
            {
                found.AddRange(Directory.EnumerateFileSystemEntries(Staging).Select(p => Path.GetFileName(p)));
            }
            found.AddRange(Directory.EnumerateDirectories(Path.GetDirectoryName(Install)!, UpdateFolders.Prefix + "*").Select(p => Path.GetFileName(p)));
            return found;
        }

        /// <summary>The installed app made the build's own Passenger: its executable copied, the rest of the build linked
        /// beside it. An agent's relay runs from it.</summary>
        public void Relaying()
        {
            string app = Path.Combine(Install, "app");
            File.Delete(Executable);
            File.Delete(Path.Combine(app, "Passenger.deps.json"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Passenger"), Executable);
            File.SetUnixFileMode(Executable, Runnable);
            foreach (string entry in Directory.EnumerateFileSystemEntries(AppContext.BaseDirectory))
            {
                string beside = Path.Combine(app, Path.GetFileName(entry));
                if (!Path.Exists(beside))
                {
                    File.CreateSymbolicLink(beside, entry);
                }
            }
        }

        /// <summary>The release a passenger/ folder holds, as its app says, or null for none.</summary>
        public static string? ReleaseOf(string passenger) =>
            File.Exists(Path.Combine(passenger, "app", "release.txt")) ? File.ReadAllText(Path.Combine(passenger, "app", "release.txt")) : null;

        /// <summary>The variables install.sh runs with, the test's home its own.</summary>
        private Dictionary<string, string> Variables() =>
            new() { ["HOME"] = Home, ["XDG_DATA_HOME"] = Path.Combine(Home, ".local", "share") };

        /// <summary>A passenger/ folder of a release as its archive unpacks, in <paramref name="at"/>: tools/linux's launcher,
        /// install.sh and file types, a menu entry naming the release, an icon of it, and an app that writes down its
        /// starts, with the release and a deps.json giving its version; the folder's path.</summary>
        private string Unpacked(string at, string version)
        {
            string linux = Path.Combine(XamlTests.Repository, "tools", "linux");
            string passenger = Directory.CreateDirectory(Path.Combine(at, "passenger")).FullName;
            string app = Directory.CreateDirectory(Path.Combine(passenger, "app")).FullName;
            File.WriteAllText(Path.Combine(app, "Passenger"), $"""
                #!/bin/sh
                if [ "$1" = --mcp-attach ]; then
                  echo "relayed {version} $$" >> "{Relayed}"
                  exec "{Path.Combine(AppContext.BaseDirectory, "Passenger")}" "$@"
                fi
                echo started {version} >> "{Started}"

                """);
            File.SetUnixFileMode(Path.Combine(app, "Passenger"), Runnable);
            File.WriteAllText(Path.Combine(app, "release.txt"), version);
            File.WriteAllText(Path.Combine(app, "Passenger.deps.json"), new JsonObject
            {
                ["libraries"] = new JsonObject { [$"Passenger/{version}"] = new JsonObject { ["type"] = "project", ["serviceable"] = false } },
            }.ToJsonString());
            foreach (string script in (string[])["install.sh", "passenger"])
            {
                File.Copy(Path.Combine(linux, script), Path.Combine(passenger, script));
                File.SetUnixFileMode(Path.Combine(passenger, script), Runnable);
            }
            File.WriteAllText(Path.Combine(passenger, App + ".desktop"), File.ReadAllText(Path.Combine(linux, App + ".desktop")) + $"X-Passenger-Release={version}\n");
            File.Copy(Path.Combine(linux, App + ".xml"), Path.Combine(passenger, App + ".xml"));
            File.WriteAllText(Path.Combine(passenger, App + ".png"), $"png {version}");
            File.WriteAllText(Path.Combine(passenger, App + ".svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            return passenger;
        }

        private static async Task StartAsync(string install) =>
            await ChildProcesses.RunAsync(ChildProcesses.Command(Path.Combine(install, "passenger"), []), limit: TimeSpan.FromSeconds(30));

        private const UnixFileMode Runnable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    }

    /// <summary>GitHub on a loopback port of its own: its list of releases, answered as the test says, once it lets the
    /// answers go where it held them, or taken and never answered until the client gives up, and a release's downloads, each
    /// request's head kept as it came. A file held is sent half way and the rest only once the test lets it go.</summary>
    internal sealed class StandIn : IDisposable
    {
        private const string DownloadPath = "/Zaneris/passenger/releases/download/";

        private readonly List<string> heads = [];
        private readonly ConcurrentDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        private readonly StandInHttp http;
        private Reply? answer;
        private Held? holding;
        private TaskCompletionSource? answersHeld;

        public StandIn()
        {
            http = new StandInHttp(AnswerAsync);
            Listing = new Uri($"{http.Root}/repos/Zaneris/passenger/releases");
            Downloads = new Uri(http.Root + DownloadPath);
        }

        public sealed record Reply(int Status, string Body);

        /// <summary>The list of releases, which the answer answers.</summary>
        public Uri Listing { get; }

        /// <summary>Where a release's files download from, under each release's tag.</summary>
        public Uri Downloads { get; }

        /// <summary>The answer to each request from now on; null takes it and never answers.</summary>
        public Reply? Answer
        {
            get => Volatile.Read(ref answer);
            set => Volatile.Write(ref answer, value);
        }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (heads)
                {
                    return [.. heads];
                }
            }
        }

        /// <summary>Serves a release's file from now on, and gives where.</summary>
        public Uri Serve(string tag, string name, byte[] content)
        {
            files[$"{tag}/{name}"] = content;
            return new Uri(Downloads, $"{tag}/{name}");
        }

        /// <summary>The requests for a file of a release, by its name.</summary>
        public string[] RequestsFor(string name) => [.. Requests.Where(head => head.StartsWith("GET ", StringComparison.Ordinal)
            && head.Split(' ')[1] is var path && path.StartsWith(DownloadPath, StringComparison.Ordinal) && path.EndsWith("/" + name, StringComparison.Ordinal))];

        /// <summary>Holds a file half sent until <see cref="Let"/>.</summary>
        public void Hold(string name) => Volatile.Write(ref holding, new Held(name, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

        /// <summary>Holds each answer to the list of releases, its request taken, until <see cref="Let"/>.</summary>
        public void HoldAnswers() => Volatile.Write(ref answersHeld, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Lets go of the file and the answers held.</summary>
        public void Let()
        {
            Interlocked.Exchange(ref holding, null)?.Gate.TrySetResult();
            Interlocked.Exchange(ref answersHeld, null)?.TrySetResult();
        }

        public void Dispose()
        {
            Let();
            http.Dispose();
        }

        private async Task AnswerAsync(StandInHttp.Exchange exchange)
        {
            lock (heads)
            {
                heads.Add(exchange.Head);
            }
            if (exchange.Path.StartsWith(DownloadPath, StringComparison.Ordinal))
            {
                await SendFileAsync(exchange, exchange.Path[DownloadPath.Length..]);
            }
            else if (Answer is { } reply)
            {
                if (Volatile.Read(ref answersHeld) is { } held)
                {
                    await held.Task.WaitAsync(exchange.Ending);
                }
                await exchange.ReplyAsync(reply.Status, "application/json; charset=utf-8", reply.Body);
            }
            else
            {
                await exchange.UntilClosedAsync();
            }
        }

        private async Task SendFileAsync(StandInHttp.Exchange exchange, string file)
        {
            if (!files.TryGetValue(file, out byte[]? body))
            {
                await exchange.ReplyAsync(404, null, []);
                return;
            }
            await exchange.HeadAsync(200, "application/octet-stream", body.Length);
            Held? hold = Volatile.Read(ref holding) is { } held && file.EndsWith("/" + held.Name, StringComparison.Ordinal) ? held : null;
            int half = hold is null ? body.Length : body.Length / 2;
            await exchange.Stream.WriteAsync(body.AsMemory(0, half), exchange.Ending);
            await exchange.Stream.FlushAsync(exchange.Ending);
            if (hold is not null)
            {
                await hold.Gate.Task.WaitAsync(exchange.Ending);
                await exchange.Stream.WriteAsync(body.AsMemory(half), exchange.Ending);
            }
        }

        /// <summary>A file held half sent, and what lets it go.</summary>
        private sealed record Held(string Name, TaskCompletionSource Gate);
    }
}
