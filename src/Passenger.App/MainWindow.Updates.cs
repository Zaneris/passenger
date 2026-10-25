using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.App.Updates;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App;

/// <summary>The check for updates, and the update: the person's window asks for the latest release as it starts and
/// once a day while Check for updates is on. A copy its installer or disk image put in place downloads a newer one in
/// the background, saying so in the status bar, checks it, offers Restart to Update and installs it as Passenger ends;
/// any other copy says once per version that one is out, linking this system's download. Help, Check for Updates asks
/// when a person wants. Every answer is logged; only one a person asked for, or a download that did not land, is
/// said.</summary>
public partial class MainWindow
{
    /// <summary>The words when there is nothing newer.</summary>
    public const string UpToDate = "Passenger is up to date";

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

    /// <summary>Ends a check or download under way as the window closes, so a feed that never answers holds nothing after it.</summary>
    private readonly CancellationTokenSource updatesEnd = new();
    private DispatcherTimer? updateTimer;
    private bool watchesUpdates;

    /// <summary>The release announced, for a window that keeps no state; the person's keeps it in the store.</summary>
    private string? announcedHere;

    /// <summary>The download under way, ended when the setting goes off or a newer release replaces it.</summary>
    private CancellationTokenSource? downloading;
    private LatestRelease? downloadingRelease;
    private Task? downloadTask;

    /// <summary>The release downloaded, checked and waiting to install, and whether the person chose to restart into it.</summary>
    private LatestRelease? readyRelease;
    private bool restartToUpdate;

    /// <summary>What the status bar's notice offers, which its click does.</summary>
    private UpdateNotice notice;

    /// <summary>Where this window asks for the latest release: GitHub's, or the one PASSENGER_RELEASES names, unless
    /// one is given.</summary>
    public ReleaseFeed Releases { get; init; } = ReleaseFeed.Default;

    /// <summary>How this copy installs a newer release, or null for one that only says it is out: every window but the
    /// person's own (App gives it this copy's, which the window after it takes as it closes), and theirs too when
    /// Passenger runs from anywhere its installer or disk image did not put it.</summary>
    public Updater? SelfUpdate
    {
        get => selfUpdate;
        init => selfUpdate = value;
    }

    private Updater? selfUpdate;

    /// <summary>The newer release the status bar shows, its version and what its link opens, or null while it shows
    /// none.</summary>
    public LatestRelease? UpdateShown { get; private set; }

    /// <summary>The release downloaded and checked, which installs as Passenger ends, or null.</summary>
    public LatestRelease? UpdateReady => readyRelease;

    private void AddUpdates()
    {
        actions!.Add(new IdeAction(AppActions.CheckForUpdates, "Check for Updates…", "Help",
            context => IdeErrors.Observe(CheckForUpdatesAsync(asked: true)))
        {
            Menus = OperatingSystem.IsMacOS() ? [] : [new MenuPlace("Help", 3, 1)],
        });
        actions.Add(new IdeAction(AppActions.RestartToUpdate, "Restart to Update", "Help", _ => RestartToUpdate())
        {
            WhyNot = _ => readyRelease is null ? "No update is downloaded and ready to install" : null,
        });
        Closed += (_, _) =>
        {
            updateTimer?.Stop();
            updatesEnd.Cancel();
            // Closed alone, another window open takes the watch over, and with it the update made ready (PassengerApp).
            if (readyRelease is { } ready && !HandsOnUpdates())
            {
                IdeErrors.Write($"Updates: closing with Passenger {ready.Version} ready to install{(restartToUpdate ? ", to start it again" : string.Empty)}");
                if (keepsSettings)
                {
                    state!.Updates.Set(state.Updates.Value with { Installing = ready.Version });
                    state.Updates.Flush();
                }
            }
        };
    }

    /// <summary>The person's window asks for the latest release now and once a day after, while its setting is on; a
    /// headless window (a test's, an agent's, a screenshot's) asks only when Check for Updates is chosen. It says first
    /// how the update the last session installed went.</summary>
    public void WatchForUpdates()
    {
        PassengerApp.WatchUpdatesIn(this);
        watchesUpdates = true;
        SayInstalled();
        if (!settings.CheckForUpdates)
        {
            // Off: nothing left from a session it was on in installs.
            Discard();
        }
        WatchUpdates(settings.CheckForUpdates);
    }

