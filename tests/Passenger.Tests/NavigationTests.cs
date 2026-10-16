using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Getting to a file or a line from the keyboard, through the window.</summary>
public sealed class NavigationTests : IDisposable
{
    private readonly string folder = TestFolders.Create("nav-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Go_to_file_finds_a_file_by_its_camel_humps_and_enter_opens_it()
    {
        Write("src/Editor/CodeEditor.cs", "class CodeEditor { }");
        Write("src/Editor/Commands.cs", "class Commands { }");
        Write("notes/Changes.txt", "nothing");
        MainWindow window = Open();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        Assert.True(window.IsPopupOpen);
        window.KeyTextInput("CE");
        Ui.Settle();
        Ui.Press(window, "ENTER");

        Assert.False(window.IsPopupOpen);
        Assert.Equal("CodeEditor.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_file_with_nothing_typed_offers_the_files_opened_before_this_one()
    {
        Write("First.cs", "class First { }");
        Write("Second.cs", "class Second { }");
        MainWindow window = Open();
        window.OpenFile("First.cs");
        window.OpenFile("Second.cs");
        Ui.Settle();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        Ui.Press(window, "ENTER");

        Assert.Equal("First.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_line_takes_a_line_and_column_counted_from_one()
    {
        Write("Lines.txt", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line number {i}")));
        MainWindow window = Open();
        window.OpenFile("Lines.txt");
        Ui.Settle();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToLine));
        window.KeyTextInput("25:6");
        Ui.Settle();
        Ui.Press(window, "ENTER");

        Assert.False(window.IsPopupOpen);
        Assert.Equal(new TextPosition(24, 5), window.EditorArea.CodeEditor.CaretPosition);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Navigation_popups_keep_their_shadow_and_long_file_reading_inside_the_resized_window_without_losing_input(bool light)
    {
        string text = string.Join('\n', Enumerable.Range(1, 20).Select(number => $"line number {number}"));
        string name = "LongResult with deliberately repeated words that leave its filename wider than a navigation popup.txt";
        Write("Anchor.txt", text);
        Write(name, text);
        Write("A deliberately long nested folder for actual navigation result reading/" + name, text);
        MainWindow window = Open();
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        window.OpenFile("Anchor.txt");
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(7, 2));
        Ui.Run(window, AppActions.GoToFile);
        GoToFilePopup files = Assert.IsType<GoToFilePopup>(window.Popup);
        TextBox query = files.GetVisualDescendants().OfType<TextBox>().Single();
        ListBox results = files.GetVisualDescendants().OfType<ListBox>().Single();
        window.KeyTextInput("LongResult");
        Ui.Settle();
        Assert.Equal(2, results.ItemCount);
        Ui.Press(window, "DOWN");
        FileHit selected = Assert.IsType<FileHit>(results.SelectedItem);
        Ui.Press(window, "shift LEFT");
        (int Start, int End, int Caret) selection = (query.SelectionStart, query.SelectionEnd, query.CaretIndex);
        double requestedFrameWidth = files.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("popup")).Bounds.Width;
        foreach (double width in new double[] { 1200, 683, 640, 1200, 640 })
        {
            window.Width = width;
            Ui.Settle();
            CheckPopup(files);
            if (width == 1200)
            {
                Assert.Equal(requestedFrameWidth, files.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("popup")).Bounds.Width);
            }
            Assert.True(query.IsFocused, "Resizing the popup took the query's keyboard focus");
            Assert.Equal("LongResult", query.Text);
            Assert.Equal(selection, (query.SelectionStart, query.SelectionEnd, query.CaretIndex));
            Assert.Same(selected, results.SelectedItem);
            foreach (ListBoxItem item in results.GetVisualDescendants().OfType<ListBoxItem>())
            {
                FileHit hit = Assert.IsType<FileHit>(item.DataContext);
                Grid row = item.GetVisualDescendants().OfType<Grid>().Single(grid => ToolTip.GetTip(grid) is string);
                Assert.Equal(hit.FullPath, ToolTip.GetTip(row));
                foreach (TextBlock label in row.Children.OfType<TextBlock>().Where(label => !string.IsNullOrEmpty(label.Text)))
                {
                    Point at = label.TranslatePoint(default, results)!.Value;
                    Assert.True(at.X >= 0 && at.X + label.Bounds.Width <= results.Bounds.Width + 1,
                        $"The popup's '{label.Text}' label escapes its actual list at {width}px");
                    Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, "A long filename or folder must ellipsize inside its own column");
                }
            }
            UiAudit.Capture(window, $"navigation-file-{width:0}-{(light ? "light" : "dark")}");
        }
        ListBoxItem selectedItem = results.GetVisualDescendants().OfType<ListBoxItem>().Single(item => ReferenceEquals(item.DataContext, selected));
        Grid selectedRow = selectedItem.GetVisualDescendants().OfType<Grid>().Single(grid => ToolTip.GetTip(grid) is string);
        TextBlock selectedName = selectedRow.Children.OfType<TextBlock>().Single(label => label.Text == selected.Name);
        window.MouseMove(selectedName.TranslatePoint(new Point(selectedName.Bounds.Width / 2, selectedName.Bounds.Height / 2), window)!.Value);
        Passenger.App.Screenshot.Pump(TimeSpan.FromMilliseconds(800));
        Ui.Settle();
        Assert.True(ToolTip.GetIsOpen(selectedRow), "Hovering the ellipsized file must open its complete path for reading");
        UiAudit.Capture(window, $"navigation-file-tooltip-640-{(light ? "light" : "dark")}");
        Ui.Press(window, "ESCAPE");
        Assert.False(window.IsPopupOpen);
        Assert.True(window.EditorArea.CodeEditor.IsFocused);
        Assert.Equal(new TextPosition(7, 2), window.EditorArea.CodeEditor.CaretPosition);

        Ui.Run(window, AppActions.GoToFile);
        files = Assert.IsType<GoToFilePopup>(window.Popup);
        window.KeyTextInput("LongResult");
        Ui.Settle();
        results = files.GetVisualDescendants().OfType<ListBox>().Single();
        selectedItem = results.GetVisualDescendants().OfType<ListBoxItem>().Last();
        selected = Assert.IsType<FileHit>(selectedItem.DataContext);
        Point choose = selectedItem.TranslatePoint(new Point(selectedItem.Bounds.Width / 2, selectedItem.Bounds.Height / 2), window)!.Value;
        window.MouseDown(choose, MouseButton.Left);
        window.MouseUp(choose, MouseButton.Left);
        Ui.Settle();
        Assert.Same(selected, results.SelectedItem);
        Assert.True(results.IsKeyboardFocusWithin, "The real pointer selection did not give the results their keys");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen);
        Assert.Equal(selected.FullPath, window.EditorArea.Active?.Path);
        Assert.True(window.EditorArea.CodeEditor.IsFocused);

        foreach (string action in new[] { AppActions.GoToLine, AppActions.FindAction })
        {
            Ui.Run(window, action);
            Control popup = window.Popup!;
            TextBox input = popup.GetVisualDescendants().OfType<TextBox>().Single();
            window.KeyTextInput(action == AppActions.GoToLine ? "9:3" : "Go to File");
            foreach (double width in new double[] { 640, 683, 1200, 640 })
            {
                window.Width = width;
                Ui.Settle();
                CheckPopup(popup);
                Assert.True(input.IsFocused, "A resized navigation query lost its focus");
                Assert.Equal(action == AppActions.GoToLine ? "9:3" : "Go to File", input.Text);
                UiAudit.Capture(window, $"navigation-{action}-{width:0}-{(light ? "light" : "dark")}");
            }
            if (action == AppActions.GoToLine)
            {
                Ui.Press(window, "ENTER");
                Assert.Equal(new TextPosition(8, 2), window.EditorArea.CodeEditor.CaretPosition);
            }
            else
            {
                Point outside = new(window.Bounds.Width - 80, window.Bounds.Height - 100);
                window.MouseDown(outside, MouseButton.Left);
                window.MouseUp(outside, MouseButton.Left);
                Ui.Settle();
            }
            Assert.False(window.IsPopupOpen);
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "Dismissing navigation must give the editor its keys back");
        }

