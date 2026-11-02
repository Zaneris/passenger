using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>An HTML file's page beside its editor, a group's own, in the system's own web view (WebView2, WKWebView,
/// WebKitGTK): given the text as the editor has it, its relative addresses read from the file's folder, and given it
/// again as typing pauses. The web view keeps nothing between runs (its data under the state's cache, private). A link
/// followed in it opens where Passenger opens one, a web page in the browser and a file in the editor, the page itself
/// staying. A Linux without WebKitGTK 4.1 says so in the page's place; a window nobody sees is given the page with no web
/// view to show it.</summary>
public sealed class HtmlPreview : Panel
{
    /// <summary>How long typing pauses before the page is given the text again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    /// <summary>What a Linux without WebKitGTK 4.1 says in the page's place.</summary>
    public const string NoWebKitGtk = "The HTML preview needs WebKitGTK 4.1, which this system lacks: install it (libwebkit2gtk-4.1-0 on Ubuntu and Debian, webkit2gtk4.1 on Fedora) and open the file again";

    private static readonly Lazy<string?> Lacking = new(() => OperatingSystem.IsLinux() && !Present("libwebkit2gtk-4.1.so.0") ? NoWebKitGtk : null);

    private readonly EditorArea area;
    private readonly Debounce typing;
    private readonly TextBlock said = new() { Classes = { StyleClasses.EmptyText }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private NativeWebView? view;
    private OpenDocument? document;

    /// <summary>The address the page was last given as its own, which its own load may navigate to.</summary>
    private Uri? loading;

    internal HtmlPreview(EditorArea area)
    {
        this.area = area;
        typing = new Debounce(Pause, () =>
        {
            Give();
            return Task.CompletedTask;
        });
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
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
    public OpenDocument? Document => document;

    /// <summary>The text the page was last given, with the address its relative ones are read from; null for none.</summary>
    public (string Html, Uri Base)? Given { get; private set; }

    /// <summary>How many times the page has been given a text.</summary>
    public int Loads { get; private set; }

    /// <summary>Shows a file's page, or nothing; its edits given again as typing pauses.</summary>
    internal void Show(OpenDocument? next)
    {
        if (next == document)
        {
            return;
        }
        typing.Stop();
        if (document is not null)
        {
            document.Text.Changed -= OnChanged;
        }
        document = next;
        if (next is null)
        {
            return;
        }
        next.Text.Changed += OnChanged;
        Give();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e) => typing.Restart();

    /// <summary>The page given the text as it stands, the web view made as it is first needed.</summary>
    private void Give()
    {
        if (document is not { } shown || said.Parent is not null)
        {
            return;
        }
        string folder = Path.GetDirectoryName(shown.Path) ?? shown.Path;
        Uri at = new(shown.Path);
        Given = (shown.Text.Snapshot.ToString(), new Uri(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar));
        Loads++;
        loading = at;
        // A window nobody sees (an agent's, a test's, a screenshot's) makes no web view of the system's.
        if (!PassengerApp.ShowsNoWindow)
        {
            View().NavigateToString(Given.Value.Html, Given.Value.Base);
        }
    }

    private NativeWebView View()
    {
        if (view is not null)
        {
            return view;
        }
        view = new NativeWebView();
        view.EnvironmentRequested += (_, e) => Private(e);
        view.NavigationStarted += OnNavigating;
        view.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Follow(e.Request);
        };
        Children.Add(view);
        return view;
    }

    /// <summary>The page's own load goes on, as does a jump within it; another address it would go to opens elsewhere.</summary>
    private void OnNavigating(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is not { } to || to.Scheme is "about" or "data" || !to.IsAbsoluteUri || loading is { } own && SamePage(to, own)
            || Given is { } given && SamePage(to, given.Base))
        {
            return;
        }
        e.Cancel = true;
        Follow(to);
    }

    private static bool SamePage(Uri to, Uri page) =>
        to.IsFile == page.IsFile && string.Equals(to.GetLeftPart(UriPartial.Path), page.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

    /// <summary>A link followed: a web page in the browser, a file of the disk in the editor.</summary>
    private void Follow(Uri? to)
    {
        if (to is { Scheme: "http" or "https" })
        {
            area.Browse(to);
        }
        else if (to is { IsFile: true })
        {
            area.OpenLinked(to.LocalPath);
        }
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
