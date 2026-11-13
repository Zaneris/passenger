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
        Assert.Equal($"GET {github.Latest.PathAndQuery} HTTP/1.1", head[0]);
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
            ("no release at all", new StandIn.Reply(404, """{"message":"Not Found"}""")),
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
        Assert.True(ReleaseFeed.Default.Latest.IsLoopback, $"A test's window would ask {ReleaseFeed.Default.Latest} for the latest release, not a loopback address");

        github.Answer = new StandIn.Reply(500, """{"message":"Server Error"}""");
        MainWindow window = Started();
        string? before = window.Status;
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before, $"A check that failed was shown or said, where it is only logged: {UpdatesSaid(window)}");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Latest} for the latest release: it answered 500 Internal Server Error", StringComparison.Ordinal));
        window.Close();

        // A server that takes the request and never answers is given up on at the feed's timeout.
        github.Answer = null;
        window = Started(feedLimit: TimeSpan.FromSeconds(1));
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 2, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before, $"A check never answered was shown or said, where it is only logged: {UpdatesSaid(window)}");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Latest} for the latest release: no answer in 1 s", StringComparison.Ordinal));

        CheckForUpdates(window);
        Assert.Equal("Could not check for updates: no answer in 1 s", window.Status);
        window.Close();
    }

    /// <summary>A release answered late, as a cold start's first request on a busy machine may be, is announced once its
    /// answer comes within the feed's limit; until then the status bar shows nothing, and its notice comes with its words.</summary>
    [AvaloniaFact]
    public void A_release_answered_late_is_announced_as_its_answer_comes_and_the_notice_never_shows_without_its_words()
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

    [AvaloniaFact]
    public void An_installed_Passenger_downloads_a_newer_release_in_the_background_checks_it_and_offers_Restart_to_Update()
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

    [AvaloniaFact]
    public void A_download_that_does_not_match_the_SHA_256_its_release_gives_is_refused_and_the_person_told_why()
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

    [AvaloniaFact]
    public void With_Check_for_updates_off_nothing_is_downloaded_and_turning_it_off_drops_a_download_made()
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

    [AvaloniaFact]
    public void A_copy_its_installer_or_disk_image_did_not_put_in_place_never_downloads_an_update()
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

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void Restart_to_Update_puts_the_new_version_in_place_of_the_old_and_starts_it_and_closing_installs_it_too()
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

    /// <summary>Restart to Update with a read-only file's changes unsaved: the window asks what to do with them. Kept open,
    /// the restart is given up, so closing it later installs the update and starts nothing; Close Without Saving carries
    /// the restart on, installing the update and starting it.</summary>
    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void Restart_to_Update_held_by_a_read_only_files_question_restarts_once_answered_Close_Without_Saving()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        using IDisposable theme = TestsTheme();
        AppBundle copy = new(images, TestFolders.Create("installed-"));
        string folder = TestFolders.Create("restart-locked-");
        string locked = Path.Combine(folder, "Locked.cs");
        File.WriteAllText(locked, "class Locked { }\n");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            string newer = Bumped(1, 0);
            MainWindow window = ReadyWithLockedChanges(newer);
            Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
            Ui.ClickButton(Unsaved(window), "Cancel");
            Assert.True(window.IsVisible, "Cancel closed the window, and the read-only file's changes with it");
            window.Close();
            Ui.ClickButton(Unsaved(window), "Close Without Saving");
            Ui.WaitFor(() => !window.IsVisible, "the window to close without the read-only file's changes");
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == newer, $"Passenger closed with v{newer} ready left {copy.Bundle} at {copy.InstalledVersion}");
            Assert.True(Ui.Shared(copy.Started) == string.Empty, $"Restart to Update, given up, still started the new version: '{Ui.Shared(copy.Started)}'");

            string later = Bumped(1, 1);
            window = ReadyWithLockedChanges(later);
            Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
            Ui.ClickButton(Unsaved(window), "Close Without Saving");
            Ui.WaitFor(() => !window.IsVisible, "Restart to Update to close the window once the read-only file's question was answered");
            Ended(window);
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == later, $"Restart to Update left {copy.Bundle} at {copy.InstalledVersion}, not {later}");
            Assert.True(Ui.Shared(copy.Started) == $"started {later}\n",
                $"Restart to Update, answered Close Without Saving, did not start the new version once: what started says '{Ui.Shared(copy.Started)}'");
            Assert.Equal("class Locked { }\n", File.ReadAllText(locked));
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
            TestFolders.Delete(folder);
        }

        // A window over the folder whose read-only file has changes it cannot save, offering Restart to Update to a version.
        MainWindow ReadyWithLockedChanges(string version)
        {
            github.Answer = Release($"v{version}", files: copy.Update(version));
            MainWindow window = Started(copy.Updater());
            window.WatchForUpdates();
            window.Confirm = (_, _, _) => Task.FromResult(false);
            window.OpenFolder(folder);
            window.OpenFile("Locked.cs");
            Ui.Settle();
            window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
            window.KeyTextInput("public ");
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {version}", $"the status bar to offer Restart to Update to v{version}");
            Assert.True(window.EditorArea.Active is { IsModified: true, Name: "Locked.cs" }, "The read-only file was not left changed and unsaved");
            return window;
        }
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void An_update_that_could_not_take_the_apps_place_is_said_at_the_next_start_and_is_still_ready()
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
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: Passenger {newer} was not installed: ", StringComparison.Ordinal)
            && line.Contains("Permission denied", StringComparison.Ordinal));

        window = Started(copy.Updater());
        window.WatchForUpdates();
        Assert.Equal($"Passenger {newer} did not install: Help, Show Log says why", window.Status);
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "the update that did not install to be offered again");
        Assert.True(github.RequestsFor(copy.Package(newer)).Length == 1, $"{copy.Package(newer)} was downloaded again");
        window.Close();
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void Another_Passenger_open_from_the_same_app_holds_the_update_back_until_it_closes()
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
        }
        finally
        {
            ChildProcesses.Kill(other);
        }
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void An_app_on_another_volume_than_Passengers_state_is_updated_in_place_too()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "On Windows Velopack's Update.exe replaces the install once this process has ended; a test cannot end its own process");
        using IDisposable theme = TestsTheme();
        // A volume of the test's own, as an external drive would be, with Passenger installed on it.
        string folder = TestFolders.Create("installed-");
        string volume = Directory.CreateDirectory(Path.Combine(folder, "volume")).FullName;
        string disk = Path.Combine(folder, "volume.dmg");
        Tool("/usr/bin/hdiutil", "create", "-size", "20m", "-fs", "HFS+", "-volname", "Passenger Tests", "-ov", disk, "-quiet");
        Tool("/usr/bin/hdiutil", "attach", disk, "-nobrowse", "-noautoopen", "-mountpoint", volume, "-quiet");
        // Spotlight indexes a volume whatever folder it is mounted in, unless it holds this marker (TestRun.Unindexed's why).
        File.WriteAllText(Path.Combine(volume, ".metadata_never_index"), string.Empty);
        try
        {
            AppBundle copy = new(images, folder, volume);
            string newer = Bumped(1, 0);
            github.Answer = Release($"v{newer}", files: copy.Update(newer));
            MainWindow window = Started(copy.Updater());
            window.WatchForUpdates();
            Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", $"the status bar to offer Restart to Update to v{newer}");
            window.Close();
            Ended(window);
            window.InstallUpdate();
            string said = string.Join('\n', Ui.IdeLog().Split('\n').Where(l => l.Contains("Updates:", StringComparison.Ordinal)).TakeLast(3));
            Assert.True(copy.InstalledVersion == newer, $"Passenger on another volume closed with v{newer} ready, and is {copy.InstalledVersion}: {said}");
            Assert.True(copy.SignatureHolds(), $"The app put in place on another volume, {copy.Bundle}, is not whole: its signature does not hold");
            string[] beside = [.. Directory.EnumerateFileSystemEntries(volume).Select(p => Path.GetFileName(p))
                .Where(name => !name.StartsWith('.') || name.StartsWith(".Passenger", StringComparison.Ordinal))];
            Assert.True(beside.SequenceEqual(["Passenger.app"]), $"The update left {string.Join(", ", beside)} where the app is installed");
            Assert.True(copy.Downloaded().Count == 0, $"The update installed left {string.Join(", ", copy.Downloaded())} behind");
        }
        finally
        {
            Detach(volume);
        }
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public void An_agents_relay_follows_the_update_in_its_own_process_its_calls_meanwhile_and_after_answered_over_the_same_connection()
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

    [AvaloniaFact]
    [SupportedOSPlatform("linux")]
    public void A_Linux_install_of_ones_own_is_put_in_place_as_Passenger_restarts_its_agents_relay_following_and_its_menu_entry_refreshed()
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

    [AvaloniaFact]
    [SupportedOSPlatform("linux")]
    public void A_Linux_install_that_could_not_be_replaced_is_left_as_it_was_and_the_next_start_says_why()
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

    [AvaloniaFact]
    [SupportedOSPlatform("linux")]
    public void A_Linux_install_for_everyone_this_user_cannot_write_says_the_command_that_installs_it_and_a_click_copies_it()
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

            string unpacked = Path.Combine(copy.Staging, newer, "passenger");
            string command = $"sudo {ShellWords.Quoted(Path.Combine(copy.Install, "install.sh"), ShellKind.Posix)} --system --from {ShellWords.Quoted(unpacked, ShellKind.Posix)} --keep-replaced";
            Assert.True(window.UpdateByHand == command, $"The command that installs v{newer} is '{window.UpdateByHand}', not '{command}'");
            Assert.True(copy.Holds(newer), $"v{newer} is not unpacked at {unpacked}, which the command installs from");
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

    [AvaloniaFact]
    [SupportedOSPlatform("linux")]
    public void A_Linux_copy_a_package_manager_installed_says_its_package_manager_updates_it_and_downloads_nothing()
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

    private static void Detach(string volume) => TestVolumes.Detach(volume, (tool, arguments) => Ran(tool, arguments, TimeSpan.FromMinutes(1).Scaled()));

    /// <summary>The person's window as the app makes it, its settings kept in the test's state folder, asking the stand-in
    /// within the limit a person's Passenger gives GitHub, scaled to the run, or the test's own (a stand-in held to not
    /// answering); with the updater of a copy installed in a test folder, or, as every window but the person's own, none.</summary>
    private MainWindow Started(Updater? updater = null, TimeSpan? feedLimit = null)
    {
        ReleaseFeed feed = new(github.Latest, feedLimit ?? ReleaseFeed.ShortTimeout.Scaled());
        MainWindow window = new()
        {
            Width = 1200, Height = 800, StateFolder = settings, Releases = feed, SelfUpdate = updater,
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
            string[] application = [.. window.ApplicationMenu().Items.Select(i => (i as NativeMenuItem)?.Header ?? "-")];
            Assert.Equal(["About Passenger", "Check for Updates…", "-", "Settings…"], application);
            item = window.ApplicationMenu().Items.OfType<NativeMenuItem>().First(i => i.Header == "Check for Updates…");
        }
        else
        {
            Assert.Equal(["Check for Updates…", "About"], helpItems[^2..]);
            item = help.Items.OfType<NativeMenuItem>().First(i => i.Header == "Check for Updates…");
        }
        item.Command!.Execute(null);
        Ui.Settle();
    }

    /// <summary>The question a window closed with a read-only file's changes asks, held to the dialogs' rules.</summary>
    private static ChoiceDialog Unsaved(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the window to ask about the changes it cannot save");
        ChoiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
        Assert.Equal("Clear Read-Only Status", asked.Title);
        return asked;
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

    /// <summary>A release as GitHub's API gives it, with every download a release publishes; the files given are the
    /// stand-in's to serve, by their names, and the rest GitHub's, which no test downloads.</summary>
    private StandIn.Reply Release(string tag, bool prerelease = false, bool draft = false, IReadOnlyDictionary<string, byte[]>? files = null)
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
        };
        return new StandIn.Reply(200, release.ToJsonString());
    }

    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

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
        public AppBundle(DiskImages images, string folder, string? applications = null)
        {
            this.images = images;
            this.folder = folder;
            Applications = Directory.CreateDirectory(applications ?? Path.Combine(folder, "Applications")).FullName;
            Bundle = Path.Combine(Applications, "Passenger.app");
            Staging = Path.Combine(folder, "state", "updates");
            Make(Bundle, Product.Version);
        }

        public string Applications { get; }

        public string Bundle { get; }

        public string Executable => Path.Combine(Bundle, "Contents", "MacOS", "Passenger");

        public string Staging { get; }

        /// <summary>Where each start of the app writes its version: in the folder above the one it is in, the test's own.</summary>
        public string Started => Path.Combine(folder, "started.txt");

        /// <summary>Where each start of the app as an agent's relay writes its version and process id, there too.</summary>
        public string Relayed => Path.Combine(folder, "relayed.txt");

        public override string? InstalledVersion => Version(Bundle);

        public override Updater Updater() => new DiskImageUpdater(Bundle, Staging, StartAsync);

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

        public override IReadOnlyList<string> Downloaded()
        {
            try
            {
                return [.. Directory.EnumerateFileSystemEntries(Staging).Select(p => Path.GetFileName(p))];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
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

        private static string? Version(string bundle)
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

        /// <summary>A release of this version unpacked where the updater makes it ready: beside the install, or for an
        /// install for everyone in the state's folder; not while the updater removes it.</summary>
        public override bool Holds(string version)
        {
            string unpacked = forEveryone ? Path.Combine(Staging, version) : UpdateFolders.Beside(Install, version);
            try
            {
                return ReleaseOf(Path.Combine(unpacked, "passenger")) == version;
            }
            catch (Exception gone) when (gone is DirectoryNotFoundException or FileNotFoundException)
            {
                return false;
            }
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

    /// <summary>GitHub on a loopback port of its own: releases/latest, answered as the test says, once it lets the answers
    /// go where it held them, or taken and never answered until the client gives up, and a release's downloads, each
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
            Latest = new Uri($"{http.Root}/repos/Zaneris/passenger/releases/latest");
            Downloads = new Uri(http.Root + DownloadPath);
        }

        public sealed record Reply(int Status, string Body);

        public Uri Latest { get; }

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

        /// <summary>Holds each answer to releases/latest, its request taken, until <see cref="Let"/>.</summary>
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
