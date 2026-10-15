using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A WPF reference and an ordinary multi-targeted consumer retain their real restore and output identities while
/// code help reads generated documents in the IDE's own intermediate directory, including after closing and reopening.</summary>
public sealed class DesignTimeIsolationTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public void Custom_intermediates_and_nested_MSBuild_references_keep_their_own_outputs_and_cached_generated_documents()
    {
        string root = TestFolders.Create("isolated builds-");
        string settings = TestFolders.Create("isolated-settings-");
        string library = Path.Combine(root, "Markup");
        string app = Path.Combine(root, "Consumer");
        string solution = Path.Combine(root, "Probe.slnx");
        string framework = AvaloniaApp.Metadata("TargetFramework");
        TestFolders.Prepare(() =>
        {
            WpfApp.Create(library, "Markup");
            File.Delete(Path.Combine(library, "Directory.Build.props"));
            Directory.CreateDirectory(app);
            AvaloniaApp.UseFeed(root);
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <EnableWindowsTargeting>true</EnableWindowsTargeting>
                    <Configuration>Debug</Configuration>
                    <Platform>x64</Platform>
                    <BaseIntermediateOutputPath>obj/custom/</BaseIntermediateOutputPath>
                    <OutputPath>bin/custom/</OutputPath>
                    <ProbeFlavor>CustomGenerator</ProbeFlavor>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), Paths);
            File.WriteAllText(Path.Combine(library, "Greeter.cs"), "namespace Markup; public static class Greeter { public static string Hello() => \"Hello\"; }");
            File.WriteAllText(Path.Combine(library, "MainWindow.xaml.cs"), "namespace Markup; public partial class MainWindow : System.Windows.Window { public MainWindow() { InitializeComponent(); } }");
            File.WriteAllText(Path.Combine(app, "Consumer.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>{{framework}};{{framework}}-windows</TargetFrameworks></PropertyGroup>
                  <ItemGroup Condition="'$(Configuration)' == 'Debug' And '$(TargetFramework)' == '{{framework}}-windows'">
                    <ProjectReference Include="../Markup/Markup.csproj" GlobalPropertiesToRemove="ProbeIgnored" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(app, "Consumer.cs"), """
                public class Consumer
                {
                #if WINDOWS
                    public string Greeting => Markup.Greeter.Hello();
                #endif
                }
                """);
            File.WriteAllText(solution, "<Solution><Project Path=\"Consumer/Consumer.csproj\" /><Project Path=\"Markup/Markup.csproj\" /></Solution>");
            Restore.Run(solution);
        }, root);
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = settings };
        try
        {
            first.Show();
            first.KeepLayout();
            first.OpenFolder(solution);
            first.OpenFile(Path.Combine(library, "MainWindow.xaml.cs"));
            Ui.WaitFor(() => first.Session.DesignTimeBuilds.Builds >= 2 && first.CSharpService?.IsReady == true,
                "both projects' isolated builds and generated WPF members to load", TimeSpan.FromMinutes(2));
            Ui.Settle();
            output.WriteLine(string.Join('\n', first.CSharpService!.For(first.EditorArea.Active!)!.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Ui.WaitFor(() => first.CSharpService!.For(first.EditorArea.Active!)?.Diagnostics.Any(diagnostic => diagnostic.Severity == 1) == false,
                "WPF's InitializeComponent from its generated source to resolve");
            first.ExplorerView.Choose(Assert.Single(first.ExplorerView.Tree.Rows, row => row.Kind == NodeKind.Project && row.Name == "Markup"));
            Assert.True(first.Perform(AppActions.BuildSelected));
            Ui.WaitFor(() => !first.IsBuilding, "the custom-output WPF project to build");
            output.WriteLine(first.BottomPanel.Build.Document.Snapshot.ToString());
            Assert.Contains("Build succeeded", first.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
            string[] normal = File.ReadAllLines(Path.Combine(library, "paths-build-" + framework + "-windows.txt"));
            string[] design = File.ReadAllLines(Path.Combine(library, "paths-true-" + framework + "-windows.txt"));
            output.WriteLine("Normal paths: " + string.Join(" | ", normal));
            output.WriteLine("Design-time paths: " + string.Join(" | ", design));
            Assert.NotEqual(normal[0], design[0]);
            Assert.Equal(normal[1..], design[1..]);
            Assert.Equal(normal[1..], File.ReadAllLines(Path.Combine(library, "nested-paths.txt"))[1..]);
            Assert.StartsWith(Path.Combine(settings, "project-intermediates", "obj") + Path.DirectorySeparatorChar, design[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(design[0], "Markup.AssemblyInfo.cs")));
            Assert.True(File.Exists(Path.Combine(design[0], "Markup.GeneratedMSBuildEditorConfig.editorconfig")));
            Assert.DoesNotContain("IntermediateOutputPath=", File.ReadAllText(Path.Combine(library, "globals.txt")), StringComparison.OrdinalIgnoreCase);
            first.Close();
            Ui.Settle();
            string cache = Directory.EnumerateFiles(settings, "projectBuilds.json", SearchOption.AllDirectories).Single();
            using JsonDocument kept = JsonDocument.Parse(File.ReadAllText(cache));
            foreach (JsonProperty project in kept.RootElement.GetProperty("projects").EnumerateObject())
            {
                using BrotliStream compressed = new(new MemoryStream(Convert.FromBase64String(project.Value.GetProperty("builds").GetString()!)), CompressionMode.Decompress);
                using StreamReader reader = new(compressed, Encoding.UTF8);
                using JsonDocument infos = JsonDocument.Parse(reader.ReadToEnd());
                foreach (JsonElement info in infos.RootElement.EnumerateArray())
                {
                    string assembly = info.GetProperty("intermediateOutputFilePath").GetString()!;
                    Assert.DoesNotContain(Path.Combine(settings, "project-intermediates"), assembly, StringComparison.OrdinalIgnoreCase);
                    string commandOutput = Assert.Single(info.GetProperty("commandLineArgs").EnumerateArray(),
                        argument => argument.GetString()!.StartsWith("/out:", StringComparison.OrdinalIgnoreCase)).GetString()!;
                    Assert.Equal(assembly, commandOutput[5..].Trim('"'));
                    if (Path.GetFileName(info.GetProperty("filePath").GetString()) == "Markup.csproj")
                    {
                        Assert.Equal(Path.Combine(normal[0], Path.GetFileName(normal[3])), assembly);
                        Assert.True(File.Exists(assembly), $"Roslyn's Edit and Continue baseline assembly does not exist: {assembly}");
                    }
                    foreach (JsonElement document in info.GetProperty("documents").EnumerateArray())
                    {
                        string path = document.GetProperty("filePath").GetString()!;
                        if (path.EndsWith(".g.cs", StringComparison.Ordinal) || path.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal))
                        {
                            Assert.StartsWith(Path.Combine(settings, "project-intermediates", "obj") + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            for (int reopening = 0; reopening < 2; reopening++)
            {
                MainWindow second = new() { Width = 1200, Height = 800, StateFolder = settings };
                try
                {
                    second.Show();
                    second.KeepLayout();
                    second.OpenFolder(solution);
                    second.OpenFile(Path.Combine(library, "MainWindow.xaml.cs"));
                    Ui.WaitFor(() => second.CSharpService?.IsReady == true, "the kept generated WPF documents to load again", TimeSpan.FromMinutes(2));
                    Ui.Settle();
                    Assert.DoesNotContain(second.CSharpService!.For(second.EditorArea.Active!)!.Diagnostics, diagnostic => diagnostic.Severity == 1);
                    if (reopening > 0)
                    {
                        Assert.Equal(0, second.Session.DesignTimeBuilds.Builds);
                    }
                }
                finally
                {
                    second.Close();
                    Ui.Settle();
                }
            }
        }
        finally
        {
            first.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_project_that_moves_its_intermediates_after_evaluation_reports_why_code_help_cannot_build_it_then_recovers_when_fixed(bool localProperty)
    {
        string folder = TestFolders.Create("unsafe-intermediates-");
        string project = Path.Combine(folder, "Unsafe.csproj");
        TestFolders.Prepare(() =>
        {
            WpfApp.Create(folder, "Unsafe");
            File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), localProperty ? """
                <Project TreatAsLocalProperty="IntermediateOutputPath">
                  <PropertyGroup><IntermediateOutputPath>obj/shared/</IntermediateOutputPath></PropertyGroup>
                </Project>
                """ : """
                <Project>
                  <Target Name="MoveGeneratedFiles" BeforeTargets="CoreCompile">
                    <PropertyGroup><IntermediateOutputPath>obj/shared/</IntermediateOutputPath></PropertyGroup>
                  </Target>
                </Project>
                """);
            Restore.Run(project);
        }, folder);
        MainWindow window = new() { Width = 1200, Height = 800 };
        try
        {
            window.Show();
            window.OpenFolder(project);
            window.OpenFile(Path.Combine(folder, "MainWindow.xaml.cs"));
            Ui.WaitFor(() => window.CSharpService?.ProjectProblems.Any(problem => problem.Message.Contains("IntermediateOutputPath", StringComparison.Ordinal)) == true,
                "the project load to explain its unsafe generated-file path", TimeSpan.FromMinutes(2));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder, "obj"), "*.g.cs", SearchOption.AllDirectories));
            File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), "<Project />");
            Ui.WaitFor(() => window.Session.DesignTimeBuilds.Builds > 0 && window.CSharpService!.ProjectProblems.Count == 0,
                "code help to recover after the unsafe path override is removed", TimeSpan.FromMinutes(2));
            Assert.True(window.CSharpService!.Session!.Fed);
        }
        finally
        {
            window.Close();
        }
    }

    private const string Paths = """
        <Project>
          <UsingTask TaskName="RecordGlobalProperties" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
            <ParameterGroup><File ParameterType="System.String" Required="true" /></ParameterGroup>
            <Task><Code Type="Fragment" Language="cs"><![CDATA[
              System.Collections.Generic.List<string> values = new System.Collections.Generic.List<string>();
              foreach (System.Collections.Generic.KeyValuePair<string, string> property in ((Microsoft.Build.Framework.IBuildEngine6)BuildEngine).GetGlobalProperties())
              {
                  values.Add(property.Key + "=" + property.Value);
              }
              System.IO.File.WriteAllLines(File, values);
            ]]></Code></Task>
          </UsingTask>
          <Target Name="ProbePaths" BeforeTargets="CoreCompile">
            <PropertyGroup><_ProbeMode Condition="'$(DesignTimeBuild)' != 'true'">build</_ProbeMode><_ProbeMode Condition="'$(DesignTimeBuild)' == 'true'">true</_ProbeMode></PropertyGroup>
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/paths-$(_ProbeMode)-$(TargetFramework).txt" Lines="$([System.IO.Path]::GetFullPath($(IntermediateOutputPath)));$(ProjectAssetsFile);$(OutputPath);$(TargetPath);$(TargetRefPath);$(Configuration);$(Platform);$(ProbeFlavor)" Overwrite="true" />
            <RecordGlobalProperties Condition="'$(DesignTimeBuild)' == 'true'" File="$(MSBuildProjectDirectory)/globals.txt" />
            <MSBuild Condition="'$(DesignTimeBuild)' == 'true' And '@(ProjectReference)' != ''" Projects="@(ProjectReference)" Targets="ProbeNestedPaths" />
          </Target>
          <Target Name="ProbeNestedPaths">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/nested-paths.txt" Lines="$([System.IO.Path]::GetFullPath($(IntermediateOutputPath)));$(ProjectAssetsFile);$(OutputPath);$(TargetPath);$(TargetRefPath);$(Configuration);$(Platform);$(ProbeFlavor)" Overwrite="true" />
          </Target>
        </Project>
        """;
}
