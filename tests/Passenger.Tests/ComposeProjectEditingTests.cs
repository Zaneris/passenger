using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.MSBuild;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Docker Compose project's file (.dcproj) in the editor and the Solution view, with no engine and no restore:
/// MSBuild's code help over it, read from MSBuild's schemas and the files it names, and the files it holds as its text
/// names them, since the SDK it names is Visual Studio's alone.</summary>
public sealed class ComposeProjectEditingTests
{
    /// <summary>A Docker Compose project (.dcproj) is MSBuild's: coloured as markup, its properties completed and its elements
    /// described from MSBuild's schemas, and a file it names gone to; the SDK it names is Visual Studio's alone, so it is never
    /// evaluated, and nothing is marked on it or listed in Problems, typed in or not.</summary>
    [AvaloniaFact]
    public void A_Docker_Compose_project_is_read_as_MSBuilds_and_never_marked_for_the_Visual_Studio_SDK_it_names()
    {
        string own = PathNames.Canonical(TestFolders.Create("project-dcproj-"));
        TestFolders.Write(own, "docker-compose.yml", "services:\n  web:\n    image: busybox:1.37\n");
        TestFolders.Write(own, "docker-compose.dcproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" Sdk="Microsoft.Docker.Sdk">
              <PropertyGroup Label="Globals">
                <ProjectVersion>2.1</ProjectVersion>
                <DockerTargetOS>Linux</DockerTargetOS>
                <DockerLaunchAction>LaunchBrowser</DockerLaunchAction>
                <DockerServiceName>web</DockerServiceName>
              </PropertyGroup>
              <ItemGroup>
                <None Include="docker-compose.yml" />
              </ItemGroup>
            </Project>

            """);
        TestFolders.Write(own, "Shop.slnx", "<Solution>\n  <Project Path=\"docker-compose.dcproj\" />\n</Solution>\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(Path.Combine(own, "Shop.slnx"));
        window.OpenFile(Path.Combine(own, "docker-compose.dcproj"));
        Ui.Settle();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Assert.Equal("MSBuild", document.Language.Name);
            MSBuildDocument help = window.Session.MSBuild.For(document) ?? throw new Xunit.Sdk.XunitException("The .dcproj has no MSBuild code help");
            TextSnapshot text = editor.Document.Snapshot;
            int tag = text.ToString().IndexOf("PropertyGroup", StringComparison.Ordinal);
            Assert.Contains(editor.Highlighter!.GetSpans(text, tag, 1), s => s.Kind == TokenKind.Tag && s.Start <= tag && s.End > tag);

            Ui.PlaceAfter(editor, "<DockerServiceName>web</DockerServiceName>");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("<");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Description"), "MSBuild's properties a PropertyGroup takes, after a tag's '<'");
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "<DockerLaunchAction>Launch");
            Ui.Run(window, AppActions.QuickDocumentation);
            Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the property");
            Assert.Equal("DockerLaunchAction\nMSBuild property", window.Assist.HoverText);
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "<None Include=\"docker-com");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.Settle();
            Assert.Equal("docker-compose.yml", window.EditorArea.Active?.Name);

            Ui.Await(help.First, "the .dcproj's code help ready");
            Assert.True((help.Project, help.EvaluatedVersion) == (null, null), $"The .dcproj was evaluated through {help.Project}");
            Assert.True(document.Markers.Count == 0, $"The .dcproj is marked: {string.Join("; ", document.Markers.Select(m => m.Message))}");
            Assert.DoesNotContain(window.BottomPanel.Problems.FileProblems, p => p.Path is { } file && PathNames.Same(file, document.Path));
            Assert.False(window.Status?.StartsWith("MSBuild could not evaluate", StringComparison.Ordinal) == true, window.Status);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Docker Compose project at the solution's root shows in the Solution view only what it holds: the files its
    /// ItemGroups name, in their folders, an override under the file it depends on, and those its runs read that it names
    /// not (its launch profiles); never the projects, folders and files beside it, which the Files view still shows, nor
    /// a Dependencies row. A file it names once saved shows at once.</summary>
    [AvaloniaFact]
    public void A_Docker_Compose_project_at_the_solutions_root_shows_only_the_files_it_holds()
    {
        string own = PathNames.Canonical(TestFolders.Create("solution-dcproj-"));
        TestFolders.Write(own, "docker-compose.foo.yml", "services:\n  internal-api:\n    image: busybox:1.37\n");
        TestFolders.Write(own, "docker-compose.foo.override.yml", "services:\n  internal-api:\n    environment: { GREETING: hi }\n");
        TestFolders.Write(own, "docker/configuration/dotnet-api/Dockerfile", "FROM busybox:1.37\n");
        TestFolders.Write(own, "docker/configuration/notes.txt", "Not the project's.\n");
        TestFolders.Write(own, "launchSettings.json", "{}\n");
        TestFolders.Write(own, "Properties/launchSettings.json", "{ \"profiles\": {} }\n");
        TestFolders.Write(own, "Foo.WebApi/Program.cs", "System.Console.WriteLine(\"Foo\");\n");
        TestFolders.Write(own, "Tests/FooTests.cs", "class FooTests\n{\n}\n");
        TestFolders.Write(own, "Legacy/old.txt", "Old.\n");
        TestFolders.Write(own, "README.md", "# Foo\n");
        string dcproj = TestFolders.Write(own, "docker-compose-foo.dcproj", """
            <Project ToolsVersion="15.0" Sdk="Microsoft.Docker.Sdk">
              <PropertyGroup Label="Globals">
                <DockerComposeBaseFilePath>docker-compose.foo</DockerComposeBaseFilePath>
              </PropertyGroup>
              <ItemGroup>
                <None Include="docker-compose.foo.override.yml">
                  <DependentUpon>docker-compose.foo.yml</DependentUpon>
                </None>
                <None Include="docker-compose.foo.yml" />
                <None Include="docker\configuration\dotnet-api\Dockerfile" />
                <None Include="launchSettings.json" />
              </ItemGroup>
            </Project>

            """);
        TestFolders.Write(own, "Foo.slnx", "<Solution>\n  <Project Path=\"docker-compose-foo.dcproj\" />\n</Solution>\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(Path.Combine(own, "Foo.slnx"));
        Ui.Settle();
        try
        {
            ExplorerTree tree = window.ExplorerView.Tree;
            Assert.True(tree.IsSolutionView, "The solution opened in the Files view");
            ExplorerNode project = tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project && r.Name == "docker-compose-foo")
                ?? throw new Xunit.Sdk.XunitException($"The Solution view shows no docker-compose-foo: [{string.Join(", ", tree.Rows.Select(r => r.Name))}]");
            Assert.Equal(["Properties", "docker", "docker-compose.foo.yml", "launchSettings.json"], Under(tree, project));
            ExplorerNode compose = project.Children!.Single(c => c.Name == "docker-compose.foo.yml");
            Assert.Equal(["docker-compose.foo.override.yml"], Under(tree, compose));
            ExplorerNode docker = project.Children!.Single(c => c.Name == "docker");
            Assert.Equal(["configuration"], Under(tree, docker));
            Assert.Equal(["dotnet-api"], Under(tree, docker.Children!.Single()));
            Assert.Equal(["Dockerfile"], Under(tree, docker.Children!.Single().Children!.Single()));
            Assert.Equal(["launchSettings.json"], Under(tree, project.Children!.Single(c => c.Name == "Properties")));

            File.WriteAllText(dcproj, File.ReadAllText(dcproj).Replace("<None Include=\"launchSettings.json\" />", "<None Include=\"README.md\" />", StringComparison.Ordinal));
            List<string> Held() => tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project && r.Name == "docker-compose-foo")?.Children?.Select(c => c.Name).ToList() ?? [];
            Ui.WaitFor(() => Held().Contains("README.md") && !Held().Contains("launchSettings.json"),
                () => $"README.md in place of launchSettings.json once the .dcproj named it: [{string.Join(", ", Held())}]");

            Ui.Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => Avalonia.Controls.ToolTip.GetTip(b) is string tip
                && tip.StartsWith("Switch between the solution", StringComparison.Ordinal)));
            Assert.False(tree.IsSolutionView, "The view switch left the Solution view shown");
            List<string> files = [.. tree.Rows.Where(r => r.Depth == 1).Select(r => r.Name)];
            Assert.True(((string[])["Foo.WebApi", "Legacy", "Tests", "README.md"]).All(files.Contains), $"The Files view shows [{string.Join(", ", files)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The names of the rows right under a row, opened.</summary>
    private static List<string> Under(ExplorerTree tree, ExplorerNode row)
    {
        tree.Expand(row);
        return [.. (row.Children ?? []).Select(c => c.Name)];
    }
}
