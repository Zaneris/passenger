using Avalonia.Platform.Storage;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Links: the Run, Debug, Build and Unit Tests consoles' stack frames, compiler messages, web addresses and the
/// folder a publish landed in (a click opens the file at its line, the folder in the file manager, or the address in the
/// browser) and the terminal's (its file paths on Cmd+click, Ctrl+click elsewhere, and its programs' own links); and Open browser after launch, which
/// opens a web project's launch URL once its program listens.</summary>
public partial class MainWindow
{
    private readonly List<Uri> browsed = [];

    /// <summary>Ends the wait for the last launch's program to listen, once it ended or another launched.</summary>
    private CancellationTokenSource? browserWait;

    /// <summary>Every address the window opened in the person's browser, in order, kept since the headless platform's
    /// launcher opens nothing.</summary>
    public IReadOnlyList<Uri> Browsed => browsed;

    private readonly List<string> browsersStarted = [];

    /// <summary>Every browser program the window started to open a page in (<see cref="PageBrowsers"/>), as its command line,
    /// in order, or would have where nobody sees it.</summary>
    public IReadOnlyList<string> BrowsersStarted => browsersStarted;

    private void AddLinks()
    {
        foreach (OutputView console in (OutputView[])[Bottom.Run, Bottom.Build])
        {
            console.LinkClicked += (_, target) => OpenLink(target);
        }
        // Every debugged program's console, each session's own, as it is made.
        Bottom.Debug.ConsoleLinkClicked += (_, target) => OpenLink(target);
        Bottom.Tests.LinkClicked += (_, target) => OpenLink(target);
        // The project folder the Run window's program and the Debug window's came from is looked in first.
        Bottom.Run.Links.Folders = () => LinkFolders(buildRun.RunProject);
        Bottom.Debug.Console.Links.Folders = () => LinkFolders(buildRun.DebugProject);
        Bottom.Build.Links.Folders = () => LinkFolders(null);
        Bottom.Tests.Links.Folders = () => LinkFolders(null);
    }

    /// <summary>What the terminal's lines link, found as they are drawn; none until the Terminal window is first made.</summary>
    public ConsoleLinks? TerminalLinks { get; private set; }

    /// <summary>The terminal's links: web addresses and stack frames as the consoles find them, and any path to a file,
    /// a relative one under the folder the shell said it is in, then the open folder's, each asked of the disk off the
    /// window's thread. A line's links are given as the view takes them once for each time they are found, so a frame
    /// drawing the line again makes nothing.</summary>
    private void AddTerminalLinks(ToolWindows.TerminalPanel terminal)
    {
        ConsoleLinks links = new() { FilePaths = true };
        TerminalLinks = links;
        System.Runtime.CompilerServices.ConditionalWeakTable<FoundLink[], (int Start, int Length, object Target, bool Hidden)[]> given = [];
        string? shellFolder = null;
        links.Folders = () => LinkFolders(shellFolder);
        // Drawn again in a frame of its own: a change of the shell's folder forgets what was found while a frame is drawn.
        links.Changed += (_, _) => UiThread.Post(terminal.View.InvalidateVisual);
        terminal.View.Links = text =>
        {
            string? at = terminal.Active?.Screen.WorkingDirectory;
            if (at != shellFolder)
            {
                shellFolder = at;
                links.Forget();
            }
            FoundLink[] found = links.In(text);
            if (found.Length == 0)
            {
                return [];
            }
            if (!given.TryGetValue(found, out (int Start, int Length, object Target, bool Hidden)[]? each))
            {
                each = new (int, int, object, bool)[found.Length];
                for (int i = 0; i < found.Length; i++)
                {
                    each[i] = (found[i].Start, found[i].Length, found[i].Target, found[i].Hidden);
                }
                given.Add(found, each);
            }
            return each;
        };
        terminal.View.LinkTo = ProgramLink;
        terminal.View.LinkClicked += (_, target) => OpenLink((LinkTarget)target);
    }

