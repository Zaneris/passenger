using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>The Live Visual Tree of an Avalonia app debugged from the window: tests/Debuggees/LiveTreeApp, written into a
/// folder of its own with a project file on the tests' own Avalonia, built and launched by Debug as a person's app is. It
/// runs headless, so nothing shows on the screen, and its standard input stands in for the person's pointer in its window
/// ("click X,Y", typed into the Debug window's console). Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class LiveTreeTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public LiveTreeTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("livetree-", made => AvaloniaApp.Debuggee(made, "LiveTreeApp")));
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The app's controls in the tree, and where each value and style comes from: a button's background from
    /// Fluent's theme by its selector, a style that does not apply saying which part of its selector fails, a local
    /// value and the theme's setter it overrides, an application style's setter at its line; Go to Source on an element
    /// and on a style's selector; the tree kept as it last ran while the app is paused; and Select Element choosing the
    /// control clicked in the app.</summary>
    [AvaloniaFact]
    public void A_debugged_avalonia_app_shows_its_controls_where_each_value_and_style_comes_from_and_the_control_clicked_in_it()
    {
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Assert.True(window.DockHost.IsShown(ToolWindowIds.LiveTree), "The Live Visual Tree did not open as the app's windows came up");
        Assert.Equal("MainWindow", tree.Rows[0].Element.Type);
        Assert.Contains(tree.Rows, r => r.Element is { Type: "StackPanel", Name: "Panel" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "TextBlock", Name: "Greeting" } && r.Element.Classes.Contains("title"));
        Assert.Contains(tree.Rows, r => r.Element is { Type: "Button", Name: "Save" });
        TreeKeysCheck.Press(window, tree.Rows, tree.Rows.First(r => r.Element is { Type: "StackPanel", Name: "Panel" }), "the Live Visual Tree", enterOpens: false);
        Ui.Press(window, "Right");

        ElementDetails save = Choose(window, "Save");
        PropertyValue background = save.Properties.Single(p => p.Name == "Background");
        Assert.True(background.Source == ValueSources.Theme && background.Detail == "Button (FluentTheme)",
            $"Save's Background came from {background.Source} {background.Detail}, not Fluent's theme for Button");
        Assert.Contains(tree.PropertyRows, p => p.Name == "Background" && p.Source == ValueSources.Theme);

        StyleReport danger = save.Styles.Single(s => s.Selector == "Button.danger");
        Assert.False(danger.Applies, "Button.danger applied to a button without the class");
        Assert.Equal("`.danger` is not in its Classes", danger.WhyNot);
        Assert.Equal(Line("MainWindow.axaml", "Selector=\"Button.danger\""), danger.Place?.Line);
        Assert.Contains(danger.Setters, s => s.Property == "Foreground" && s.State == SetterStates.Inactive);
        StyleReport pressed = save.Styles.Single(s => s.Selector == "Button:pressed");
        Assert.Equal("`:pressed` is not active on it", pressed.WhyNot);

        ElementDetails delete = Choose(window, "Delete");
        Assert.Equal(ValueSources.Local, delete.Properties.Single(p => p.Name == "Background").Source);
        SetterReport themes = delete.Styles.Single(s => s.Kind == StyleKinds.Theme).Setters.Single(s => s.Property == "Background");
        Assert.True(themes is { State: SetterStates.Overridden, By: "its local value" }, $"The theme's Background setter was {themes.State} by {themes.By}");

        ElementDetails greeting = Choose(window, "Greeting");
        PropertyValue size = greeting.Properties.Single(p => p.Name == "FontSize");
        Assert.Equal(ValueSources.Style, size.Source);
        Assert.Equal("TextBlock.title (Application.Styles)", size.Detail);
        Assert.Equal((Path.Combine(folder, "App.axaml"), Line("App.axaml", "Property=\"FontSize\"")), (PathNames.Canonical(size.Place!.File), size.Place.Line));

        // Its link kept as the tree is asked again: a copy of it would lose the click.
        Choose(window, "Delete");
        Button link = tree.GoToSource!;
        Ui.Click(tree.GetLogicalDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is "Refresh"));
        Assert.True(tree.GoToSource == link, "Refresh swapped Delete's Go to Source link for a copy, so a click on it pressed as the app answered would do nothing");
        Ui.Click(link);
        string xaml = Path.Combine(folder, "MainWindow.axaml");
        int deleteLine = Line("MainWindow.axaml", "x:Name=\"Delete\"") - 1;
        Assert.Equal(xaml, window.EditorArea.Active?.Path);
        Assert.Equal(deleteLine, window.EditorArea.CodeEditor.CaretPosition.Line);
        Assert.StartsWith("Button", File.ReadAllLines(xaml)[deleteLine][window.EditorArea.CodeEditor.CaretPosition.Column..], StringComparison.Ordinal);

        tree.ShowPage(LiveTreePage.Styles);
        Ui.Settle();
        Control block = tree.StyleBlocks.First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Button.danger"));
        Ui.Click(block.GetLogicalDescendants().OfType<Button>().First());
        Assert.Equal(xaml, window.EditorArea.Active?.Path);
        Assert.Equal(Line("MainWindow.axaml", "Selector=\"Button.danger\"") - 1, window.EditorArea.CodeEditor.CaretPosition.Line);

        Ui.Run(window, AppActions.Pause);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the app to pause");
        Ui.WaitFor(() => tree.BannerText is not null, "the tree to say the app is paused");
        Assert.Contains("is paused", tree.BannerText, StringComparison.Ordinal);
        Assert.Contains(tree.Rows, r => r.Element.Name == "Delete");
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => tree.BannerText is null, "the tree to be live again once the app runs");

        Box saved = Choose(window, "Save").Layout.InWindow!;
        Choose(window, "Greeting");
        Ui.Click(tree.SelectElement);
        Assert.True(tree.SelectElement.IsChecked, "Select Element did not stay on");
        // Inside Save's border, short of its text: the button's own presenter, a part of its template.
        Ui.TypeToDebugged(window, $"click {saved.X + 3},{saved.Y + 3}");
        Ui.WaitFor(() => tree.SelectElement.IsChecked == false && tree.Details?.TemplatedParent == "Button#Save",
            "the control clicked in the app to be chosen in the tree", TimeSpan.FromSeconds(30));
        Assert.Equal("ContentPresenter", tree.Details!.Type);
        Assert.Contains(tree.Details.Styles, s => s.Kind == StyleKinds.TemplatedParentTheme && s.Selector.StartsWith("Button:pointerover", StringComparison.Ordinal));

        Stop(window);
        Assert.Empty(tree.Rows);
        window.Close();
    }

    /// <summary>The app's XAML compiler counts lines as an XML reader does, ending one at a lone '\r' and at no U+0085, U+2028
    /// or U+2029, so below a lone '\r' in the window's tag and a comment holding all four before Delete on Delete's own line,
    /// the places the app reports run two lines ahead of the editor's at Delete and one at the danger style: the details name
    /// each at its own line, the theme's setter the style overrides on Delete names it there too, and Go to Source lands on
    /// it.</summary>
    [AvaloniaFact]
    public void Below_line_breaks_only_an_xml_reader_counts_the_details_name_and_go_to_each_place_at_the_editors_line()
    {
        string xaml = Path.Combine(folder, "MainWindow.axaml");
        string text = File.ReadAllText(xaml)
            .Replace("Title=\"Live Tree App\" Width", "Title=\"Live Tree App\"\r        Width", StringComparison.Ordinal)
            .Replace("<Button x:Name=\"Delete\"", $"<!-- Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd --> <Button x:Name=\"Delete\" Classes=\"danger\"", StringComparison.Ordinal);
        File.WriteAllText(xaml, text);
        (int delete, int button) = EditorPlace(text, "Button x:Name=\"Delete\"");
        int danger = EditorPlace(text, "Selector=\"Button.danger\"").Line;

        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        ElementDetails details = Choose(window, "Delete");
        // The app reports XamlIl's place: past both lone '\r's, at the column after the comment's.
        SourcePlace reported = Ui.Await(window.LiveApp!.DetailsAsync(details.Id), "Delete's details as the app reports them").Source!;
        int named = text.IndexOf("Button x:Name=\"Delete\"", StringComparison.Ordinal);
        Assert.Equal((delete + 3, named - text.LastIndexOf('\r', named)), (reported.Line, reported.Column));
        Assert.Equal((delete + 1, button + 1), (details.Source?.Line, details.Source?.Column));
        Assert.Equal($"MainWindow.axaml:{delete + 1}", (tree.GoToSource?.Content as TextBlock)?.Text);
        Assert.Equal(danger + 1, details.Styles.Single(s => s.Selector == "Button.danger").Place?.Line);

        Ui.Click(tree.GoToSource!);
        Assert.Equal(xaml, window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(delete, button), window.EditorArea.CodeEditor.CaretPosition);
        tree.ShowPage(LiveTreePage.Styles);
        Ui.Settle();
        Control block = tree.StyleBlocks.First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Button.danger"));
        Button link = block.GetLogicalDescendants().OfType<Button>().First();
        Assert.Equal($"MainWindow.axaml:{danger + 1}", (link.Content as TextBlock)?.Text);
        // The button's theme's Foreground, which the danger style overrides, names that style where the editor has it.
        Assert.Contains(tree.StyleBlocks.SelectMany(b => b.GetLogicalDescendants().OfType<TextBlock>()),
            t => t.Text == $"overridden by Button.danger (MainWindow.axaml:{danger + 1})");
        Ui.Click(link);
        Assert.Equal(danger, window.EditorArea.CodeEditor.CaretPosition.Line);

        Stop(window);
        window.Close();
    }

    /// <summary>The folder opened in a window and its app launched by Debug, its tree shown.</summary>
    private MainWindow Debugged()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Delete"),
            $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
        return window;
    }

    /// <summary>Chooses the element of a name in the tree, as a click on its row does, and waits for its details.</summary>
    private static ElementDetails Choose(MainWindow window, string name)
    {
        LiveTreeView tree = window.LiveTree;
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == name);
        Ui.WaitFor(() => tree.Details?.Name == name, $"{name}'s details");
        return tree.Details!;
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Ended, "the app's inspector to end with it");
    }

    /// <summary>Where (line and column from zero) some text begins in a text as the editor counts lines, at each '\n' alone.</summary>
    private static (int Line, int Column) EditorPlace(string text, string find)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        return (text.AsSpan(0, at).Count('\n'), at - text.LastIndexOf('\n', at) - 1);
    }

    /// <summary>The line (from one) of a file of the app holding some text, so a test names what it means rather than a number.</summary>
    private int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(Path.Combine(folder, file)), l => l.Contains(text, StringComparison.Ordinal)) + 1;
}
