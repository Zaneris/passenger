using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The Explorer and the file shown: Select Opened File chooses it again, by its key or its header's button,
/// however the rows were chosen since, in the Files view where the solution does not hold it; Always Select Opened File,
/// in its options, has it follow the file shown or keep its row; Open Files with Single Click opens a file on a plain
/// click, the keys kept on its row; Settings › Explorer holds the same options.</summary>
[AboutMacOS]
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

    /// <summary>Select Opened File chooses the file shown by its key or button, Always Select Opened File keeps or
    /// follows the row, a single click opens a file where asked, each keeping the keys where they were, and a
    /// double-click on a project opens its project file, as one flow, each step the check one test made in an instance of
    /// its own.</summary>
    [AvaloniaFact]
    public void The_Explorer_chooses_the_file_shown_or_keeps_its_row_as_its_options_say()
    {
        Flow.StepApart<ExplorerOpenedFileTests>("Select opened file chooses the file shown again by its key with the keys and by its button leaving them", t => t.Select_opened_file_chooses_the_file_shown_again_by_its_key_with_the_keys_and_by_its_button_leaving_them());
        Flow.StepApart<ExplorerOpenedFileTests>("with always select opened file off the explorer keeps its row and turned on it chooses the file shown", t => t.With_always_select_opened_file_off_the_explorer_keeps_its_row_and_turned_on_it_chooses_the_file_shown());
        Flow.StepApart<ExplorerOpenedFileTests>("with open files with single click a plain click opens a file keeping the keys and no other click does", t => t.With_open_files_with_single_click_a_plain_click_opens_a_file_keeping_the_keys_and_no_other_click_does());
        Flow.StepApart<ExplorerOpenedFileTests>("a file with an error waves its name and every row holding it until the error is gone", t => t.A_file_with_an_error_waves_its_name_and_every_row_holding_it_until_the_error_is_gone());
    }

    /// <summary>An error in an open file waves the name of its row and of its folder's, its project's and the solution's,
    /// leaving the rows that hold no error plain, and the error taken away takes every wave with it.</summary>
    private void A_file_with_an_error_waves_its_name_and_every_row_holding_it_until_the_error_is_gone()
    {
        string order = At("Shop.App/Models/Order.cs");
        MainWindow window = Ui.OpenFolder(folder);
        Ui.WaitFor(() => window.ExplorerView.Tree.IsSolutionView && window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project), "the Solution view's projects");
        window.OpenFile(order);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, "the language server");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.PlaceAfter(editor, "public class Order\n{");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int total = \"none\";");
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the error typed marked");
        Ui.Settle();
        string[] waved = [.. Waved(window)];
        Assert.True(waved.SequenceEqual(["Shop", "Shop.App", "Models", "Order.cs"]),
            $"An error in Order.cs waved [{string.Join(", ", waved)}], where its name and its folder's, project's and solution's tell where it is");
        Row(window, order);
        Assert.True(NameOf(window, order).HasErrors, "Order.cs's row holds the error but its name draws no wave");

        Ui.Press(window, "meta Z");
        Ui.WaitFor(() => !document.Markers.Any(m => m.Kind == MarkerKind.Error), "the error undone unmarked");
        Ui.Settle();
        Assert.True(!Waved(window).Any(), $"With its error undone the Explorer still waves [{string.Join(", ", Waved(window))}]");
    }

    /// <summary>The names the Explorer's rows wave, in their order.</summary>
    private static IEnumerable<string> Waved(MainWindow window) => window.ExplorerView.Tree.Rows.Where(r => r.HasErrors).Select(r => r.Name);

    /// <summary>The name drawn on a file's row.</summary>
    private static ErrorName NameOf(MainWindow window, string path) =>
        window.ExplorerView.GetVisualDescendants().OfType<ErrorName>().First(n => n.Text == Path.GetFileName(path));

    private void Select_opened_file_chooses_the_file_shown_again_by_its_key_with_the_keys_and_by_its_button_leaving_them()
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

    private void With_always_select_opened_file_off_the_explorer_keeps_its_row_and_turned_on_it_chooses_the_file_shown()
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

    private void With_open_files_with_single_click_a_plain_click_opens_a_file_keeping_the_keys_and_no_other_click_does()
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
            Point from = Ui.Centre(Ui.Row(window.ExplorerView, Row(window, product)), window);
            Ui.Drag(window, from, from + new Vector(40, 0), MouseButton.Left, RawInputModifiers.None);
            Assert.True(File.Exists(product), "A drag of Product.cs along its own row moved it");
            Shown(window, order, "A drag of Product.cs");

            Ui.DoubleClick(Ui.Row(window.ExplorerView, Row(window, product)));
            Assert.True(window.EditorArea.Active?.Path == product && window.EditorArea.CodeEditor.IsKeyboardFocusWithin,
                $"A double-click on Product.cs showed {window.EditorArea.Active?.Name}, the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, where the editor takes them");
        }
        finally
        {
            window.ApplySettings(window.Settings with { OpenFilesWithSingleClick = false });
            window.Close();
        }

        static void Shown(MainWindow window, string path, string what) => Assert.True(window.EditorArea.Active?.Path == path,
            $"{what} showed {window.EditorArea.Active?.Name}, where it opens nothing and {Path.GetFileName(path)} stays shown");
    }

    /// <summary>A double-click on Shop.Core's row opens Shop.Core.csproj, the row left as it was; with Open a Project's File
    /// on Double-Click off, a double-click expands or collapses it.</summary>
    private void A_double_click_on_a_projects_row_opens_its_project_file_and_with_the_setting_off_expands_it()
    {
        MainWindow window = Open();
        try
        {
            string project = At("Shop.Core/Shop.Core.csproj");
            ExplorerNode row = window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Project && r.Path == project);
            bool expanded = row.IsExpanded;
            Ui.DoubleClick(Ui.Row(window.ExplorerView, row));
            Assert.True(window.EditorArea.Active?.Path == project && row.IsExpanded == expanded,
                $"A double-click on Shop.Core's row showed {window.EditorArea.Active?.Name} and left it {(row.IsExpanded ? "expanded" : "collapsed")}, "
                + $"where it opens Shop.Core.csproj and leaves the row {(expanded ? "expanded" : "collapsed")}");
            window.ApplySettings(window.Settings with { OpenProjectFileOnDoubleClick = false });
            Ui.DoubleClick(Ui.Row(window.ExplorerView, row));
            Assert.True(row.IsExpanded != expanded, $"With the setting off a double-click on Shop.Core's row left it {(row.IsExpanded ? "expanded" : "collapsed")}, where it toggles the row");
        }
        finally
        {
            window.ApplySettings(window.Settings with { OpenProjectFileOnDoubleClick = true });
            window.Close();
        }
    }

    /// <summary>An item of the Explorer's options in its header, chosen as a person chooses it.</summary>
    internal static void ChooseOption(MainWindow window, string item)
    {
        OpenOptions(window);
        Ui.Choose(window.DockHost.OpenOptions, item);
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
