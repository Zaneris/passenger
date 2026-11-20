using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>An HTML file's page beside its editor, a group's own, in the system's own web view (WebView2, WKWebView,
/// WebKitGTK): given the text as the editor has it, its relative addresses read from the file's folder (on Windows served
/// from it, <see cref="ServedPage"/>), and given it again as typing pauses, at the place it was scrolled to
/// (<see cref="PagePlace"/>). The web view keeps nothing between runs (its data under the state's cache, private). The page
/// never leaves itself: where it would go elsewhere, by a link or its own script, the banner over it says where and opens
/// it only when asked, a web page in the browser and a file of this machine in the editor; it says too what was not served
/// to it. A page setting no background stands on a browser's white, whatever the window's look. A Linux without WebKitGTK
/// 4.1 says so in the page's place; a window nobody sees is given the page with no web view to show it.</summary>
public sealed class HtmlPreview : DockPanel
{
    /// <summary>How long typing pauses before the page is given the text again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    /// <summary>What a Linux without WebKitGTK 4.1 says in the page's place.</summary>
    public const string NoWebKitGtk = "The HTML preview needs WebKitGTK 4.1, which this system lacks: install it (libwebkit2gtk-4.1-0 on Ubuntu and Debian, webkit2gtk4.1 on Fedora) and open the file again";

    private static readonly Lazy<string?> Lacking = new(() => OperatingSystem.IsLinux() && !Present("libwebkit2gtk-4.1.so.0") ? NoWebKitGtk : null);

    private readonly EditorArea area;
    private readonly Debounce typing;
    private readonly EmptyState said = new();
    private readonly Banner banner = new();
    private readonly Button open;

    /// <summary>What the page asked for since it was last given and was not served, each with why.</summary>
    private readonly List<string> notServed = [];

    /// <summary>Where the page last went that it was kept from since it was last given, what opening it does, or why it is
    /// never opened; null for nowhere.</summary>
    private (Uri To, Action? Open, string? Never)? wentAway;
    private NativeWebView? view;
    private readonly FollowedDocument followed;

    /// <summary>On Windows, what answers the web view's requests of the page and its folder; null elsewhere, before the
    /// web view is ready, and where WebView2 refused.</summary>
    private WebView2Pages? served;

    /// <summary>The page last served, whose addresses name its folder's files.</summary>
    private ServedPage? serving;

    /// <summary>The address the page was last given as its own, which its own load may navigate to.</summary>
    private Uri? loading;

    /// <summary>Where the page was scrolled to as it was last given again, put back once the new page has loaded.</summary>
    private PagePlace? place;

    /// <summary>The file whose page the web view holds, so another file's page is never read for this one's place.</summary>
    private OpenDocument? held;

    /// <summary>Whether a page given has yet to load: read meanwhile, the place would be the new page's start.</summary>
    private bool landing;

    /// <summary>Pages begun being given, which numbers each: only the newest is navigated to.</summary>
    private int gives;

    internal HtmlPreview(EditorArea area)
    {
        this.area = area;
        typing = new Debounce(Pause, GiveAsync);
        followed = FollowedDocument.OfText(OnChanged);
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        open = banner.Link("Open", () => wentAway?.Open?.Invoke());
        Children.Add(banner);
        if (WhyNone() is { } why)
        {
            said.Text = why;
            said.Margin = new Avalonia.Thickness(16);
            Children.Add(said);
        }
    }

    /// <summary>Why this system shows no page, null where its web view shows one; a test stands in for the system.</summary>
    public static Func<string?> WhyNone { get; set; } = () => Lacking.Value;

    /// <summary>What the page shows instead, null while it shows the file.</summary>
    public string? Said => said.Parent is null ? null : said.Text;

    /// <summary>The file it shows, null for none.</summary>
    public OpenDocument? Document => followed.Document;

    /// <summary>The text the page was last given, with the address its relative ones are read from; null for none.</summary>
    public (string Html, Uri Base)? Given { get; private set; }

    /// <summary>How many times the page has been given a text.</summary>
    public int Loads { get; private set; }

    /// <summary>How many pages given have loaded in the web view, each then put back at the place it was given at.</summary>
    public int Landed { get; private set; }

    /// <summary>What the banner over the page says of where it went and what it was not served; null for nothing.</summary>
    public string? Refused => banner.Said;

    /// <summary>What the banner's link says, null while it shows none.</summary>
    public string? Opens => open.IsVisible ? open.Content as string : null;

    /// <summary>The banner's link, which opens where the page went.</summary>
    public Button OpenLink => open;

    /// <summary>Shows a file's page, or nothing; its edits given again as typing pauses, each where the last was scrolled to.</summary>
    internal void Show(OpenDocument? next)
    {
        if (!followed.Show(next))
        {
            return;
        }
        typing.Stop();
        // Hidden or showing another file, the web view may have let its page go: a file shown again starts at its top.
        held = null;
        if (next is null)
        {
            return;
        }
        IdeErrors.Observe(GiveAsync());
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e) => typing.Restart();

