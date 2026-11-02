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
/// addresses, again once typing pauses and not before; off until the strip's toggle turns it on; and a Linux without
/// WebKitGTK 4.1 saying so in the page's place.</summary>
public sealed class HtmlPreviewTests : IDisposable
{
    private const string Page = "<!doctype html>\n<html>\n  <head><link rel=\"stylesheet\" href=\"site.css\"></head>\n  <body><h1>Hello</h1></body>\n</html>\n";

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
}
