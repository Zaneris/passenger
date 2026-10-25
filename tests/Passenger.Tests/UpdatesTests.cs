using System.Collections.Concurrent;
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
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.Updates;
using Passenger.Core;
using Velopack.Locators;

namespace Passenger.Tests;

/// <summary>Passenger says when a newer release is out, and installs it: an installed copy downloads it, checks its
/// SHA-256 (refusing one that does not match, saying why), offers Restart to Update and installs it as it ends; any other
/// copy says once per version that it is out, never of an older one, a pre-release, a draft or a failure. Check for
/// Updates asks whenever a person wants; the setting off, nothing is asked or downloaded. A stand-in on a loopback port
/// answers for GitHub and its downloads, which no test reaches; each installed copy is in a test folder, and each
/// version's disk image is made once for the class (<see cref="DiskImages"/>).</summary>
public sealed class UpdatesTests : IClassFixture<UpdatesTests.DiskImages>, IDisposable
{
    /// <summary>The downloads the release workflow publishes with every release, by fixed names.</summary>
    private static readonly string[] Downloads =
    [
        "Passenger-macos-arm64.dmg", "Passenger-windows-x64.exe", "Passenger-windows-arm64.exe",
        "Passenger-linux-x64.tar.gz", "Passenger-linux-arm64.tar.gz",
    ];

    private readonly string settings = TestFolders.Create("updates-");
    private readonly StandIn github = new();
    private readonly DiskImages images;

    public UpdatesTests(DiskImages images) => this.images = images;

    public void Dispose()
    {
        github.Dispose();
        TestFolders.Delete(settings);
    }

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

        Assert.True(Notice(window) == $"Passenger {newer} available", $"A newer release, v{newer}, is out, but the status bar's notice is '{Notice(window)}'");
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
        Assert.True(Notice(window) is null, $"v{newer} was announced already, yet the window started again announces it once more: '{Notice(window)}'");

        CheckForUpdates(window);
        Assert.True(Notice(window) == $"Passenger {newer} available", "Check for Updates did not show the newer release the window had announced before");
        Assert.Equal($"Passenger {newer} is available", window.Status);
        window.Close();

