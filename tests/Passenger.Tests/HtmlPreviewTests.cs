using System.Runtime.InteropServices;
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
/// addresses, again once typing pauses and not before; off until the strip's toggle turns it on; the page kept from
/// going elsewhere, the banner over it opening where it went only when asked; what Git leaves out below its folder
/// refused it; and, where a window is seen, its folder's stylesheet and picture loaded.</summary>
public sealed partial class HtmlPreviewTests : IDisposable
{
    private const string Page = "<!doctype html>\n<html>\n  <head><link rel=\"stylesheet\" href=\"site.css\"></head>\n  <body><h1>Hello</h1></body>\n</html>\n";

    /// <summary>A picture one pixel across.</summary>
    private const string Dot = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("html-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>An HTML page is given its text, served what its folder holds and Git keeps, and kept from going
    /// elsewhere.</summary>
    [AvaloniaFact]
    public void An_html_page_is_served_its_own_folder_alone()
    {
        Flow.StepApart<HtmlPreviewTests>(nameof(An_HTML_page_served_is_refused_what_gitignore_leaves_out_below_its_folder),
            test => test.An_HTML_page_served_is_refused_what_gitignore_leaves_out_below_its_folder());
        OpenWindows.CloseAll();
        Flow.StepApart<HtmlPreviewTests>(nameof(An_HTML_files_page_is_given_its_text_beside_it_once_turned_on_and_again_as_typing_pauses),
            test => test.An_HTML_files_page_is_given_its_text_beside_it_once_turned_on_and_again_as_typing_pauses());
        OpenWindows.CloseAll();
        Flow.StepApart<HtmlPreviewTests>(nameof(An_HTML_page_is_kept_from_going_elsewhere_and_the_banner_over_it_opens_where_it_went_only_when_asked),
            test => test.An_HTML_page_is_kept_from_going_elsewhere_and_the_banner_over_it_opens_where_it_went_only_when_asked());
    }

    /// <summary>Where a window is seen, an HTML page loads its folder's stylesheet and picture as it is
    /// typed.</summary>
    [AvaloniaFact]
    public void An_html_page_draws_in_a_web_view_where_one_is_seen()
    {
        Flow.StepApart<HtmlPreviewTests>(nameof(An_HTML_page_loads_its_folders_stylesheet_and_picture_as_it_is_typed),
            test => test.An_HTML_page_loads_its_folders_stylesheet_and_picture_as_it_is_typed());
    }

    private void An_HTML_files_page_is_given_its_text_beside_it_once_turned_on_and_again_as_typing_pauses()
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
        // Typing's pause held open, so the keys come within it however long the machine takes between them.
        using (preview.HoldTypingOpen())
        {
            window.KeyTextInput(",");
            window.KeyTextInput(" shop");
            Assert.True(preview.Loads == 1, $"The page was given the text {preview.Loads - 1} more times while it was typed");
        }
        Ui.WaitFor(() => preview.Loads == 2, () => $"the page given the text typed once typing paused ({preview.Loads} times)");
        Ui.Settle();
        Assert.True(preview.Loads == 2, $"The page was given the text {preview.Loads - 1} times once typing paused, where once gives it all");
        Assert.Equal(Page.Replace("Hello", "Hello, shop", StringComparison.Ordinal), preview.Given!.Value.Html);

        Ui.Click(group.PreviewToggle);
        Assert.Null(group.Html);
        window.Close();
    }

    private void An_HTML_page_is_kept_from_going_elsewhere_and_the_banner_over_it_opens_where_it_went_only_when_asked()
    {
        string index = Path.Combine(folder, "index.html");
        string other = Path.Combine(folder, "other.html");
        File.WriteAllText(index, Page);
        File.WriteAllText(other, Page);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(index));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        HtmlPreview preview = group.Html ?? throw new Xunit.Sdk.XunitException("The toggle showed no page beside the HTML file");
        if (HtmlPreview.WhyNone() is { } why)
        {
            window.Close();
            Assert.Skip($"This system shows no page: {why}");
        }
        Assert.Null(preview.Refused);
        Assert.False(preview.Leaving(new Uri(index)), "The page was kept from loading itself");

        // A link or the page's own script: the web view stays, and the browser opens only from the banner.
        Uri web = new("https://example.com/next?from=page");
        Assert.True(preview.Leaving(web), $"The page was let go to {web}");
        Ui.Settle();
        Assert.Equal($"The page was kept from going to {web.OriginalString}", preview.Refused);
        Assert.Equal("Open in Browser", preview.Opens);
        Assert.True(window.Browsed.Count == 0, $"The page going to {web} opened {string.Join(", ", window.Browsed)} with nobody asking");
        Ui.Click(preview.OpenLink);
        Assert.True(window.Browsed.LastOrDefault() == web, $"The banner's Open in Browser opened [{string.Join(", ", window.Browsed)}], not {web}");

        // A share's path is never opened, nor asked of the disk.
        Uri share = new("file://host/share/page.html");
        Assert.True(preview.Leaving(share), $"The page was let go to {share}");
        Ui.Settle();
        Assert.Equal($"The page was kept from going to {share.OriginalString}, another machine's or a device's, which is never opened", preview.Refused);
        Assert.Null(preview.Opens);

