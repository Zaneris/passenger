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
        folder = PathNames.Canonical(folders.Take("livetree-", made =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("LiveTreeApp")))
            {
                File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
            }
            string project = Path.Combine(made, "LiveTreeApp.csproj");
            File.WriteAllText(project, AvaloniaApp.HeadlessProject());
            return project;
        }));
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_debugged_avalonia_app_shows_its_controls_and_where_each_value_and_style_comes_from()
    {
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Assert.True(window.DockHost.IsShown("liveTree"), "The Live Visual Tree did not open as the app's windows came up");
        Assert.Equal("MainWindow", tree.Rows[0].Element.Type);
        Assert.Contains(tree.Rows, r => r.Element is { Type: "StackPanel", Name: "Panel" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "TextBlock", Name: "Greeting" } && r.Element.Classes.Contains("title"));
        Assert.Contains(tree.Rows, r => r.Element is { Type: "Button", Name: "Save" });

        // A button's background comes from Fluent's control theme for Button, named by its selector and its theme.
        ElementDetails save = Choose(window, "Save");
        PropertyValue background = save.Properties.Single(p => p.Name == "Background");
        Assert.True(background.Source == ValueSources.Theme && background.Detail == "Button (FluentTheme)",
            $"Save's Background came from {background.Source} {background.Detail}, not Fluent's theme for Button");
        Assert.Contains(tree.PropertyRows, p => p.Name == "Background" && p.Source == ValueSources.Theme);

        // A style of the window's that does not apply says which part of its selector fails, and links to its selector.
        StyleReport danger = save.Styles.Single(s => s.Selector == "Button.danger");
        Assert.False(danger.Applies, "Button.danger applied to a button without the class");
        Assert.Equal("`.danger` is not in its Classes", danger.WhyNot);
        Assert.Equal(Line("MainWindow.axaml", "Selector=\"Button.danger\""), danger.Place?.Line);
        Assert.Contains(danger.Setters, s => s.Property == "Foreground" && s.State == SetterStates.Inactive);
        StyleReport pressed = save.Styles.Single(s => s.Selector == "Button:pressed");
        Assert.Equal("`:pressed` is not active on it", pressed.WhyNot);

        // A value set on the element is local, and the theme's setter for it says what overrides it.
        ElementDetails delete = Choose(window, "Delete");
        Assert.Equal(ValueSources.Local, delete.Properties.Single(p => p.Name == "Background").Source);
        SetterReport themes = delete.Styles.Single(s => s.Kind == StyleKinds.Theme).Setters.Single(s => s.Property == "Background");
        Assert.True(themes is { State: SetterStates.Overridden, By: "its local value" }, $"The theme's Background setter was {themes.State} by {themes.By}");

        // A style of the application's gives the title its size, from its setter's line in App.axaml.
        ElementDetails greeting = Choose(window, "Greeting");
        PropertyValue size = greeting.Properties.Single(p => p.Name == "FontSize");
        Assert.Equal(ValueSources.Style, size.Source);
        Assert.Equal("TextBlock.title (Application.Styles)", size.Detail);
        Assert.Equal((Path.Combine(folder, "App.axaml"), Line("App.axaml", "Property=\"FontSize\"")), (PathNames.Canonical(size.Place!.File), size.Place.Line));

        // Go to source lands on the element in its XAML.
        Choose(window, "Delete");
        Click(tree.GoToSource!);
        string xaml = Path.Combine(folder, "MainWindow.axaml");
        int deleteLine = Line("MainWindow.axaml", "x:Name=\"Delete\"") - 1;
        Assert.Equal(xaml, window.EditorArea.Active?.Path);
        Assert.Equal(deleteLine, window.EditorArea.CodeEditor.CaretPosition.Line);
        Assert.StartsWith("Button", File.ReadAllLines(xaml)[deleteLine][window.EditorArea.CodeEditor.CaretPosition.Column..], StringComparison.Ordinal);

        // And from a style to its selector's line, on the Styles page.
        tree.ShowPage(LiveTreePage.Styles);
        Ui.Settle();
        Control block = tree.StyleBlocks.First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Button.danger"));
        Click(block.GetLogicalDescendants().OfType<Button>().First());
        Assert.Equal(xaml, window.EditorArea.Active?.Path);
        Assert.Equal(Line("MainWindow.axaml", "Selector=\"Button.danger\"") - 1, window.EditorArea.CodeEditor.CaretPosition.Line);

        long? selected = tree.Details?.Id;
        window.Width = 683;
        window.DockHost.Show("explorer");
        Ui.Settle();
        Assert.False(tree.IsEffectivelyVisible, "The Live Visual Tree must give the editor room when the other side is brought forward at half width");
        Assert.True(window.DockHost["liveTree"]!.IsShown, "The temporarily collapsed Live Visual Tree must remain in the remembered layout");
        window.Width = 1400;
        Ui.Settle();
        Assert.True(tree.IsEffectivelyVisible, "The Live Visual Tree must return when the window widens");
        Assert.Equal(selected, tree.Details?.Id);

        // Paused, the app answers nothing: the tree stays as it last ran and says so, until the app runs again.
        Ui.Run(window, AppActions.Pause);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the app to pause");
        Ui.WaitFor(() => tree.BannerText is not null, "the tree to say the app is paused");
        Assert.Contains("is paused", tree.BannerText, StringComparison.Ordinal);
        Assert.Contains(tree.Rows, r => r.Element.Name == "Delete");
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => tree.BannerText is null, "the tree to be live again once the app runs");

        Stop(window);
        Assert.Empty(tree.Rows);
        window.Close();
    }

    [AvaloniaFact]
    public void Select_element_chooses_the_control_clicked_in_the_app()
    {
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Box save = Choose(window, "Save").Layout.InWindow!;
        Choose(window, "Greeting");

        ClickOn(window, tree.SelectElement);
        Assert.True(tree.SelectElement.IsChecked, "Select Element did not stay on");
        // Inside Save's border, short of its text: the button's own presenter, a part of its template.
        Pointer(window, $"click {save.X + 3},{save.Y + 3}");
        Ui.WaitFor(() => tree.SelectElement.IsChecked == false && tree.Details?.TemplatedParent == "Button#Save",
            "the control clicked in the app to be chosen in the tree", TimeSpan.FromSeconds(30));
        Assert.Equal("ContentPresenter", tree.Details!.Type);
        Assert.Contains(tree.Details.Styles, s => s.Kind == StyleKinds.TemplatedParentTheme && s.Selector.StartsWith("Button:pointerover", StringComparison.Ordinal));

        Stop(window);
        window.Close();
    }

    /// <summary>The folder opened in a window and its app launched by Debug, its tree shown.</summary>
    private MainWindow Debugged()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(Ui.Await(window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4)));
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

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
    }

    /// <summary>A click on a control of the window, where it is drawn.</summary>
    private static void ClickOn(MainWindow window, Control control)
    {
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
    }

    /// <summary>What the person's pointer does in the app, sent as a line of its input from the Debug window's console.</summary>
    private static void Pointer(MainWindow window, string line)
    {
        window.ShowToolWindow("debug");
        window.BottomPanel.Debug.ShowTab(console: true);
        Ui.Settle();
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Debug.Console;
        Point inside = console.Editor.TranslatePoint(new Point(console.Editor.Bounds.Width / 2, console.Editor.Bounds.Height / 2), window)!.Value;
        window.MouseDown(inside, Avalonia.Input.MouseButton.Left);
        window.MouseUp(inside, Avalonia.Input.MouseButton.Left);
        window.KeyTextInput(line);
        Ui.Press(window, "ENTER");
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Ended, "the app's inspector to end with it");
    }

    /// <summary>The line (from one) of a file of the app holding some text, so a test names what it means rather than a number.</summary>
    private int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(Path.Combine(folder, file)), l => l.Contains(text, StringComparison.Ordinal)) + 1;
}