    /// <summary>Whether the window closing hands its watch for updates to another window open, rather than installing
    /// what it made ready as Passenger ends: it closes alone, not as Passenger quits.</summary>
    internal bool HandsOnUpdates() =>
        !quitting && PassengerApp.Windows.Any(w => w != this && w.IsVisible);

    /// <summary>The process's one watch for updates, taken from the window that had it as that one closed with this one
    /// open: its way of installing and the update it made ready, which this window offers and installs as Passenger ends,
    /// and the checks, the first now.</summary>
    internal void TakeOverUpdates(MainWindow from)
    {
        if (!IsVisible)
        {
            return;
        }
        selfUpdate ??= from.selfUpdate;
        if (from.readyRelease is { } ready && selfUpdate is not null)
        {
            readyRelease = ready;
            ShowReady(ready);
        }
        watchesUpdates = true;
        WatchUpdates(settings.CheckForUpdates);
    }

    /// <summary>Installs the update made ready, once the window has closed and what it started has ended, as the
    /// process ends: at once on macOS, by Velopack's Update.exe once this process has gone on Windows, then starting
    /// the new version when the person chose Restart to Update.</summary>
    public void InstallUpdate()
    {
        if (readyRelease is not null && SelfUpdate is { } updater)
        {
            updater.Install(restartToUpdate);
            IdeErrors.Flush();
        }
    }

    /// <summary>Starts the checks, the first now, as the setting comes on, and stops them (and drops a download made or
    /// under way) as it goes off.</summary>
    private void WatchUpdates(bool on)
    {
        if (!watchesUpdates || on == (updateTimer is not null))
        {
            return;
        }
        if (!on)
        {
            updateTimer!.Stop();
            updateTimer = null;
            Discard();
            return;
        }
        updateTimer = new DispatcherTimer { Interval = UpdateInterval };
        updateTimer.Tick += (_, _) => IdeErrors.Observe(CheckForUpdatesAsync(asked: false));
        updateTimer.Start();
        IdeErrors.Observe(CheckForUpdatesAsync(asked: false));
    }

    /// <summary>Asks for the latest release, off the UI thread. A newer one downloads when this copy installs updates
    /// and the setting is on; otherwise it is shown when a person asked or it is newer than the one announced last. A
    /// person who asked is told the rest too, while a check of the window's own only logs it.</summary>
    private async Task CheckForUpdatesAsync(bool asked)
    {
        if (asked)
        {
            status.Text = "Checking for updates…";
        }
        ReleaseFeed feed = Releases;
        CancellationToken end = updatesEnd.Token;
        ReleaseAnswer answer = await BackgroundWork.Run(() => Updates.Releases.AskAsync(feed, Product.Version, end));
        if (end.IsCancellationRequested)
        {
            return;
        }
        IdeErrors.Write($"Updates: {answer.Said}");
        switch (answer)
        {
            case ReleaseAnswer.Newer { Release: var release } when SelfUpdate is { } updater && settings.CheckForUpdates:
                Announced = release.Version;
                if (readyRelease?.Version == release.Version)
                {
                    ShowReady(release);
                    if (asked)
                    {
                        status.Text = $"Passenger {release.Version} is ready: restart to update";
                    }
                }
                else
                {
                    if (asked)
                    {
                        status.Text = $"Passenger {release.Version} is available, and downloading";
                    }
                    if (downloadingRelease?.Version != release.Version)
                    {
                        await DownloadAsync(updater, release);
                    }
                }
                break;
            case ReleaseAnswer.Newer { Release: var release }:
                bool told = Announced is { } before && VersionOrder.Instance.Compare(release.Version, before) <= 0;
                if (asked || !told)
                {
                    ShowUpdate(release, release.Link);
                }
                if (!told)
                {
                    Announced = release.Version;
                }
                if (asked)
                {
                    status.Text = $"Passenger {release.Version} is available";
                }
                break;
            case ReleaseAnswer.Failed { Reason: var reason } when asked:
                status.Text = $"Could not check for updates: {reason}";
                break;
            case ReleaseAnswer.Current when asked:
                status.Text = UpToDate;
                break;
        }
    }