        // A file of this machine opens in the editor only from the banner, its own page then given with nothing said.
        Assert.True(preview.Leaving(new Uri(other)), $"The page was let go to {other}");
        Ui.Settle();
        Assert.Equal("Open in Editor", preview.Opens);
        Assert.True(window.EditorArea.Active?.Path == index, $"The page going to {other} opened {window.EditorArea.Active?.Path} with nobody asking");
        Ui.Click(preview.OpenLink);
        Ui.WaitFor(() => window.EditorArea.Active?.Path == other, () => $"the banner's Open in Editor to open {other}, not {window.EditorArea.Active?.Path}");
        Ui.WaitFor(() => preview.Given is not null && preview.Refused is null, () => $"the page of {other} given with nothing said over it, not '{preview.Refused}'");
        window.Close();
    }

    private void An_HTML_page_served_is_refused_what_gitignore_leaves_out_below_its_folder()
    {
        string site = Directory.CreateDirectory(Path.Combine(folder, "site")).FullName;
        TestGit.Run(folder, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "secrets.json\nbin/\n");
        string index = Path.Combine(site, "index.html");
        File.WriteAllText(index, Page);
        string secrets = Path.Combine(site, "secrets.json");
        File.WriteAllText(secrets, "{}\n");
        Directory.CreateDirectory(Path.Combine(site, "bin", "Debug"));
        File.WriteAllText(Path.Combine(site, "bin", "Debug", "app.js"), "let a;\n");
        string css = Path.Combine(site, "site.css");
        File.WriteAllText(css, "h1 { color: teal; }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);

        IReadOnlyList<string> leftOut = Ui.Await(window.EditorArea.LeftOutUnder(site), "git's word of what .gitignore leaves out");
        ServedPage served = new(index, Page, leftOut);
        Uri At(string below) => new(served.Address, below);
        Assert.True(served.Answer(At("secrets.json")) == PageAnswer.Forbidden, $"A file .gitignore leaves out was served, of [{string.Join(", ", leftOut)}]");
        Assert.True(served.Answer(At("bin/Debug/app.js")) == PageAnswer.Forbidden, $"A file in a folder .gitignore leaves out was served, of [{string.Join(", ", leftOut)}]");
        Assert.Equal(css, served.Answer(At("site.css")).File);

        string elsewhere = Directory.CreateDirectory(Path.Combine(TestFolders.Create("html-"), "plain")).FullName;
        Assert.Empty(Ui.Await(window.EditorArea.LeftOutUnder(elsewhere), "git's word of a folder in no repository"));
        window.Close();
    }

    /// <summary>A page in a subfolder, unsaved and as typed, takes a stylesheet from a folder below it and a picture from
    /// beside it. On Windows, where Passenger serves the page's folder, a picture beside the open folder loads by no address,
    /// and one whose name begins with a dot is refused, the banner saying so. Only a web view of the system's loads a page,
    /// so headless this skips, run by hand where Passenger's window is seen.</summary>
    private void An_HTML_page_loads_its_folders_stylesheet_and_picture_as_it_is_typed()
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
        string css = Directory.CreateDirectory(Path.Combine(pages, "css")).FullName;
        string beside = Path.Combine(folder, "beside.png");
        File.WriteAllBytes(beside, Convert.FromBase64String(Dot));
        File.WriteAllBytes(Path.Combine(pages, "dot.png"), Convert.FromBase64String(Dot));
        File.WriteAllBytes(Path.Combine(pages, ".hidden.png"), Convert.FromBase64String(Dot));
        File.WriteAllText(Path.Combine(css, "site.css"), "h1 { color: rgb(0, 128, 0); }\n");
        string index = Path.Combine(pages, "index.html");
        File.WriteAllText(index, $"""
            <!doctype html>
            <html>
              <head><link rel="stylesheet" href="css/site.css"></head>
              <body>
                <h1>Hello</h1>
                <img id="inside" src="dot.png">
                <img id="hidden" src=".hidden.png">
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
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Not served to the page: .hidden.png as its name begins with a dot", preview.Refused);
        }

        Typing.Replace(window, "Hello<", "Hello, shop<");
        Ui.WaitFor(() => preview.Landed == 2, () => $"the page given the edit to load ({preview.Loads} given, {preview.Landed} loaded)");
        Assert.Equal("Hello, shop", Script(web, "document.querySelector('h1').textContent")?.Trim('"'));
        CheckLoaded(web);
        window.Close();
    }

    /// <summary>The page's stylesheet and picture loaded, and on Windows neither address of the picture beside the open
    /// folder, nor the picture whose name begins with a dot.</summary>
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
            Assert.Equal("0", Script(web, "document.getElementById('hidden').naturalWidth")?.Trim('"'));
        }
    }

    /// <summary>What a WKWebView says of itself, asked through the Objective-C runtime.</summary>
    private static partial class ObjC
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        public static bool DrawsBackground(nint web)
        {
            nint key = Send(Class("NSString"), Selector("stringWithUTF8String:"), "drawsBackground");
            return (Send(Send(web, Selector("valueForKey:"), key), Selector("boolValue")) & 0xFF) != 0;
        }

        [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Class(string name);

        [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Selector(string name);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector, nint argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Send(nint receiver, nint selector, string argument);
    }

    /// <summary>The page's answer to a script, the window's loop running while it is asked.</summary>
    private static string? Script(NativeWebView web, string script) => Ui.Await(web.InvokeScript(script), $"the page's answer to {script}");
}
