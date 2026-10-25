using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;

namespace Passenger.Tests;

/// <summary>Passenger's file icons: a shape and colour of its own for each language, tool and kind of file, drawn in the
/// Explorer, the editor's tabs and the file lists, a plain folder as a folder.</summary>
public sealed class FileIconsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("file-icons-");

    public FileIconsTests()
    {
        Write("Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("LicenseChecker.cs", "class LicenseChecker;\n");
        Write("ReadmeView.cs", "class ReadmeView;\n");
        Write("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("package.json", "{}\n");
        Write("Dockerfile", "FROM scratch\n");
        Write(".gitignore", "bin/\n");
        Write("README.md", "# Shop\n");
        Write("LICENSE-MIT", "MIT\n");
        Write("appsettings.json", "{}\n");
        Write("build.ps1", "Write-Host build\n");
        Write("run.sh", "echo run\n");
        Write("main.go", "package main\n");
        Write("logo.png", "");
        Write("app.test.ts", "");
        Write("notes.unknown", "");
        Write(".github/dependabot.yml", "version: 2\n");
        Write("src/Order.cs", "class Order;\n");
        TestGit.Init(folder);
        // A change the Commit window lists.
        Write("package.json", "{ \"name\": \"shop\" }\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Each_kind_of_file_is_drawn_in_a_shape_and_colour_of_its_own_in_the_Explorer_the_tabs_and_the_Commit_window()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            (string Name, string Shape, string? Colour)[] expected =
            [
                ("Program.cs", "CSharp", "Blue"),
                ("LicenseChecker.cs", "CSharp", "Blue"),
                ("ReadmeView.cs", "CSharp", "Blue"),
                ("Shop.csproj", "Stack", "Blue"),
                ("package.json", "Npm", "Red"),
                ("Dockerfile", "Docker", "Blue"),
                (".gitignore", "Git", "Red"),
                ("README.md", "Book", "Blue"),
                ("LICENSE-MIT", "Law", "Blue"),
                ("appsettings.json", "Json", "Cyan"),
                ("build.ps1", "PowerShell", "Blue"),
                ("run.sh", "Terminal", "Purple"),
                ("main.go", "Go", "Blue"),
                ("logo.png", "Image", "Orange"),
                ("app.test.ts", "TestTypeScript", "Blue"),
                ("notes.unknown", "File", null),
                (".github", "GitHub", null),
            ];
            foreach ((string name, string shape, string? colour) in expected)
            {
                PathIcon icon = Drawn(window, name);
                Assert.True(ReferenceEquals(Resource($"FileIcon.{shape}"), icon.Data), $"The Explorer does not draw {name} as its {shape}");
                Assert.True(ReferenceEquals(Resource(colour is null ? TokenKeys.File_Plain : $"Passenger.FileIcon.{colour}"), icon.Foreground),
                    $"The Explorer does not draw {name} in the {colour ?? "plain"} file colour");
            }
            Assert.True(ReferenceEquals(Resource(IconKeys.Explorer), Drawn(window, "src").Data), "A plain folder lost the folder icon");
            Assert.True(Icons(window.EditorArea).Any(i => ReferenceEquals(i.Data, Resource("FileIcon.CSharp")) && ReferenceEquals(i.Foreground, Resource(TokenKeys.FileIcon_Blue))),
                "The open C# file's tab is not drawn with the C# icon");

            Ui.Landed(() => window.Vcs is { } vcs && vcs.Changes.Any(c => c.Name == "package.json"), "package.json listed as changed");
            window.DockHost.Show(ToolWindowIds.Commit);
            Ui.Landed(() => Icons(window.CommitView).Any(i => ReferenceEquals(i.Data, Resource("FileIcon.Npm")) && ReferenceEquals(i.Foreground, Resource(TokenKeys.FileIcon_Red))),
                "the Commit window's package.json drawn with npm's icon");
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<PathIcon> Icons(Visual area) => area.GetVisualDescendants().OfType<PathIcon>();

    /// <summary>The icon the Explorer draws on a row.</summary>
    private static PathIcon Drawn(MainWindow window, string name)
    {
        ExplorerNode row = window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidOperationException($"The Explorer shows no {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
        return Icons(window.ExplorerView).FirstOrDefault(i => ReferenceEquals(i.DataContext, row) && !i.Classes.Contains("chevron"))
            ?? throw new InvalidOperationException($"The Explorer draws no icon on {name}'s row");
    }

    private static object Resource(string key)
    {
        Application app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is not null ? value : throw new InvalidOperationException($"The theme has no {key}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