    /// <summary>Downloads and checks the release's package, the status bar saying how far it has come, then offers
    /// Restart to Update; a package refused, or one that would not come, is said, and the notice links the release
    /// instead.</summary>
    private async Task DownloadAsync(Updater updater, LatestRelease release)
    {
        downloading?.Cancel();
        CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(updatesEnd.Token);
        downloading = mine;
        downloadingRelease = release;
        readyRelease = null;
        ShowDownloading(release, 0);
        CancellationToken cancel = mine.Token;
        // Progress posted before the download ended can land after its outcome, which it must not cover.
        Action<int> progress = UiThread.Marshal<int>(percent =>
        {
            if (downloading == mine && !cancel.IsCancellationRequested)
            {
                ShowDownloading(release, percent);
            }
        });
        // Not BackgroundWork: a download of minutes would hold every screenshot; its progress lands as it goes.
        Task<UpdateOutcome> running = Task.Run(() => updater.DownloadAsync(release, progress, cancel), cancel);
        downloadTask = running;
        UpdateOutcome? outcome = null;
        try
        {
            outcome = await running;
        }
        catch (OperationCanceledException)
        {
            // Ended by the setting going off, a newer release or the window closing, each of which says what follows.
        }
        catch (Exception error)
        {
            // Not one an updater expects (those are its outcomes): an IDE error, and the download did not land.
            IdeErrors.Report(error);
            outcome = new UpdateOutcome.Failed(error.Message, $"could not download Passenger {release.Version}: {error.Message}");
        }
        finally
        {
            if (downloading == mine)
            {
                downloading = null;
                downloadingRelease = null;
            }
            if (cancel.IsCancellationRequested)
            {
                outcome = null;
            }
            mine.Dispose();
        }
        if (outcome is null)
        {
            return;
        }
        IdeErrors.Write($"Updates: {outcome.Said}");
        switch (outcome)
        {
            case UpdateOutcome.Ready:
                readyRelease = release;
                ShowReady(release);
                break;
            case UpdateOutcome.Refused { Reason: var reason }:
                ShowRefused(release, $"Passenger {release.Version} was not installed: {reason}");
                status.Text = $"Passenger {release.Version} was not installed: {reason}";
                break;
            case UpdateOutcome.Failed { Reason: var reason }:
                ShowUpdate(release, release.Link, $"Could not download Passenger {release.Version}: {reason}");
                status.Text = $"Could not download Passenger {release.Version}: {reason}";
                break;
            default:
                ShowUpdate(release, release.Link);
                break;
        }
    }

    /// <summary>Quits Passenger, every window closing, to install the update and start it, as the person chose; a window
    /// that does not close (a file it could not save) installs nothing yet, and one that asks first restarts once answered.</summary>
    private void RestartToUpdate()
    {
        if (readyRelease is null || SelfUpdate is not { } updater)
        {
            return;
        }
        if (updater.OthersRunning())
        {
            status.Text = $"Another Passenger is open from {updater.Installed}: close it, then restart to update";
            return;
        }
        // A window kept open keeps the update for the next end, which starts nothing.
        restartToUpdate = true;
        PassengerApp.Quit(givenUp: () => restartToUpdate = false);
    }

    /// <summary>Stops a download and drops what was downloaded, the notice going back to a link to the release.</summary>
    private void Discard()
    {
        downloading?.Cancel();
        downloading = null;
        downloadingRelease = null;
        if (SelfUpdate is not { } updater)
        {
            return;
        }
        if (notice is UpdateNotice.Downloading or UpdateNotice.Ready && UpdateShown is { } shown)
        {
            ShowUpdate(shown, shown.Link);
        }
        readyRelease = null;
        // Once the download stopped has let go of its files. Not drawn: removing hundreds of megabytes is the disk's work.
        Task? stopping = downloadTask;
        IdeErrors.Observe(Task.Run(async () =>
        {
            if (stopping is not null)
            {
                await stopping.ContinueWith(_ => { }, TaskScheduler.Default);
            }
            updater.Discard();
        }));
    }