    /// <summary>What a link a program made in the terminal opens (OSC 8, as Spectre.Console's link markup writes): a web
    /// address in the browser, a file address on this machine its file or folder once clicked; null for any other, which
    /// stays text. The view asks it as it draws, so it asks nothing of the disk, and an address naming another host, a
    /// share, a device or an automount root is never a link (<see cref="FoundPaths.OfFileAddress"/>): drawing a line reaches
    /// no other machine, nor mounts anything.</summary>
    public static LinkTarget? ProgramLink(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            return new WebLink(uri);
        }
        return FoundPaths.OfFileAddress(uri) is { } path ? new ProgramFileLink(path) : null;
    }

    /// <summary>What an address written in a file's comment or string opens (<see cref="DocumentDecorations.Addresses"/>), its
    /// editor's Go to Declaration following it: a web address in the browser, a file address on this machine its file or
    /// folder once followed, as a program's link in the terminal (<see cref="ProgramLink"/>), so drawing it asks nothing of
    /// the disk; null for any other, which stays text.</summary>
    public static readonly Func<string, object?> WrittenLink = address => WebAddresses.Parse(address) is not { } uri ? null
        : uri.IsFile ? (FoundPaths.OfFileAddress(uri) is { } path ? new ProgramFileLink(path) : null)
        : new WebLink(uri);

    /// <summary>Opens a path a program linked, once clicked: its file in the editor, else its folder in the file manager,
    /// as the disk has them, asked off the window's thread; one that is neither is said in the status bar.</summary>
    private async Task OpenProgramFileAsync(string path)
    {
        (string? file, bool folder) = await BackgroundWork.Run(() => File.Exists(path) ? (PathNames.Canonical(path), false) : ((string?)null, Directory.Exists(path)));
        if (file is not null)
        {
            OpenLink(new FileLink(file, default));
        }
        else if (folder)
        {
            ShowInFileManager(path);
        }
        else
        {
            Say($"The program's link names {path}, which is not there");
        }
    }

    /// <summary>Where a relative path in a console is looked for: the project its output came from, the open folder, then
    /// the folder's projects.</summary>
    private IReadOnlyList<string> LinkFolders(string? project)
    {
        List<string> folders = [];
        if (project is not null)
        {
            folders.Add(project);
        }
        if (Folder is not null)
        {
            folders.Add(Folder);
        }
        folders.AddRange(Projects.Select(p => p.Folder).Where(f => !folders.Contains(f)));
        return folders;
    }

    /// <summary>Opens what a link names: a file at its line and column in the editor, a folder in the person's file
    /// manager, or an address in the browser.</summary>
    public void OpenLink(LinkTarget target)
    {
        UiThread.Check();
        switch (target)
        {
            case FileLink file:
                if (file.CompiledLines)
                {
                    NavigateTo(file.Path, CSharp.ServerPlaces.PositionOfCompiled(file.Path, CSharp.ServerPlaces.TextOf(Editors, file.Path),
                        new Lsp.LspPosition(file.Position.Line, file.Position.Column)));
                }
                else
                {
                    NavigateTo(file.Path, file.Position);
                }
                if (Editors.Active is { } active && PathNames.Same(active.Path, file.Path))
                {
                    Editors.CodeEditor.Focus();
                }
                break;
            case FolderLink folder:
                ShowInFileManager(folder.Path);
                break;
            case WebLink web:
                Browse(web.Address);
                break;
            case ProgramFileLink named:
                IdeErrors.Observe(OpenProgramFileAsync(named.Path));
                break;
        }
    }

    /// <summary>Opens an address in the person's browser, through the system's launcher as every link the IDE opens is: the
    /// browser is the person's, never ended with the IDE.</summary>
    public void Browse(Uri address) => Browse(address, null);

    /// <summary>Opens a page a run serves (a resource's endpoint, a container's port) in the browser Settings names, else the
    /// system's default.</summary>
    private void BrowsePage(Uri page) => Browse(page, PageBrowsers.Of(settings.Browser));

    /// <summary>Opens an address in the person's browser: through the system's launcher as every link the IDE opens is, or
    /// in the browser program a run names (<paramref name="browser"/>), started apart, which a window nobody sees never
    /// starts. The browser is the person's, never ended with the IDE.</summary>
    public void Browse(Uri address, PageBrowser? browser)
    {
        UiThread.Check();
        browsed.Add(address);
        IdeErrors.Observe(browser is null ? Launcher.LaunchUriAsync(address) : BrowseInAsync(address, browser));
    }

    /// <summary>Opens an address in a browser program, its command found off the window's thread (a name on the PATH as
    /// the login shell has it); what fails is said in the status bar.</summary>
    private async Task BrowseInAsync(Uri address, PageBrowser browser)
    {
        (System.Diagnostics.ProcessStartInfo? start, bool bundle, string? whyNot) = await BackgroundWork.Run(() => PageBrowsers.Opening(browser, address));
        if (start is null)
        {
            status.Text = PageBrowsers.NotOpened(address, whyNot!);
            return;
        }
        browsersStarted.Add(ChildProcesses.Display(start));
        if (Unseen)
        {
            return;
        }
        try
        {
            if (bundle)
            {
                // The system's open ends once the app has the page.
                ChildResult opened = await ChildProcesses.RunAsync(start, limit: TimeSpan.FromSeconds(30));
                if (!opened.Succeeded)
                {
                    status.Text = PageBrowsers.NotOpened(address, $"{browser.Where} names {browser.Line}, which did not open it: {opened.Said}");
                }
                return;
            }
            ChildProcesses.StartApart(start);
        }
        catch (Exception failed) when (ChildFailures.Of(failed))
        {
            status.Text = PageBrowsers.NotOpened(address, $"{browser.Where} names {browser.Line}, which did not start: {failed.Message}");
        }
    }

    private readonly List<string> played = [];
    private readonly List<string> handed = [];

    /// <summary>Every sound file the window was asked to play, in order, kept since a window shown nowhere plays none.</summary>
    public IReadOnlyList<string> Played => played;

    /// <summary>Every file the window handed the system's player, in order, or would have where nobody sees it: a sound as it
    /// is, or the WAV an Ogg Vorbis or MP3 was decoded to.</summary>
    public IReadOnlyList<string> SoundsHanded => handed;

    /// <summary>Plays a sound file through the system's player (<see cref="Sounds"/>), decoded off the window's thread where
    /// the player takes it only so, saying why where it cannot; a window nobody sees (<see cref="Unseen"/>) plays nothing.</summary>
    public async Task PlaySoundAsync(string file)
    {
        UiThread.Check();
        played.Add(file);
        (string? playable, string? whyNot) = await BackgroundWork.Run(() => Sounds.Playable(file, Path.Combine(StateFolder, "cache", "sounds")));
        if (playable is null)
        {
            status.Text = whyNot;
            return;
        }
        handed.Add(playable);
        if (Unseen)
        {
            return;
        }
        if (await Sounds.PlayAsync(playable) is { } why)
        {
            status.Text = why;
        }
    }

    private readonly List<string> shownInFileManager = [];

    /// <summary>Every file and folder the window showed in the file manager, in order (where a publish landed, the
    /// Explorer's Reveal, the log), kept since the headless platform opens nothing.</summary>
    public IReadOnlyList<string> ShownInFileManager => shownInFileManager;

    /// <summary>Shows a file chosen in its folder, or a folder's contents, in the person's file manager: the only way the
    /// IDE opens it (<see cref="FileManager"/>), the file manager the person's, never ended with the IDE.</summary>
    public void ShowInFileManager(string path)
    {
        UiThread.Check();
        shownInFileManager.Add(path);
        FileManager.Show(this, path);
    }

    /// <summary>Open browser after launch: the plan's browser URL opens once its program listens there (its port takes
    /// a connection), or a container's service answers HTTP there, the status adding to what it says meanwhile that it
    /// waits, and saying what it said alone once the page opens; never once the program ended, nor for a launch that came
    /// after it.</summary>
    private void OpenBrowserOnceListening(LaunchPlan plan, Task ended)
    {
        browserWait?.Cancel();
        browserWait = null;
        if (BrowserLaunch.Address(plan.BrowserUrl) is not { } address)
        {
            return;
        }
        CancellationTokenSource waiting = browserWait = new CancellationTokenSource();
        IdeErrors.Observe(ended.ContinueWith(_ => waiting.Cancel(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
        string? service = plan.BrowserService;
        TimeSpan limit = Waits.Of(BrowserLaunch.AnswerLimit);
        (string? Text, ProblemSeverity? Severity, StatusPlace? Place) before = status.Said;
        string? saying = service is null ? null : BrowserLaunch.Waiting(before.Text, service, address);
        if (saying is not null)
        {
            status.Say(saying, before.Severity, before.Place);
        }
        // Not background work: it lasts as long as the program takes to listen, which no screenshot or settling test waits for.
        IdeErrors.Observe(BackgroundWork.Unheld(async () =>
        {
            bool? up = service is null ? await BrowserLaunch.ListeningAsync(address, waiting.Token) : await BrowserLaunch.AnsweringAsync(address, limit, waiting.Token);
            UiThread.Post(() =>
            {
                // The wait's words give way to those before, unless anything since (a later launch's wait too) took their place.
                if (saying is not null && ReferenceEquals(browserWait, waiting) && status.Text == saying && up != false)
                {
                    status.SayAgain(before);
                }
                if (up is null || waiting.IsCancellationRequested)
                {
                    return;
                }
                if (up == true)
                {
                    Browse(address, PageBrowsers.Of(plan.BrowserLine, plan.Name, settings.Browser));
                }
                else
                {
                    status.Text = service is null ? BrowserLaunch.NotResponding(address) : BrowserLaunch.NotAnswered(service, address, limit);
                }
            });
        }));
    }
}
