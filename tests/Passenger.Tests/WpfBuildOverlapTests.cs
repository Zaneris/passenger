using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Build, Run and dotnet watch use their own WPF generated files while design-time markup compilation holds
/// its files, including when another window owns that work (#218).</summary>
public sealed class WpfBuildOverlapTests(ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData("build")]
    [InlineData("run")]
    [InlineData("watch")]
    [InlineData("referenced")]
    [InlineData("closed owner")]
    [InlineData("unrelated")]
    public void Building_or_running_a_WPF_project_while_design_time_work_writes_App_g_cs_keeps_the_generated_files_owned(string scenario)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The generated-file sharing violation occurs on Windows");
        }
        string folder = TestFolders.Create("wpf-build-overlap-");
        string ready = Path.Combine(folder, "design-time-owner.txt");
        string release = Path.Combine(folder, "release-design-time.txt");
        string overlapped = Path.Combine(folder, "build-overlapped.txt");
        TestFolders.Prepare(() =>
        {
            WpfApp.Create(folder, "Overlap");
            string project = Path.Combine(folder, "Overlap.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<PropertyGroup>", "<PropertyGroup><StartupObject>Overlap.ProbeProgram</StartupObject>", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(folder, "ProbeProgram.cs"), "namespace Overlap; public static class ProbeProgram { public static void Main() { System.Console.WriteLine(\"The WPF app ran\"); } }");
            File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), Targets);
            Restore.Run(project);
        }, folder);
        string buildFolder = folder;
        if (scenario is "referenced" or "unrelated")
        {
            buildFolder = TestFolders.Create("wpf-build-other-");
            string other = buildFolder;
            TestFolders.Prepare(() =>
            {
                WpfApp.Create(other, "Other");
                if (scenario == "referenced")
                {
                    string middle = TestFolders.Create("wpf-build-middle-");
                    AvaloniaApp.UseFeed(middle);
                    File.WriteAllText(Path.Combine(middle, "Middle.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{AvaloniaApp.Metadata("TargetFramework")}-windows</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"{Path.Combine(folder, "Overlap.csproj")}\" /></ItemGroup></Project>");
                    string project = Path.Combine(other, "Other.csproj");
                    File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", $"<ItemGroup><ProjectReference Include=\"{Path.Combine(middle, "Middle.csproj")}\" /></ItemGroup></Project>", StringComparison.Ordinal));
                }
                Restore.Run(Path.Combine(other, "Other.csproj"));
            }, other);
        }
        ConcurrentDictionary<int, ChildStart> children = new();
        void Started(object? sender, ChildStart child) => children[child.ProcessId] = child;
        ChildProcesses.Started += Started;
        MainWindow window = new() { Width = 1200, Height = 800 };
        MainWindow? otherWindow = null;
        try
        {
            window.Show();
            window.OpenFolder(Path.Combine(folder, "Overlap.csproj"));
            window.OpenFile(Path.Combine(folder, "MainWindow.xaml.cs"));
            Ui.PumpUntil(() => File.Exists(ready), "the design-time writer to hold App.g.cs", TimeSpan.FromMinutes(2));
            string owner = Ui.Shared(ready);
            string generated = Ui.Shared(ready + ".path");
            output.WriteLine($"Design-time generated-file owner: {owner}");
            output.WriteLine(string.Join('\n', children.Values.Where(child => child.Command.Contains("Passenger.Host", StringComparison.Ordinal))));
            MainWindow builder = window;
            if (scenario is "referenced" or "closed owner" or "unrelated")
            {
                if (scenario == "closed owner")
                {
                    window.Close();
                }
                otherWindow = new MainWindow { Width = 1200, Height = 800 };
                otherWindow.Show();
                otherWindow.OpenFolder(Path.Combine(buildFolder, buildFolder == folder ? "Overlap.csproj" : "Other.csproj"));
                builder = otherWindow;
            }
            if (scenario == "watch")
            {
                builder.ApplySettings(builder.Settings with { HotReload = true });
            }
            Task<string?>? run = scenario is "run" or "watch" ? builder.RunAsync() : null;
            if (run is null)
            {
                Assert.True(builder.Perform(AppActions.Build));
            }
            Ui.PumpUntil(() => run?.IsCompleted ?? !builder.IsBuilding, "the WPF build", TimeSpan.FromSeconds(scenario == "unrelated" ? 20 : 120));
            string built = builder.BottomPanel.Build.Document.Snapshot.ToString();
            output.WriteLine(built);
            Assert.False(File.Exists(overlapped), $"Build reached WPF's generated files while design-time process {owner} held App.g.cs:\n{built}");
            if (run is not null)
            {
                Assert.Null(run.Result);
                if (scenario == "watch")
                {
                    Ui.PumpUntil(() => builder.BottomPanel.Run.Document.Snapshot.ToString().Contains("The WPF app ran", StringComparison.Ordinal), "the WPF app to run under dotnet watch", TimeSpan.FromMinutes(2));
                    Assert.True(builder.Perform(AppActions.Build));
                    Ui.PumpUntil(() => !builder.IsBuilding, "Build while dotnet watch remains active", TimeSpan.FromMinutes(2));
                    Assert.True(builder.Runner.IsRunning, "Build ended the active watcher to acquire its project");
                    Assert.Contains("Build succeeded", builder.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
                    Assert.True(builder.Perform(AppActions.Stop));
                }
                Ui.PumpUntil(() => builder.RunEnd.IsCompleted, "the WPF app's program to exit", TimeSpan.FromSeconds(30));
                if (scenario != "watch")
                {
                    Assert.Equal(0, builder.RunEnd.Result);
                }
                Assert.Contains("The WPF app ran", builder.BottomPanel.Run.Document.Snapshot.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Build succeeded", built, StringComparison.Ordinal);
            }
            Assert.False(File.Exists(release), "A build had to release the design-time writer before it could finish");
            if (scenario != "closed owner")
            {
                Assert.Throws<IOException>(() => new FileStream(generated, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            }
            if (scenario != "unrelated")
            {
                string ordinary = Ui.Shared(Path.Combine(folder, "build-path.txt")).Trim();
                output.WriteLine($"Normal generated path: {ordinary}");
                Assert.NotEqual(Path.GetFullPath(generated), Path.GetFullPath(ordinary), StringComparer.OrdinalIgnoreCase);
                Assert.True(File.Exists(ordinary), "The ordinary WPF build did not create its generated code");
            }
            File.WriteAllText(release, "The build finished while the design-time writer remained held");
            Ui.Settle();
        }
        finally
        {
            File.WriteAllText(release, "The test ended");
            ChildProcesses.Started -= Started;
            otherWindow?.Close();
            window.Close();
        }
    }

    private const string Targets = """
        <Project>
          <UsingTask TaskName="HoldWpfGeneratedFile" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
            <ParameterGroup>
              <GeneratedFile ParameterType="System.String" Required="true" />
              <Ready ParameterType="System.String" Required="true" />
              <Release ParameterType="System.String" Required="true" />
            </ParameterGroup>
            <Task>
              <Code Type="Fragment" Language="cs"><![CDATA[
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GeneratedFile));
                using (System.IO.FileStream generated = new System.IO.FileStream(GeneratedFile, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.Write, System.IO.FileShare.Read))
                {
                    System.IO.File.WriteAllText(Ready, System.Diagnostics.Process.GetCurrentProcess().Id + " holding " + System.IO.Path.GetFullPath(GeneratedFile));
                    System.IO.File.WriteAllText(Ready + ".path", System.IO.Path.GetFullPath(GeneratedFile));
                    System.DateTime until = System.DateTime.UtcNow.AddMinutes(3);
                    while (!System.IO.File.Exists(Release) && System.DateTime.UtcNow < until)
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                }
              ]]></Code>
            </Task>
          </UsingTask>
          <Target Name="HoldDesignTimeWpfWriter" BeforeTargets="MarkupCompilePass1" Condition="'$(DesignTimeBuild)' == 'true' And '$(RazorDesignTimeTargets)' != '' And !Exists('$(MSBuildProjectDirectory)/release-design-time.txt')">
            <HoldWpfGeneratedFile GeneratedFile="$(IntermediateOutputPath)App.g.cs" Ready="$(MSBuildProjectDirectory)/design-time-owner.txt" Release="$(MSBuildProjectDirectory)/release-design-time.txt" />
          </Target>
          <Target Name="RecordOverlappingBuild" BeforeTargets="MarkupCompilePass1" Condition="'$(DesignTimeBuild)' != 'true' And Exists('$(MSBuildProjectDirectory)/design-time-owner.txt') And !Exists('$(MSBuildProjectDirectory)/release-design-time.txt')">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-path.txt" Lines="$([System.IO.Path]::GetFullPath($(IntermediateOutputPath)App.g.cs))" Overwrite="true" />
            <ReadLinesFromFile File="$(MSBuildProjectDirectory)/design-time-owner.txt.path"><Output TaskParameter="Lines" ItemName="HeldGeneratedPath" /></ReadLinesFromFile>
            <WriteLinesToFile Condition="'@(HeldGeneratedPath)' == '$([System.IO.Path]::GetFullPath($(IntermediateOutputPath)App.g.cs))'" File="$(MSBuildProjectDirectory)/build-overlapped.txt" Lines="A real build overlapped design-time markup compilation" Overwrite="true" />
          </Target>
        </Project>
        """;
}
