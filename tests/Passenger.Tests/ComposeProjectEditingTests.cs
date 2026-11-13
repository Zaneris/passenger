using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.MSBuild;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Docker Compose project's file (.dcproj) in the editor, with no engine and no restore: MSBuild's code help
/// over it, read from MSBuild's schemas and the files it names, since the SDK it names is Visual Studio's alone.</summary>
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
}
