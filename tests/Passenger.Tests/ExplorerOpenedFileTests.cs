using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Explorer and the file shown: Select Opened File chooses it again, by its key or its header's button,
/// however the rows were chosen since, in the Files view where the solution does not hold it; Always Select Opened File,
/// in its options, has it follow the file shown or keep its row; Open Files with Single Click opens a file on a plain
/// click, the keys kept on its row; Settings › Explorer holds the same options.</summary>
public sealed class ExplorerOpenedFileTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("explorer-opened-"));

    public ExplorerOpenedFileTests()
    {
        TestFolders.Write(folder, "Shop.App/Shop.App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Shop.App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        TestFolders.Write(folder, "Shop.App/Models/Order.cs", "namespace Shop.App.Models;\n\npublic class Order\n{\n}\n");
        TestFolders.Write(folder, "Shop.Core/Shop.Core.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Shop.Core/Catalog/Product.cs", "namespace Shop.Core.Catalog;\n\npublic class Product\n{\n}\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "README.md", "# Shop\n");
        TestFolders.Write(folder, "Shop.App/appsettings.json", "{\n}\n");
        TestFolders.Write(folder, "Shop.App/appsettings.Development.json", "{\n}\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Select_opened_file_chooses_the_file_shown_again_by_its_key_with_the_keys_and_by_its_button_leaving_them()
    {
        string order = At("Shop.App/Models/Order.cs");
        MainWindow window = Open();
        window.OpenFile(order);
        Ui.Settle();
        Assert.True(Chosen(window) == order, $"Opening Order.cs left the Explorer on {Chosen(window)}");

        Ui.ClickRow(window, Row(window, At("Shop.App/Program.cs")));
        Ui.Click(HeaderButton(window, AppActions.CollapseAll));
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Path == order);
        Assert.True(window.Perform(AppActions.FocusEditor));
        Ui.Settle();
        Ui.Run(window, AppActions.SelectOpenedFile);
        Assert.True(Chosen(window) == order && window.ExplorerView.Rows().IsKeyboardFocusWithin,
            $"Select Opened File's key left {Chosen(window)} chosen, the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, where Order.cs's row takes them");

        Ui.ClickRow(window, Row(window, At("Shop.Core/Catalog/Product.cs")));
        Assert.True(window.Perform(AppActions.FocusEditor));
        Ui.Settle();
        Button button = HeaderButton(window, AppActions.SelectOpenedFile);
        Assert.StartsWith("Select Opened File", ToolTip.GetTip(button) as string, StringComparison.Ordinal);
        Ui.Click(button);
        Assert.True(Chosen(window) == order && window.EditorArea.CodeEditor.IsKeyboardFocusWithin,
            $"Select Opened File's button left {Chosen(window)} chosen and the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, where Order.cs is chosen and the editor keeps them");

        string readme = At("README.md");
        window.OpenFile(readme);
        Ui.Settle();
        Assert.True(window.ExplorerView.Tree.IsSolutionView, "Opening a file the solution does not hold switched the Explorer's view by itself");
        Ui.Run(window, AppActions.SelectOpenedFile);
        Assert.True(!window.ExplorerView.Tree.IsSolutionView && Chosen(window) == readme,
            $"Select Opened File on README.md, which only the folder holds, left the {(window.ExplorerView.Tree.IsSolutionView ? "Solution" : "Files")} view on {Chosen(window)}");
        window.Close();
    }

    [AvaloniaFact]
    public void With_always_select_opened_file_off_the_explorer_keeps_its_row_and_turned_on_it_chooses_the_file_shown()
    {
        string program = At("Shop.App/Program.cs");
        string order = At("Shop.App/Models/Order.cs");
        string product = At("Shop.Core/Catalog/Product.cs");
        MainWindow window = Open();
        try
        {
            window.OpenFile(program);
            window.OpenFile(order);
            Ui.Settle();
            Assert.True(Chosen(window) == order, $"Opening Order.cs left the Explorer on {Chosen(window)}");

            ChooseOption(window, "Always Select Opened File");
            Assert.False(window.Settings.AlwaysSelectOpenedFile, "Always Select Opened File in the Explorer's options left it on");
            Ui.Run(window, AppActions.NextTab);
            window.OpenFile(product);
            Ui.Settle();
            Assert.True(window.EditorArea.Active?.Path == product && Chosen(window) == order,
                $"With Always Select Opened File off, showing Program.cs and opening Product.cs moved the Explorer to {Chosen(window)}");

            ChooseOption(window, "Always Select Opened File");
            Assert.True(window.Settings.AlwaysSelectOpenedFile && Chosen(window) == product,
                $"Always Select Opened File turned on again left the Explorer on {Chosen(window)}, where the file shown is Product.cs");
        }
        finally
        {
            window.ApplySettings(window.Settings with { AlwaysSelectOpenedFile = true });
            window.Close();
        }
    }

    [AvaloniaFact]
    public void With_open_files_with_single_click_a_plain_click_opens_a_file_keeping_the_keys_and_no_other_click_does()
    {
        string program = At("Shop.App/Program.cs");
        string order = At("Shop.App/Models/Order.cs");
        string product = At("Shop.Core/Catalog/Product.cs");
        string settings = At("Shop.App/appsettings.json");
        MainWindow window = Open();
        try
        {
            window.OpenFile(program);
            Ui.Settle();
            Ui.ClickRow(window, Row(window, order));
            Shown(window, program, "A click on Order.cs with Open Files with Single Click off");

            ChooseOption(window, "Open Files with Single Click");
            Assert.True(window.Settings.OpenFilesWithSingleClick, "Open Files with Single Click in the Explorer's options left it off");
            Ui.Click(Ui.Row(window.ExplorerView, Row(window, order)));
            Assert.True(window.EditorArea.Active?.Path == order && Chosen(window) == order && window.ExplorerView.Rows().IsKeyboardFocusWithin,
                $"A click on Order.cs showed {window.EditorArea.Active?.Name} with {Chosen(window)} chosen and the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, "
                + "where it opens Order.cs and the Explorer keeps the keys on its row");
            Ui.Press(window, "UP");
            Shown(window, order, "Up to the row above Order.cs");

            RawInputModifiers command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            Ui.Click(Ui.Row(window.ExplorerView, Row(window, product)), modifiers: RawInputModifiers.Shift);
            Shown(window, order, "A Shift-click on Product.cs, choosing a range of rows,");
            Ui.Click(Ui.Row(window.ExplorerView, Row(window, product)), modifiers: command);
            Shown(window, order, "A Cmd (Ctrl) click on Product.cs, choosing it beside the rest,");
            ExplorerNode models = Row(window, At("Shop.App/Models"));
            Ui.Click(Ui.Row(window.ExplorerView, models));
            Assert.True(models.IsExpanded, "A click on the Models folder's row closed it");
            Shown(window, order, "A click on the Models folder");
            ExplorerNode nesting = Row(window, settings);
            Ui.Click(Ui.Row(window.ExplorerView, nesting).GetVisualDescendants().OfType<RowGlyphs>().Single(), new Point(9, ListRow.Height / 2));
            Assert.True(nesting.IsExpanded, "A click on appsettings.json's chevron left the file nested under it hidden");
            Shown(window, order, "A click on appsettings.json's chevron");

            Ui.DoubleClick(Ui.Row(window.ExplorerView, Row(window, product)));
            Assert.True(window.EditorArea.Active?.Path == product && window.EditorArea.CodeEditor.IsKeyboardFocusWithin,
                $"A double-click on Product.cs showed {window.EditorArea.Active?.Name}, the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, where the editor takes them");
            Point from = Ui.Centre(Ui.Row(window.ExplorerView, Row(window, order)), window);
            Ui.Drag(window, from, from + new Vector(40, 0), MouseButton.Left, RawInputModifiers.None);
            Assert.True(File.Exists(order), "A drag of Order.cs along its own row moved it");
            Shown(window, product, "A drag of Order.cs");
        }
        finally
        {
            window.ApplySettings(window.Settings with { OpenFilesWithSingleClick = false });
            window.Close();
        }

        static void Shown(MainWindow window, string path, string what) => Assert.True(window.EditorArea.Active?.Path == path,
            $"{what} showed {window.EditorArea.Active?.Name}, where it opens nothing and {Path.GetFileName(path)} stays shown");
    }

    /// <summary>Settings › Explorer holds the Explorer's options, each the value its options menu shows: a search finds
    /// them, one changed in Settings is checked in the menu and works, and one the menu changes is cleared in Settings.</summary>
    [AvaloniaFact]
    public void Settings_explorer_page_holds_the_explorers_options_each_the_value_its_menu_shows()
    {
        const string Follow = "Always select opened file";
        const string Click = "Open files with single click";
        const string Nest = "Show files with the same names as nested";
        string order = At("Shop.App/Models/Order.cs");
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "single click";
            Ui.Settle();
            Assert.Equal([SettingsDialog.ExplorerPage], dialog.Pages);
            dialog.Query = "file nesting";
            Ui.Settle();
            Assert.Equal([SettingsDialog.ExplorerPage], dialog.Pages);
            dialog.Query = string.Empty;
            dialog.Page = SettingsDialog.ExplorerPage;
            Ui.Settle();
            Assert.Equal([Follow, Click, Nest], dialog.Shown);
            Assert.True(Checked(dialog, Follow) && !Checked(dialog, Click) && Checked(dialog, Nest),
                $"Settings › Explorer shows {Follow} {Checked(dialog, Follow)}, {Click} {Checked(dialog, Click)} and {Nest} {Checked(dialog, Nest)}, where they start on, off and on");
            Ui.Click(SettingsTests.Editor<CheckBox>(dialog, Click));
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.OpenFilesWithSingleClick && OptionChecked(window, "Open Files with Single Click"),
                "Open files with single click checked in Settings is not checked in the Explorer's options");
            Ui.Click(Ui.Row(window.ExplorerView, Row(window, order)));
            Assert.True(window.EditorArea.Active?.Path == order, $"A click on Order.cs with single click on from Settings showed {window.EditorArea.Active?.Name}");

            ChooseOption(window, "Always Select Opened File");
            dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Page = SettingsDialog.ExplorerPage;
            Ui.Settle();
            Assert.True(!Checked(dialog, Follow) && Checked(dialog, Click), $"Settings › Explorer shows {Follow} {Checked(dialog, Follow)} after the options menu cleared it");
            dialog.Close();
            Ui.Settle();
        }
        finally
        {
            window.ApplySettings(window.Settings with { AlwaysSelectOpenedFile = true, OpenFilesWithSingleClick = false });
            window.Close();
        }

        static bool Checked(SettingsDialog dialog, string label) => SettingsTests.Editor<CheckBox>(dialog, label).IsChecked == true;
    }

    /// <summary>An item of the Explorer's options in its header, chosen as a person chooses it.</summary>
    internal static void ChooseOption(MainWindow window, string item)
    {
        OpenOptions(window);
        Ui.Choose(window.DockHost.OpenOptions, item);
    }

    /// <summary>Whether an item of the Explorer's options shows checked, its menu opened as a person opens it and closed again.</summary>
    private static bool OptionChecked(MainWindow window, string item)
    {
        OpenOptions(window);
        MenuFlyout menu = window.DockHost.OpenOptions ?? throw new Xunit.Sdk.XunitException("The Explorer's Options opened no menu");
        bool on = menu.Items.OfType<MenuItem>().First(m => m.Header as string == item).IsChecked;
        menu.Hide();
        Ui.Settle();
        return on;
    }

    private static void OpenOptions(MainWindow window)
    {
        Control pane = window.ExplorerView.GetVisualAncestors().OfType<Control>().First(a => a.GetType().Name == "ToolPane");
        Ui.Click(pane.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options"));
    }

    /// <summary>A button of the Explorer's header, by the action it runs.</summary>
    private static Button HeaderButton(MainWindow window, string action) =>
        window.ExplorerView.GetVisualAncestors().OfType<DockPanel>().First(p => p.Classes.Contains(StyleClasses.ToolPane))
            .GetVisualDescendants().OfType<Button>().First(b => b.Name == action);

    private static string? Chosen(MainWindow window) => window.ExplorerView.Selected?.Path;

    /// <summary>Opens the Explorer's folders down to a file, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Row(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path)
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private string At(string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.ExplorerView.Tree.IsSolutionView && window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project), "the Solution view's projects");
        return window;
    }
}