        string later = Bumped(1, 1);
        github.Answer = Release($"v{later}");
        window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(Notice(window) == $"Passenger {later} available", $"v{later} came out after v{newer} was announced, but the window shows '{Notice(window)}'");
        window.Close();
    }

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
            $"The latest release is the running version, yet the window shows '{Notice(window)}' and says '{window.Status}'");

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
            Assert.True(Notice(window) is null, $"With {what} out, the status bar shows '{Notice(window)}'");
        }
        window.Close();
    }

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
        Assert.True(Notice(window) is null, $"With Check for updates off, the status bar shows '{Notice(window)}'");

        window.ApplySettings(window.Settings with { CheckForUpdates = true });
        Ui.Settle();
        Assert.True(github.Requests.Count == 1, "Check for updates turned on again did not ask for the latest release");
        Assert.True(Notice(window) is not null, "Check for updates turned on again did not announce the newer release");
        window.Close();
    }

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
        Assert.True(Notice(window) is null && window.Status == before, $"A check that failed shows '{Notice(window)}' and says '{window.Status}'");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Latest} for the latest release: it answered 500 Internal Server Error", StringComparison.Ordinal));
        window.Close();

        // A server that takes the request and never answers is given up on at the feed's timeout.
        github.Answer = null;
        window = Started();
        window.WatchForUpdates();
        Ui.Settle();
        Assert.True(github.Requests.Count == 2, "The window did not ask for the latest release as it started");
        Assert.True(Notice(window) is null && window.Status == before, $"A check never answered shows '{Notice(window)}' and says '{window.Status}'");
        Assert.Contains(Ui.IdeLog().Split('\n'), line => line.Contains($"Updates: could not ask {github.Latest} for the latest release: no answer in 1 s", StringComparison.Ordinal));

        CheckForUpdates(window);
        Assert.Equal("Could not check for updates: no answer in 1 s", window.Status);
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
        window.Ended?.Wait();
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
        Assert.True(Notice(window) == $"Passenger {newer} available", $"Check for Updates with the setting off shows '{Notice(window)}'");
        Ui.Settle();
        string[] downloaded = [.. update.Keys.Where(name => github.RequestsFor(name).Length > 0)];
        Assert.True(downloaded.Length == 0, $"With Check for updates off, the window downloaded {string.Join(", ", downloaded)}");
        Assert.False(copy.Holds(newer), $"With Check for updates off, v{newer} is ready to install");

        window.ApplySettings(window.Settings with { CheckForUpdates = true });
        Ui.WaitFor(() => Notice(window) == $"Restart to update to Passenger {newer}", "Check for updates turned on to download the newer release");
        window.ApplySettings(window.Settings with { CheckForUpdates = false });
        Ui.WaitFor(() => !copy.Holds(newer) && copy.Downloaded().Count == 0, "the update downloaded to be dropped as Check for updates went off");
        Assert.True(Notice(window) == $"Passenger {newer} available", $"With the download dropped, the status bar still shows '{Notice(window)}'");
        Assert.Null(window.UpdateReady);
        window.Close();
        window.Ended?.Wait();
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
        Assert.True(Notice(window) == $"Passenger {newer} available", $"A copy that cannot update itself shows '{Notice(window)}', not the newer release's link");
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
        window.Ended?.Wait();
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
        window.Ended?.Wait();
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
            window.Ended?.Wait();
            window.InstallUpdate();
            Assert.True(copy.InstalledVersion == newer, $"Passenger closed with v{newer} ready left {copy.Bundle} at {copy.InstalledVersion}");
            Assert.True(Ui.Shared(copy.Started) == string.Empty, $"Restart to Update, given up, still started the new version: '{Ui.Shared(copy.Started)}'");

            string later = Bumped(1, 1);
            window = ReadyWithLockedChanges(later);
            Ui.Click(window.FindControl<Button>("UpdateButton")!, settle: false);
            Ui.ClickButton(Unsaved(window), "Close Without Saving");
            Ui.WaitFor(() => !window.IsVisible, "Restart to Update to close the window once the read-only file's question was answered");
            window.Ended?.Wait();
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
            window.Ended?.Wait();
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
        using System.Diagnostics.Process other = ChildProcesses.Start(ChildProcesses.Command(executable, ["120"]));
        try
        {
            Ui.Click(window.FindControl<Button>("UpdateButton")!);
            Assert.True(window.IsVisible, "Restart to Update closed the window while another Passenger runs from the same app");
            Assert.Equal($"Another Passenger is open from {copy.Bundle}: close it, then restart to update", window.Status);

            window.Close();
            window.Ended?.Wait();
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
            window.Ended?.Wait();
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

    /// <summary>Unmounts the test's volume. The system's own readers of new files (Spotlight, the code-signing checks)
    /// can hold a volume just written busy for a moment, which even a forced detach refuses (exit code 16): tried again
    /// for up to 10 s, then failed naming what holds it.</summary>
    private static void Detach(string volume)
    {
        ChildResult detached = null!;
        if (Waiting.Until(() => (detached = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/hdiutil", ["detach", volume, "-force", "-quiet"]),
            limit: TimeSpan.FromMinutes(1).Scaled())).GetAwaiter().GetResult()).Succeeded, TimeSpan.FromSeconds(10), every: TimeSpan.FromSeconds(1)))
        {
            return;
        }
        ChildResult holders = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command("/usr/sbin/lsof", ["+D", volume]), limit: TimeSpan.FromMinutes(1))).GetAwaiter().GetResult();
        Assert.Fail($"The test's volume {volume} could not be detached (hdiutil exited with code {detached.ExitCode}: {detached.Error.Trim()}); what has files open on it:\n{holders.Output}");
    }

    /// <summary>The person's window as the app makes it, its settings kept in the test's state folder, asking the stand-in;
    /// with the updater of a copy installed in a test folder, or, as every window but the person's own, none.</summary>
    private MainWindow Started(Updater? updater = null)
    {
        MainWindow window = new()
        {
            Width = 1200, Height = 800, StateFolder = settings, Releases = new ReleaseFeed(github.Latest, TimeSpan.FromSeconds(1)), SelfUpdate = updater,
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
        ChildResult result = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command(tool, arguments), limit: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, $"The test's installed copy is not as it was to be made: {tool} {string.Join(' ', arguments)} exited with code {result.ExitCode}:\n{result.Error}{result.Output}");
        return result;
    }

    /// <summary>A Passenger installed in a test folder as this system's disk image or installer puts it, at the running
    /// version, and the files a newer release carries for its update.</summary>
    private abstract class InstalledCopy
    {
        public static InstalledCopy Make(DiskImages images, string folder)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Passenger installs updates itself on macOS and Windows alone");
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

        public string Staging { get; }

        /// <summary>Where each start of the app writes its version: in the folder above the one it is in, the test's own.</summary>
        public string Started => Path.Combine(folder, "started.txt");

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

        /// <summary>Whether the installed app's signature holds, as Gatekeeper checks it.</summary>
        public bool SignatureHolds() =>
            Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/codesign", ["--verify", "--deep", "--strict", Bundle]))).GetAwaiter().GetResult().Succeeded;

        /// <summary>The app of a version at a bundle's path, signed; its start is written down in the folder above the one it
        /// is put in, so one disk image serves every test's folder.</summary>
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
            File.WriteAllText(executable, $"#!/bin/sh\necho started {version} >> \"$(dirname \"$0\")/../../../../started.txt\"\n");
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

    /// <summary>GitHub on a loopback port of its own: releases/latest, answered as the test says or taken and never answered
    /// until the client gives up, and a release's downloads, each request's head kept as it came. A file held is sent half
    /// way and the rest only once the test lets it go.</summary>
    internal sealed class StandIn : IDisposable
    {
        private const string DownloadPath = "/Zaneris/passenger/releases/download/";

        private readonly List<string> heads = [];
        private readonly ConcurrentDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        private readonly StandInHttp http;
        private Reply? answer;
        private Held? holding;

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

        public void Let() => Interlocked.Exchange(ref holding, null)?.Gate.TrySetResult();

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