    /// <summary>Typing's pause held open until let go (<see cref="Debounce.HoldOpen"/>): for a test of what keys typed within
    /// the pause give the page, however slow its machine is between them.</summary>
    public IDisposable HoldTypingOpen() => typing.HoldOpen();

    /// <summary>The page given the text as it stands, the web view made as it is first needed; the same file's page is read
    /// first for where it was scrolled to, unless the last one given has yet to load.</summary>
    private async Task GiveAsync()
    {
        if (followed.Document is not { } shown || said.Parent is not null)
        {
            return;
        }
        string folder = Path.GetDirectoryName(shown.Path) ?? shown.Path;
        Uri at = new(shown.Path);
        (string Html, Uri Base) page = (shown.Text.Snapshot.ToString(), new Uri(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar));
        Given = page;
        Loads++;
        loading = at;
        notServed.Clear();
        wentAway = null;
        Tell();
        // A window nobody sees (an agent's, a test's, a screenshot's) makes no web view of the system's.
        if (PassengerApp.ShowsNoWindow)
        {
            return;
        }
        NativeWebView web = View();
        int mine = ++gives;
        if (held != shown)
        {
            place = null;
        }
        else if (!landing)
        {
            place = await PlaceOf(web);
            if (mine != gives || followed.Document != shown)
            {
                return;
            }
        }
        // Served, the page is kept from what .gitignore leaves out below its folder, which git is asked for first.
        IReadOnlyList<string> leftOut = OperatingSystem.IsWindows() && served is not null ? await area.LeftOutUnder(folder) : [];
        if (mine != gives || followed.Document != shown)
        {
            return;
        }
        held = shown;
        landing = true;
        Navigate(web, shown.Path, page, leftOut);
    }

    /// <summary>The web view given the page: as text with its folder for its relative addresses, or on Windows, whose
    /// WebView2 reads no folder for a text, served from its folder at an address of Passenger's own. There nothing is given
    /// before the web view is ready, which gives the page as it comes (<see cref="OnAdapter"/>).</summary>
    private void Navigate(NativeWebView web, string path, (string Html, Uri Base) page, IReadOnlyList<string> leftOut)
    {
        if (!OperatingSystem.IsWindows())
        {
            web.NavigateToString(page.Html, page.Base);
        }
        else if (served is { } pages)
        {
            ServedPage next = new(path, page.Html, leftOut);
            next.Refused += (_, file) => NotServed(next, file);
            pages.Serve(next);
            serving = next;
            web.Navigate(next.Address);
        }
        else if (web.TryGetPlatformHandle() is not null)
        {
            web.NavigateToString(page.Html, page.Base);
        }
    }

    /// <summary>A web view ready, again once shown again: a WKWebView draws its ground, and a WebView2 is asked to serve the
    /// page, which is given now, as the new web view holds no page yet.</summary>
    private void OnAdapter(object? sender, WebViewAdapterEventArgs e)
    {
        if (e.TryGetPlatformHandle() is IAppleWKWebViewPlatformHandle apple)
        {
            DrawGround(apple.WKWebView);
        }
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        served?.Dispose();
        served = e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle ? WebView2Pages.Over(handle) : null;
        held = null;
        if (followed.Document is not null)
        {
            IdeErrors.Observe(GiveAsync());
        }
    }

    /// <summary>A WKWebView left to draw no ground shows the window through a page setting no background. Drawn by the web
    /// view, the ground lies under the page: a background given to the page's html would stop its body's filling the
    /// view.</summary>
    private static void DrawGround(nint web)
    {
        nint key = MacObjC.Send(MacObjC.Class("NSString"), MacObjC.Selector("stringWithUTF8String:"), "drawsBackground");
        nint yes = MacObjC.Send(MacObjC.Class("NSNumber"), MacObjC.Selector("numberWithBool:"), true);
        MacObjC.Send(web, MacObjC.Selector("setValue:forKey:"), yes, key);
    }

