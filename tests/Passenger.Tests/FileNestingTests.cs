using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>File nesting in the Solution view: a file's dependents shown under it by Rider's own rules (a Razor
/// component's code-behind and styles, a Razor Page's model, a form's designer and resources, a resource file's code)
/// and by what the project says depends on what, the settings for an environment under appsettings.json; File Nesting's
/// "Show files with the same names as nested" lays the rules' out flat, DependentUpon's staying; and a parent renamed
/// or moved in the Explorer takes its dependents with it, renamed with it, its project's DependentUpon
/// following.</summary>
public sealed class FileNestingTests : IDisposable
{
    private const string Project = $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n\n"
        + "  <ItemGroup>\n    <Compile Update=\"Shapes.Circle.cs\">\n      <DependentUpon>Shapes.cs</DependentUpon>\n    </Compile>\n  </ItemGroup>\n</Project>\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("nesting-"));
    private readonly string trash = TestFolders.Create("trash-");

    public FileNestingTests()
    {
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        Write("Shop/Shop.csproj", Project);
        Write("Shop/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("Shop/appsettings.json", "{}\n");
        Write("Shop/appsettings.Development.json", "{}\n");
        Write("Shop/Shapes.cs", "namespace Shop;\n\npublic static partial class Geometry\n{\n}\n");
        Write("Shop/Shapes.Circle.cs", "namespace Shop;\n\npublic static partial class Geometry\n{\n    public static double Circle(double r) => 3.14 * r * r;\n}\n");
        Write("Shop/Components/Counter.razor", "<p>@count</p>\n");
        Write("Shop/Components/Counter.razor.cs", "namespace Shop.Components;\n\npublic partial class Counter\n{\n    private int count;\n}\n");
        Write("Shop/Components/Counter.razor.css", "p { color: red; }\n");
        Write("Shop/Pages/Index.cshtml", "@page\n@model Shop.Pages.IndexModel\n");
        Write("Shop/Pages/Index.cshtml.cs", "namespace Shop.Pages;\n\npublic class IndexModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel\n{\n}\n");
        Write("Shop/Forms/Form1.cs", "namespace Shop.Forms;\n\npublic partial class Form1\n{\n}\n");
        Write("Shop/Forms/Form1.Designer.cs", "namespace Shop.Forms;\n\npublic partial class Form1\n{\n}\n");
        Write("Shop/Forms/Form1.resx", "<root />\n");
        Write("Shop/Properties/Resources.resx", "<root />\n");
        Write("Shop/Properties/Resources.Designer.cs", "namespace Shop.Properties;\n");
        Directory.CreateDirectory(Path.Combine(folder, "Shop", "Archive"));
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    /// <summary>A path in the project as the Explorer has it, a relative one written with / taking the system's separator.</summary>
    private string At(string relative) => Path.Combine(folder, "Shop", relative.Replace('/', Path.DirectorySeparatorChar));

    [AvaloniaFact]
    public void The_solution_view_nests_each_file_under_the_one_it_belongs_to_and_File_Nesting_lays_the_rules_ones_out_flat()
    {
        MainWindow window = Open();
        (string Child, string Parent)[] byRule =
        [
            ("Components/Counter.razor.cs", "Components/Counter.razor"),
            ("Components/Counter.razor.css", "Components/Counter.razor"),
            ("Pages/Index.cshtml.cs", "Pages/Index.cshtml"),
            ("Forms/Form1.Designer.cs", "Forms/Form1.cs"),
            ("Forms/Form1.resx", "Forms/Form1.cs"),
            ("Properties/Resources.Designer.cs", "Properties/Resources.resx"),
            ("appsettings.Development.json", "appsettings.json"),
        ];
        foreach ((string child, string parent) in byRule)
        {
            Assert.Equal(At(parent), Holder(window, At(child)).Path);
        }
        Assert.Equal(At("Shapes.cs"), Holder(window, At("Shapes.Circle.cs")).Path);
        ExplorerNode counter = Show(window, At("Components/Counter.razor"));
        window.ExplorerView.Tree.Collapse(counter);
        Assert.True(counter.HasChildren, "Counter.razor shows no chevron for the files nested under it");
        Assert.Equal(["Counter.razor"], Under(window, Show(window, At("Components"))).Select(r => r.Name));

        FileNestingDialog dialog = OpenFileNesting(window);
        Assert.True(dialog.NestBox.IsChecked, "File Nesting's Show files with the same names as nested is off at first");
        Ui.Click(dialog.NestBox);
        Assert.False(dialog.Rules.IsEnabled, "The rules are not greyed with the check box cleared");
        Ui.Click(dialog.OkButton);
        Ui.WaitFor(() => !dialog.IsVisible, "the File Nesting dialog to close");
        foreach ((string child, string parent) in byRule)
        {
            string beside = Path.GetDirectoryName(At(parent))!;
            Assert.Equal(beside == Path.Combine(folder, "Shop") ? At("Shop.csproj") : beside, Holder(window, At(child)).Path);
        }
        Assert.Equal(At("Shapes.cs"), Holder(window, At("Shapes.Circle.cs")).Path);
        Assert.Equal(["Counter.razor", "Counter.razor.cs", "Counter.razor.css"], Under(window, Show(window, At("Components"))).Select(r => r.Name));

        dialog = OpenFileNesting(window);
        Ui.Click(dialog.NestBox);
        Ui.Click(dialog.OkButton);
        Ui.WaitFor(() => !dialog.IsVisible, "the File Nesting dialog to close");
        Assert.Equal(At("Components/Counter.razor"), Holder(window, At("Components/Counter.razor.cs")).Path);
        window.Close();
    }

    [AvaloniaFact]
    public void A_parent_renamed_or_moved_in_the_explorer_takes_its_dependents_renamed_with_it_and_its_projects_DependentUpon_follows()
    {
        MainWindow window = Open();
        List<string> asked = Answer(window);

        Choose(window, At("Components/Counter.razor"));
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Clicker.razor");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.Equal(["Clicker.razor", "Clicker.razor.cs", "Clicker.razor.css"], Directory.GetFiles(At("Components")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(At("Components/Clicker.razor"), Holder(window, At("Components/Clicker.razor.cs")).Path);

        Choose(window, At("Shapes.cs"));
        Ui.Run(window, AppActions.Rename);
        popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Figures.cs");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.True(File.Exists(At("Figures.Circle.cs")), $"Shapes.Circle.cs was not renamed with Shapes.cs: {window.Status}");
        string project = File.ReadAllText(At("Shop.csproj"));
        Assert.True(project == Project.Replace("Shapes.", "Figures.", StringComparison.Ordinal),
            $"The project did not follow Shapes.cs and Shapes.Circle.cs to their new names ({window.Status}):\n{project}");
        Ui.WaitFor(() => Shows(window, At("Figures.Circle.cs")) && Holder(window, At("Figures.Circle.cs")).Path == At("Figures.cs"),
            "Figures.Circle.cs nested under Figures.cs once the project is read again");

        Choose(window, At("Figures.cs"));
        Ui.Run(window, EditorActions.Undo);
        Assert.Single(asked);
        Assert.True(File.Exists(At("Shapes.Circle.cs")), $"Undo did not rename Figures.Circle.cs back: {window.Status}");
        Assert.True(File.Exists(At("Shapes.cs")), $"Undo did not rename Figures.cs back: {window.Status}");
        Assert.Equal(Project, File.ReadAllText(At("Shop.csproj")));

        Choose(window, At("Forms/Form1.cs"));
        Ui.Run(window, EditorActions.Cut);
        Assert.True(Show(window, At("Forms/Form1.resx")).IsCut, "Form1.resx, nested under the form cut, is not dimmed with it");
        Choose(window, At("Archive"));
        Ui.Run(window, EditorActions.Paste);
        Assert.Empty(Directory.GetFiles(At("Forms")));
        Assert.Equal(["Form1.Designer.cs", "Form1.cs", "Form1.resx"], Directory.GetFiles(At("Archive")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(At("Archive/Form1.cs"), Holder(window, At("Archive/Form1.resx")).Path);
        window.Close();
    }

    /// <summary>A window on the folder, with no C# language server: nesting, renames and moves ask nothing of C#, and the
    /// project is never restored for one.</summary>
    private MainWindow Open() => Ui.OpenFolder(folder, languageServer: false);

    /// <summary>File Nesting... from the Explorer's options in its header, as a person opens it.</summary>
    private static FileNestingDialog OpenFileNesting(MainWindow window)
    {
        Control pane = window.ExplorerView.GetVisualAncestors().OfType<Control>().First(a => a.GetType().Name == "ToolPane");
        Button options = pane.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options");
        Ui.Click(options);
        Ui.Choose(window.DockHost.OpenOptions, "Appearance", "File Nesting…");
        Ui.WaitFor(() => window.Frontmost is FileNestingDialog, "the File Nesting dialog");
        return DialogButtonsTests.Check((FileNestingDialog)window.Frontmost);
    }

    /// <summary>Opens the Explorer down to a file or folder and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    /// <summary>Whether the Explorer shows a file, opened down to it: not yet while the project as read before a rename
    /// still stands, which names the old files.</summary>
    private static bool Shows(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.Any(r => r.Path == path);
    }

    /// <summary>The row a file's row is shown under: the nearest above it that is less deep.</summary>
    private static ExplorerNode Holder(MainWindow window, string path)
    {
        ExplorerNode row = Show(window, path);
        IList<ExplorerNode> rows = window.ExplorerView.Tree.Rows;
        for (int at = rows.IndexOf(row) - 1; at >= 0; at--)
        {
            if (rows[at].Depth < row.Depth)
            {
                return rows[at];
            }
        }
        throw new Xunit.Sdk.XunitException($"{path} is shown under nothing");
    }

    /// <summary>The rows right under a row, opened.</summary>
    private static List<ExplorerNode> Under(MainWindow window, ExplorerNode row)
    {
        window.ExplorerView.Tree.Expand(row);
        IList<ExplorerNode> rows = window.ExplorerView.Tree.Rows;
        return [.. rows.Skip(rows.IndexOf(row) + 1).TakeWhile(r => r.Depth > row.Depth).Where(r => r.Depth == row.Depth + 1)];
    }

    /// <summary>Clicks a row of the Explorer, opened down to it, as a person chooses one before a key.</summary>
    private static void Choose(MainWindow window, string path)
    {
        ExplorerNode row = Show(window, path);
        Ui.ClickRow(window, row);
    }

    /// <summary>Agrees to every question the window asks, keeping their titles.</summary>
    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