    /// <summary>What the last session's install came to, said once: the version it installed, or that it did not.</summary>
    private void SayInstalled()
    {
        if (!keepsSettings || state!.Updates.Value.Installing is not { } installing)
        {
            return;
        }
        state.Updates.Set(state.Updates.Value with { Installing = null });
        if (installing == Product.Version)
        {
            status.Text = $"Passenger was updated to {installing}";
            IdeErrors.Write($"Updates: Passenger {installing} was installed as the last session ended");
        }
        else
        {
            Say($"Passenger {installing} did not install: Help, Show Log says why", LogPlace);
            IdeErrors.Write($"Updates: Passenger {installing} was to install as the last session ended, but this is {Product.Version}");
        }
    }

    /// <summary>The newest version announced: the store's in the window a person uses, so a restart does not say it
    /// again.</summary>
    private string? Announced
    {
        get => keepsSettings ? state!.Updates.Value.Announced : announcedHere;
        set
        {
            announcedHere = value;
            if (keepsSettings)
            {
                state!.Updates.Set(state.Updates.Value with { Announced = value });
            }
        }
    }

    /// <summary>The release as a link to this system's download (or its page, for a release without one).</summary>
    private void ShowUpdate(LatestRelease release, Uri link, string? why = null)
    {
        UpdateShown = release with { Link = link };
        string opens = link == release.Link && release.Asset is { } asset ? $"Download {asset}" : $"Open the release page of {release.Tag}";
        Notice(UpdateNotice.Available, IconKeys.ArrowDown, TokenKeys.Link, $"Passenger {release.Version} available",
            $"{(why is null ? string.Empty : why + "\n")}{opens}\n{link}");
    }

    /// <summary>The download's progress, then, once it has all come (100), its check, which takes a while for a disk image.</summary>
    private void ShowDownloading(LatestRelease release, int percent)
    {
        UpdateShown = release;
        Notice(UpdateNotice.Downloading, IconKeys.ArrowDown, TokenKeys.Link,
            percent < 100 ? $"Downloading Passenger {release.Version}: {percent}%" : $"Checking Passenger {release.Version}…",
            $"Passenger {release.Version} is downloading from GitHub, to be checked and installed when Passenger restarts.\nClick to open its release page.");
    }

    private void ShowReady(LatestRelease release)
    {
        UpdateShown = release;
        Notice(UpdateNotice.Ready, IconKeys.Rerun, TokenKeys.Link, $"Restart to update to Passenger {release.Version}",
            $"Passenger {release.Version} is downloaded and checked. Click to restart into it;\notherwise it installs when Passenger closes.");
    }

    /// <summary>A download refused, in the editor's warning colour, its link the release's page rather than the file
    /// that did not match.</summary>
    private void ShowRefused(LatestRelease release, string why)
    {
        UpdateShown = release with { Link = release.Page };
        Notice(UpdateNotice.Refused, IconKeys.Error, TokenKeys.Warning, $"Passenger {release.Version} not installed",
            $"{why}\nOpen the release page of {release.Tag}\n{release.Page}");
    }

    private void Notice(UpdateNotice now, string icon, string brush, string text, string tip)
    {
        notice = now;
        UpdateText.Text = text;
        ToolTip.SetTip(UpdateButton, tip);
        UpdateIcon.Bind(PathIcon.DataProperty, UpdateIcon.GetResourceObservable(icon));
        UpdateIcon.Bind(PathIcon.ForegroundProperty, UpdateIcon.GetResourceObservable(brush));
        UpdateText.Bind(TextBlock.ForegroundProperty, UpdateText.GetResourceObservable(brush));
        UpdateButton.IsVisible = true;
    }

    /// <summary>The notice's click: the download (or the release's page) in the person's browser, through the system's
    /// own launcher as a link anywhere opens; Restart to Update once the update is ready.</summary>
    private void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (notice == UpdateNotice.Ready)
        {
            Request(AppActions.RestartToUpdate);
        }
        else if (UpdateShown is { } shown)
        {
            Browse(notice == UpdateNotice.Downloading ? shown.Page : shown.Link);
        }
    }

    private enum UpdateNotice
    {
        Available,
        Downloading,
        Ready,
        Refused,
    }
}