    private void OnAdapterGone(object? sender, WebViewAdapterEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            served?.Dispose();
            served = null;
        }
    }

    /// <summary>Where the page in the web view is scrolled to; null where the web view cannot say.</summary>
    private static async Task<PagePlace?> PlaceOf(NativeWebView web)
    {
        try
        {
            return PagePlace.Of(await web.InvokeScript(PagePlace.Read));
        }
        catch (Exception error)
        {
            // Each system's web view throws its own; the page is given at its start.
            IdeErrors.Write($"The HTML preview could not read where its page was scrolled to: {error}");
            return null;
        }
    }

    /// <summary>A page given has loaded: put back where the one before it was scrolled to. A load that failed (another
    /// address refused, a load given again before it ended) is not the page's.</summary>
    private void OnLanded(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (landing && e.IsSuccess && sender is NativeWebView web)
        {
            landing = false;
            IdeErrors.Observe(PutBackAsync(web, place));
        }
    }

    private async Task PutBackAsync(NativeWebView web, PagePlace? at)
    {
        if (at is { Away: true } back)
        {
            try
            {
                await web.InvokeScript(back.Restore);
            }
            catch (Exception error)
            {
                // Each system's web view throws its own; the page stays at its start.
                IdeErrors.Write($"The HTML preview could not put its page back where it was scrolled to: {error}");
            }
        }
        Landed++;
    }

    private NativeWebView View()
    {
        if (view is not null)
        {
            return view;
        }
        view = new NativeWebView { Background = Avalonia.Media.Brushes.White };
        view.EnvironmentRequested += (_, e) => Private(e);
        view.NavigationStarted += OnNavigating;
        view.NavigationCompleted += OnLanded;
        view.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is { IsAbsoluteUri: true } to)
            {
                KeptFrom(to);
            }
        };
        view.AdapterCreated += OnAdapter;
        view.AdapterDestroyed += OnAdapterGone;
        Children.Add(view);
        return view;
    }

    private void OnNavigating(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is { } to && Leaving(to))
        {
            e.Cancel = true;
        }
    }

    /// <summary>Whether the web view's going to <paramref name="to"/> leaves the page, which it never does: the page's own
    /// load goes on, as does a jump within it; anywhere else the page is kept from, the banner saying where.</summary>
    public bool Leaving(Uri to)
    {
        if (!to.IsAbsoluteUri || to.Scheme is "about" or "data" || loading is { } own && SamePage(to, own)
            || Given is { } given && SamePage(to, given.Base) || serving is { } page && SamePage(to, page.Address))
        {
            return false;
        }
        KeptFrom(to);
        return true;
    }

    private static bool SamePage(Uri to, Uri page) =>
        to.IsFile == page.IsFile && string.Equals(to.GetLeftPart(UriPartial.Path), page.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

    /// <summary>Where the page would have gone, by a link or by its own script, said over it and opened only when asked: a
    /// web page in the browser, a file of the served page's folder or of this machine in the editor, and a share's or a
    /// device's path never (<see cref="FoundPaths.OfFileAddress"/>).</summary>
    private void KeptFrom(Uri to)
    {
        Action? opens = null;
        string? opening = null;
        string? never = null;
        if (string.Equals(to.Host, ServedPage.Host, StringComparison.OrdinalIgnoreCase))
        {
            if (serving?.FileOf(to) is { } file)
            {
                (opens, opening) = (() => area.OpenLinked(file), "Open in Editor");
            }
        }
        else if (to.Scheme is "http" or "https")
        {
            (opens, opening) = (() => area.Browse(to), "Open in Browser");
        }
        else if (to.IsFile)
        {
            if (FoundPaths.OfFileAddress(to) is { } local)
            {
                (opens, opening) = (() => area.OpenLinked(local), "Open in Editor");
            }
            else
            {
                never = "another machine's or a device's, which is never opened";
            }
        }
        wentAway = (to, opens, never);
        open.Content = opening;
        Tell();
    }

    /// <summary>A file a page served asked for and was refused, said over the page while that page is the one shown.</summary>
    private void NotServed(ServedPage page, string file)
    {
        if (page == serving && !notServed.Contains(file))
        {
            notServed.Add(file);
            Tell();
        }
    }

    /// <summary>The banner over the page: where it was kept from going, its link opening it where it opens, and what was
    /// not served to it; hidden for neither.</summary>
    private void Tell()
    {
        List<string> parts = [];
        if (wentAway is { } went)
        {
            parts.Add($"The page was kept from going to {went.To.OriginalString}{(went.Never is { } never ? $", {never}" : string.Empty)}");
        }
        if (notServed.Count > 0)
        {
            parts.Add($"Not served to the page: {string.Join("; ", notServed)}");
        }
        banner.Say(parts.Count == 0 ? null : string.Join(". ", parts));
        open.IsVisible = wentAway is { Open: not null };
    }

    /// <summary>The web view keeps nothing a page leaves (cookies, storage), its folders the state's cache, never the app's.</summary>
    private static void Private(WebViewEnvironmentRequestedEventArgs e)
    {
        string folder = Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "cache", "webview");
        Containers.OwnFiles.Folder(folder);
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = folder;
                windows.IsInPrivateModeEnabled = true;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.BaseDataDirectory = Path.Combine(folder, "data");
                gtk.BaseCacheDirectory = Path.Combine(folder, "cache");
                gtk.EphemeralDataManager = true;
                break;
        }
    }

    /// <summary>Whether a system library loads, which it stays once it does: the web view loads it again.</summary>
    private static bool Present(string library) => NativeLibrary.TryLoad(library, out _);
}
