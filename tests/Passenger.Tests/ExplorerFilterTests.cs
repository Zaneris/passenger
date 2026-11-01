using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Explorer's filter: Find's keys give it the keys, its text narrows the tree to the files whose names hold
/// it with their folders, in the Solution view and the Files view, Down takes the keys into the tree, and Escape empties it
/// and gives the tree its keys back, the rows open again as they were.</summary>
public sealed class ExplorerFilterTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("explorer-filter-"));

    public ExplorerFilterTests()
    {
        TestFolders.Write(folder, "Shop.App/Shop.App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Shop.App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        TestFolders.Write(folder, "Shop.App/Models/Order.cs", "namespace Shop.App.Models;\n\npublic class Order\n{\n}\n");
        TestFolders.Write(folder, "Shop.App/Models/OrderLine.cs", "namespace Shop.App.Models;\n\npublic class OrderLine\n{\n}\n");
        TestFolders.Write(folder, "Shop.App/Models/Customer.cs", "namespace Shop.App.Models;\n\npublic class Customer\n{\n}\n");
        TestFolders.Write(folder, "Shop.Core/Shop.Core.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Shop.Core/Catalog/Product.cs", "namespace Shop.Core.Catalog;\n\npublic class Product\n{\n}\n");
        TestFolders.Write(folder, "Shop.Core/Catalog/Orders.cs", "namespace Shop.Core.Catalog;\n\npublic static class Orders\n{\n}\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "README.md", "# Shop\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_filter_shows_the_files_named_with_their_folders_opens_one_and_escape_gives_back_the_tree_as_it_was()
    {
        MainWindow window = Open();
        ExplorerView explorer = window.ExplorerView;
        // Order.cs's folders open, as a person left them, and Shop.Core closed.
        Row(window, At("Shop.App/Models/Order.cs"));
        if (explorer.Tree.Rows.First(r => r is { Kind: NodeKind.Project, Name: "Shop.Core" }) is { IsExpanded: true } core)
        {
            explorer.Tree.Collapse(core);
        }
        Ui.ClickRow(window, Row(window, At("Shop.App/Program.cs")));
        string[] before = Shown(explorer);
        Assert.DoesNotContain("    Catalog", before);

        Ui.Run(window, AppActions.Find);
        Assert.True(Filter(window).IsKeyboardFocusWithin, $"Find in the Explorer gave the keys to {window.FocusManager?.GetFocusedElement()?.GetType().Name}, not its filter");
        window.KeyTextInput("order");
        Ui.Settle();
        Assert.Equal(["Shop", "  Shop.App", "    Models", "      Order.cs", "      OrderLine.cs", "  Shop.Core", "    Catalog", "      Orders.cs"], Shown(explorer));

        Ui.Press(window, "DOWN");
        Assert.True(explorer.Rows().IsKeyboardFocusWithin && explorer.Selected?.Name == "Order.cs",
            $"Down in the filter left {explorer.Selected?.Name} chosen and the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, where the tree takes them on the first file");
        Ui.Press(window, "ENTER");
        Assert.Equal(At("Shop.App/Models/Order.cs"), window.EditorArea.Active?.Path);

        Ui.Click(Filter(window));
        Ui.Press(window, "ESCAPE");
        Assert.True(explorer.FilterText.Length == 0 && explorer.Rows().IsKeyboardFocusWithin,
            $"Escape in the filter left \"{explorer.FilterText}\" in it and the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        Assert.Equal(before, Shown(explorer));
        Assert.Equal("Order.cs", explorer.Selected?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void The_files_view_is_filtered_too_and_a_filter_naming_no_file_says_so()
    {
        MainWindow window = Open();
        ExplorerView explorer = window.ExplorerView;
        Ui.Click(window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Switch between the solution", StringComparison.Ordinal)));
        Assert.False(explorer.Tree.IsSolutionView, "The view switch left the Solution view shown");
        string root = Path.GetFileName(folder);

        Ui.Click(Filter(window));
        window.KeyTextInput("readme");
        Ui.Settle();
        Assert.Equal([root, "  README.md"], Shown(explorer));
        Ui.Press(window, "ESCAPE");
        Ui.Click(Filter(window));
        window.KeyTextInput("product");
        Ui.Settle();
        Assert.Equal([root, "  Shop.Core", "    Catalog", "      Product.cs"], Shown(explorer));

        window.KeyTextInput("zzz");
        Ui.Settle();
        TextBlock hint = explorer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Hint");
        Assert.True(hint.IsEffectivelyVisible && hint.Text == "No file matches", $"A filter naming no file shows the hint {(hint.IsEffectivelyVisible ? hint.Text : "hidden")}");
        Ui.Press(window, "ESCAPE");
        Assert.False(hint.IsEffectivelyVisible, "The hint that no file matches stayed once the filter was emptied");
        window.Close();
    }

    private static TextBox Filter(MainWindow window) =>
        window.ExplorerView.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText?.EndsWith("to filter", StringComparison.Ordinal) == true);

    /// <summary>The rows as the Explorer shows them, each by its name indented two spaces a level.</summary>
    private static string[] Shown(ExplorerView explorer) => [.. explorer.Tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name)];

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
        Ui.WaitFor(() => window.ExplorerView.Tree.IsSolutionView && window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project)
            && window.FileIndex.Count > 0 && !window.FileIndex.IsWalking, "the Solution view's projects and the folder's files indexed");
        return window;
    }
}