        window.OpenFile("Anchor.txt");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(19, 2));
        Ui.Run(window, AppActions.EditBreakpoint);
        Control anchored = window.Popup!;
        TextBox condition = anchored.GetVisualDescendants().OfType<TextBox>().Single(input => input.IsFocused);
        window.KeyTextInput("line == 20");
        foreach (double width in new double[] { 1200, 640, 683, 640 })
        {
            window.Width = width;
            Ui.Settle();
            CheckPopup(anchored);
            Assert.True(condition.IsFocused);
            Assert.Equal("line == 20", condition.Text);
            UiAudit.Capture(window, $"navigation-anchored-breakpoint-{width:0}-{(light ? "light" : "dark")}");
        }
        Ui.Press(window, "ESCAPE");
        Assert.False(window.IsPopupOpen);
        Assert.True(window.EditorArea.CodeEditor.IsFocused);

        Ui.Run(window, AppActions.OpenInNewWindow);
        Window detached = Assert.Single(window.EditorArea.Windows).Window;
        detached.SetRenderScaling(2);
        detached.RequestedThemeVariant = window.RequestedThemeVariant;
        detached.Height = 800;
        Ui.Run(detached, AppActions.GoToFile);
        Control detachedPopup = window.Popup!;
        TextBox detachedQuery = detachedPopup.GetVisualDescendants().OfType<TextBox>().Single();
        detached.KeyTextInput("LongResult");
        foreach (double width in new double[] { 640, 1200, 683, 640 })
        {
            detached.Width = width;
            Ui.Settle();
            Assert.Same(detached, TopLevel.GetTopLevel(detachedPopup));
            CheckPopup(detachedPopup, detached);
            if (width == 1200)
            {
                Assert.Equal(requestedFrameWidth, detachedPopup.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("popup")).Bounds.Width);
            }
            Assert.True(detachedQuery.IsFocused);
            Assert.Equal("LongResult", detachedQuery.Text);
            UiAudit.Capture(detached, $"navigation-detached-file-{width:0}-{(light ? "light" : "dark")}");
        }
        detached.Close();
        Ui.Settle();
        Assert.False(window.IsPopupOpen, "Closing the editor window left its popup open");
        Ui.Run(window, AppActions.GoToFile);
        Ui.Settle();
        Assert.Same(window, TopLevel.GetTopLevel(window.Popup));
        CheckPopup(window.Popup!);
        Ui.Press(window, "ESCAPE");
        window.Close();

        void CheckPopup(Control popup, Window? owner = null)
        {
            owner ??= window;
            Border frame = popup.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("popup"));
            Point at = frame.TranslatePoint(default, owner)!.Value;
            Assert.True(at.X >= 40 && at.X + frame.Bounds.Width <= owner.Bounds.Width - 40 + 1,
                $"The popup frame [{at.X}, {at.X + frame.Bounds.Width}] leaves no room for its 32px shadow and 8px edge at {owner.Bounds.Width}px");
            Assert.True(at.Y >= 32 && at.Y + frame.Bounds.Height <= owner.Bounds.Height - 48 + 1,
                "The popup leaves no room for its vertical shadow and edge");
            Assert.True(frame.BoxShadow.Count > 0, "A navigation popup must retain the shared frame shadow");
        }
    }

    [AvaloniaFact]
    public void The_explorers_shortcut_gives_it_the_keys_on_the_open_files_row()
    {
        Write("One.cs", "class One { }");
        Write("Two.cs", "class Two { }");
        MainWindow window = Open();
        window.OpenFile("Two.cs");
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsFocused);

        Ui.Run(window, AppActions.FocusExplorer);
        Assert.False(window.EditorArea.CodeEditor.IsFocused, "The Explorer's shortcut left the keys with the editor");
        // The keys go on from the open file's row, as in Rider's Project view, and Enter opens the row they reach.
        Ui.Press(window, "UP");
        Assert.Equal("One.cs", window.ExplorerView.Selected?.Name);
        Ui.Press(window, "ENTER");
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void The_explorer_chooses_the_file_shown_as_select_next_tab_or_a_click_on_a_tab_shows_another()
    {
        Write("One.cs", "class One { }");
        Write("Two.cs", "class Two { }");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        window.OpenFile("Two.cs");
        Ui.Settle();
        Assert.Equal("Two.cs", window.ExplorerView.Selected?.Name);

        Ui.Run(window, AppActions.NextTab);
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        Assert.True(window.ExplorerView.Selected?.Name == "One.cs", $"Select Next Tab showed One.cs and left the Explorer on {window.ExplorerView.Selected?.Name}");

        Control two = window.EditorArea.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Two.cs" && t.Classes.Contains("tabName"));
        Point at = two.TranslatePoint(new Point(two.Bounds.Width / 2, two.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.Equal("Two.cs", window.EditorArea.Active?.Name);
        Assert.True(window.ExplorerView.Selected?.Name == "Two.cs", $"A click on Two.cs's tab left the Explorer on {window.ExplorerView.Selected?.Name}");
        window.Close();
    }

    [AvaloniaFact]
    public void Focus_editor_gives_the_keys_back_to_the_file_shown()
    {
        Write("One.cs", "class One { }");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.False(window.EditorArea.CodeEditor.IsFocused);

        // As ide_action runs it, or a shortcut the person gives it.
        Assert.True(window.Perform(AppActions.FocusEditor));
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsFocused, "Focus Editor left the keys in the Explorer");
        window.KeyTextInput("sealed ");
        Assert.Equal("sealed class One { }", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        window.Close();
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }
}
