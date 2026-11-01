using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Explorer and the file shown: Select Opened File chooses it again, by its key or its header's button,
/// however the rows were chosen since, in the Files view where the solution does not hold it; Always Select Opened File,
/// in its options, has it follow the file shown or keep its row.</summary>
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

    /// <summary>An item of the Explorer's options in its header, chosen as a person chooses it.</summary>
    internal static void ChooseOption(MainWindow window, string item)
    {
        Control pane = window.ExplorerView.GetVisualAncestors().OfType<Control>().First(a => a.GetType().Name == "ToolPane");
        Ui.Click(pane.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options"));
        Ui.Choose(window.DockHost.OpenOptions, item);
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
