using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An HTML file's page beside its text, in the system's web view, which a headless window neither makes nor
/// draws: what the page is given and when, the text as the editor has it with the file's folder for its relative
/// addresses, again once typing pauses and not before; off until the strip's toggle turns it on; a Linux without
/// WebKitGTK 4.1 saying so in the page's place; and, where a window is seen, the page given again at the place it was
/// scrolled to, the open folder's stylesheet and picture loaded, and a page over WebView2's 2 MB for a text shown
/// whole.</summary>
public sealed class HtmlPreviewTests : IDisposable
{
    private const string Page = "<!doctype html>\n<html>\n  <head><link rel=\"stylesheet\" href=\"site.css\"></head>\n  <body><h1>Hello</h1></body>\n</html>\n";

    /// <summary>A picture one pixel across.</summary>
    private const string Dot = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("html-"));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void An_HTML_files_page_is_given_its_text_beside_it_once_turned_on_and_again_as_typing_pauses()
    {
        File.WriteAllText(Path.Combine(folder, "index.html"), Page);
        File.WriteAllText(Path.Combine(folder, "site.css"), "h1 { color: teal; }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(Path.Combine(folder, "index.html")));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.True(group.PreviewToggle.IsVisible && group.PreviewToggle.IsChecked == false && ActionTips.Of(group.PreviewToggle) == "HTML Preview",
            $"The HTML file's tab strip offers no HTML Preview toggle, off (shown {group.PreviewToggle.IsVisible}, tip {ActionTips.Of(group.PreviewToggle)})");
        Assert.Null(group.Html);

        Ui.Click(group.PreviewToggle);
        HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed no page beside the HTML file");
        Assert.True(group.Editor.IsEffectivelyVisible, "The HTML file's text went for its page, which stands beside it");
        if (HtmlPreview.WhyNone() is { } why)
        {
            Assert.Equal(why, preview.Said);
            window.Close();
            Assert.Skip($"This system shows no page: {why}");
        }
        Assert.True(preview.Given is { } given && given.Html == Page && given.Base == new Uri(folder + Path.DirectorySeparatorChar) && preview.Loads == 1,
            $"The page was given {preview.Loads} times, last {preview.Given?.Base}: {preview.Given?.Html}");
        Assert.False(preview.GetLogicalChildren().OfType<NativeWebView>().Any(), "A window nobody sees made a web view of the system's");

        window.EditorArea.CodeEditor.Focus();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<h1>Hello");
        window.KeyTextInput(",");
        window.KeyTextInput(" shop");
        Assert.True(preview.Loads == 1, $"The page was given the text {preview.Loads - 1} more times while it was typed");
        Ui.WaitFor(() => preview.Loads == 2, () => $"the page given the text typed once typing paused ({preview.Loads} times)");
        Assert.Equal(Page.Replace("Hello", "Hello, shop", StringComparison.Ordinal), preview.Given!.Value.Html);

        Ui.Click(group.PreviewToggle);
        Assert.Null(group.Html);
        window.Close();
    }

    /// <summary>Scrolled down, the page is given an edit at the same place; given a page shorter than the place, it ends at
    /// its end; and a page scrolling itself as it loads is left where it went. Only a web view of the system's scrolls, which
    /// a window nobody sees never makes, so headless this skips, run by hand where Passenger's window is seen.</summary>
    [AvaloniaFact]
    public void An_HTML_page_given_again_keeps_the_place_it_was_scrolled_to()
    {
        if (PassengerApp.ShowsNoWindow)
        {
            Assert.Skip("A window nobody sees makes no web view of the system's, the only one to scroll a page: this runs where Passenger's window is seen");
        }
        if (HtmlPreview.WhyNone() is { } why)
        {
            Assert.Skip($"This system shows no page: {why}");
        }
        string index = Path.Combine(folder, "index.html");
        File.WriteAllText(index, Paragraphs(300));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(index));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed no page beside the HTML file");
        NativeWebView web = Assert.Single(preview.GetLogicalChildren().OfType<NativeWebView>());
        Ui.WaitFor(() => preview.Landed == 1, () => $"the page's first load ({preview.Loads} given, {preview.Landed} loaded)");
        Assert.True(Math.Abs(ScrolledTo(web, "window.scrollTo(0, 2000);") - 2000) < 1, $"A page of 300 paragraphs did not scroll to 2,000 but to {ScrolledTo(web)}");

        Typing.Replace(window, "Paragraph 3<", "Paragraph three<");
        Ui.WaitFor(() => preview.Landed == 2, () => $"the page given the edit to load ({preview.Loads} given, {preview.Landed} loaded)");
        Ui.WaitFor(() => Math.Abs(ScrolledTo(web) - 2000) < 1, () => $"the page given the edit to stand where the reader had scrolled, 2,000, not {ScrolledTo(web)}");

        Ui.Run(window, AppActions.Save);
        File.WriteAllText(index, Paragraphs(60));
        Ui.WaitFor(() => preview.Landed == 3, () => $"the shorter page written on disk to load ({preview.Loads} given, {preview.Landed} loaded)");
        double end = double.Parse(Script(web, "Math.max(0, document.scrollingElement.scrollHeight - window.innerHeight)")!.Trim('"'), CultureInfo.InvariantCulture);
        Assert.True(end is > 0 and < 2000, $"A page of 60 paragraphs should end above 2,000 and below its view's height, not at {end}");
        Ui.WaitFor(() => Math.Abs(ScrolledTo(web) - end) < 1, () => $"the page shorter than the place to stand at its end, {end}, not {ScrolledTo(web)}");

        File.WriteAllText(index, Paragraphs(300, "<script>window.addEventListener('load', function () { window.scrollTo(0, 300); });</script>"));
        Ui.WaitFor(() => preview.Landed == 4, () => $"the page scrolling itself to load ({preview.Loads} given, {preview.Landed} loaded)");
        Ui.WaitFor(() => Math.Abs(ScrolledTo(web) - 300) < 1, () => $"the page that scrolls itself as it loads to stand where it went, 300, not {ScrolledTo(web)}");
        window.Close();
    }

    /// <summary>A page in a subfolder, unsaved and as typed, takes a stylesheet from the open folder above it and a picture
    /// from beside it. On Windows, where Passenger serves the open folder, a picture beside that folder loads by no address.
    /// Only a web view of the system's loads a page, so headless this skips, run by hand where Passenger's window is
    /// seen.</summary>
    [AvaloniaFact]
    public void An_HTML_page_loads_the_open_folders_stylesheet_and_picture_as_it_is_typed()
    {
        if (PassengerApp.ShowsNoWindow)
        {
            Assert.Skip("A window nobody sees makes no web view of the system's, the only one to load a page: this runs where Passenger's window is seen");
        }
        if (HtmlPreview.WhyNone() is { } why)
        {
            Assert.Skip($"This system shows no page: {why}");
        }
        string open = Directory.CreateDirectory(Path.Combine(folder, "open")).FullName;
        string pages = Directory.CreateDirectory(Path.Combine(open, "pages")).FullName;
        string css = Directory.CreateDirectory(Path.Combine(open, "css")).FullName;
        string beside = Path.Combine(folder, "beside.png");
        File.WriteAllBytes(beside, Convert.FromBase64String(Dot));
        File.WriteAllBytes(Path.Combine(pages, "dot.png"), Convert.FromBase64String(Dot));
        File.WriteAllText(Path.Combine(css, "site.css"), "h1 { color: rgb(0, 128, 0); }\n");
        string index = Path.Combine(pages, "index.html");
        File.WriteAllText(index, $"""
            <!doctype html>
            <html>
              <head><link rel="stylesheet" href="../css/site.css"></head>
              <body>
                <h1>Hello</h1>
                <img id="inside" src="dot.png">
                <img id="up" src="../../beside.png">
                <img id="whole" src="{new Uri(beside).AbsoluteUri}">
              </body>
            </html>

            """);
        MainWindow window = Ui.OpenFolder(open, languageServer: false);
        Assert.Null(window.OpenFile(index));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed no page beside the HTML file");
        NativeWebView web = Assert.Single(preview.GetLogicalChildren().OfType<NativeWebView>());
        Ui.WaitFor(() => preview.Landed == 1, () => $"the page's first load ({preview.Loads} given, {preview.Landed} loaded)");
        CheckLoaded(web);

        Typing.Replace(window, "Hello<", "Hello, shop<");
        Ui.WaitFor(() => preview.Landed == 2, () => $"the page given the edit to load ({preview.Loads} given, {preview.Landed} loaded)");
        Assert.Equal("Hello, shop", Script(web, "document.querySelector('h1').textContent")?.Trim('"'));
        CheckLoaded(web);
        window.Close();
    }

    /// <summary>A page larger than WebView2 takes as a text is shown whole. Only a web view of the system's loads a page, so
    /// headless this skips, run by hand where Passenger's window is seen.</summary>
    [AvaloniaFact]
    public void An_HTML_page_of_several_megabytes_is_shown_whole()
    {
        if (PassengerApp.ShowsNoWindow)
        {
            Assert.Skip("A window nobody sees makes no web view of the system's, the only one to load a page: this runs where Passenger's window is seen");
        }
        if (HtmlPreview.WhyNone() is { } why)
        {
            Assert.Skip($"This system shows no page: {why}");
        }
        const int Count = 150_000;
        string index = Path.Combine(folder, "index.html");
        File.WriteAllText(index, Paragraphs(Count));
        Assert.True(new FileInfo(index).Length > 3_000_000, $"A page of {new FileInfo(index).Length} bytes proves nothing of a page over 2 MB");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(index));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed no page beside the HTML file");
        NativeWebView web = Assert.Single(preview.GetLogicalChildren().OfType<NativeWebView>());
        Ui.WaitFor(() => preview.Landed == 1, () => $"the large page to load ({preview.Loads} given, {preview.Landed} loaded)");
        Ui.WaitFor(() => Script(web, "document.querySelectorAll('p').length")?.Trim('"') == Count.ToString(CultureInfo.InvariantCulture),
            () => $"the page to hold its {Count} paragraphs, not {Script(web, "document.querySelectorAll('p').length")}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_system_without_a_web_view_for_the_page_says_so_in_its_place()
    {
        File.WriteAllText(Path.Combine(folder, "index.html"), Page);
        Func<string?> system = HtmlPreview.WhyNone;
        HtmlPreview.WhyNone = () => HtmlPreview.NoWebKitGtk;
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            Assert.Null(window.OpenFile(Path.Combine(folder, "index.html")));
            Ui.Settle();
            EditorGroup group = window.EditorArea.ActiveGroup;
            Ui.Click(group.PreviewToggle);
            HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed nothing beside the HTML file");
            Assert.Equal(HtmlPreview.NoWebKitGtk, preview.Said);
            Assert.True(preview.Given is null && !preview.GetLogicalChildren().OfType<NativeWebView>().Any(), "A system without a web view was given a page");
            Assert.Contains(preview.GetLogicalChildren().OfType<TextBlock>(), t => t.Text == HtmlPreview.NoWebKitGtk && t.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            HtmlPreview.WhyNone = system;
        }
    }

    /// <summary>A page of numbered paragraphs, its head holding <paramref name="head"/>.</summary>
    private static string Paragraphs(int count, string head = "") =>
        $"<!doctype html>\n<html>\n  <head>{head}</head>\n  <body>\n{string.Concat(Enumerable.Range(1, count).Select(i => $"    <p>Paragraph {i}</p>\n"))}  </body>\n</html>\n";

    /// <summary>The page's stylesheet and picture loaded, and on Windows neither address of the picture beside the open
    /// folder.</summary>
    private static void CheckLoaded(NativeWebView web)
    {
        Ui.WaitFor(() => Script(web, "getComputedStyle(document.querySelector('h1')).color")?.Trim('"') == "rgb(0, 128, 0)",
            () => $"the heading coloured by the page's own stylesheet, not {Script(web, "getComputedStyle(document.querySelector('h1')).color")}");
        Ui.WaitFor(() => Script(web, "String(Array.from(document.images).every(i => i.complete))")?.Trim('"') == "true", () => "the page's pictures to load or fail");
        Assert.Equal("1", Script(web, "document.getElementById('inside').naturalWidth")?.Trim('"'));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("0", Script(web, "document.getElementById('up').naturalWidth")?.Trim('"'));
            Assert.Equal("0", Script(web, "document.getElementById('whole').naturalWidth")?.Trim('"'));
        }
    }

    /// <summary>The page's answer to a script, the window's loop running while it is asked.</summary>
    private static string? Script(NativeWebView web, string script) => Ui.Await(web.InvokeScript(script), $"the page's answer to {script}");

    /// <summary>How far down the page is scrolled once <paramref name="first"/> has run in it; NaN where it cannot say.</summary>
    private static double ScrolledTo(NativeWebView web, string first = "") => PagePlace.Of(Script(web, first + PagePlace.Read))?.Y ?? double.NaN;
}
