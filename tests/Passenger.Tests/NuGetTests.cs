using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Packages;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The NuGet window over two projects and a stand-in feed whose VulnerabilityInfo the audit reads, so nothing
/// reaches nuget.org: a change runs one at a time, the projects' actions disabled meanwhile, and one refused says why.</summary>
public sealed class NuGetTests : IDisposable
{
    private const string Id = "Passenger.Vulnerable";

    private const string Advisory = "https://github.com/advisories/GHSA-pass-nuget-test";

    private readonly string folder = TestFolders.Create("nuget-");
    private readonly StandInFeed feed = new();
    private readonly string lib;

    public NuGetTests()
    {
        feed.Add(Id, "1.0.0");
        feed.Add(Id, "1.0.1");
        feed.Add(Id, "1.1.0");
        feed.Advise(Id, 1, Advisory, "[1.0.0, 1.0.1)");
        // Only the stand-in, for packages and for the audit, and restores kept out of the person's package cache.
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        lib = Project("Lib", string.Empty);
        Project("App", string.Empty);
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    /// <summary>While another asker's change of Lib (an agent's, Migrations') runs, the NuGet window's Install on App is
    /// disabled, and a change the window asks for meanwhile is refused saying what is under way, not dropped unsaid; once
    /// the first change ends, App's Install is enabled again.</summary>
    [AvaloniaFact]
    public void While_a_package_change_runs_the_projects_actions_are_disabled_and_one_asked_meanwhile_is_refused_saying_why()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Search(nuget);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reached = nuget.Packages.HoldNextChange(release.Task);
        Task<PackageOutcome> first = nuget.Packages.InstallAsync(Named(window, "Lib"), Id, "1.1.0");
        try
        {
            Ui.PumpUntil(() => reached.IsCompleted, "Lib's install to be under way");
            Button install = Action(nuget, "App", "Install") ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on App's line");
            Assert.True(!install.IsEnabled && nuget.Status == "Working…",
                $"While Lib's install runs, App's Install is {(install.IsEnabled ? "enabled" : "disabled")} and the status says \"{nuget.Status}\"");
            Task<bool> second = nuget.InstallAsync(Named(window, "App"));
            Assert.False(Ui.AwaitPumped(second, "the window's install asked meanwhile"), "The window's install of App ran while Lib's was under way");
            Assert.True(nuget.Status == $"Already under way: Installing {Id} 1.1.0 in Lib…",
                $"The window's install refused while Lib's ran says \"{nuget.Status}\", not what is under way");
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True(Ui.Landed(first, "Lib's install").Done, nuget.Log);
        Ui.Landed(() => Action(nuget, "App", "Install") is { IsEnabled: true }, "App's Install enabled again once Lib's install ended");
        Assert.Contains(Id, File.ReadAllText(lib), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A change refused because an open file would not save (Windows refuses a write to a file another program
    /// holds, stood in for here) says why in the window's status, never "Working…" left unexplained, and leaves the
    /// projects' actions enabled and the project as it was.</summary>
    [AvaloniaFact]
    public void A_change_refused_as_an_open_file_would_not_save_says_why_and_leaves_the_actions_enabled()
    {
        const string Unsaved = "Program.cs could not be saved: The process cannot access the file because it is being used by another process.";
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Search(nuget);
        string before = File.ReadAllText(lib);
        Func<Passenger.App.Working?, CancellationToken, Task<string?>>? saves = nuget.Packages.SaveFirst;
        nuget.Packages.SaveFirst = (_, _) => Task.FromResult<string?>(Unsaved);

        Ui.Click(Action(nuget, "Lib", "Install") ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on Lib's line"), settle: false);
        Ui.Landed(() => !nuget.IsBusy, "the refused install's end");
        Assert.True(nuget.Status == Unsaved, $"The install refused as a file would not save says \"{nuget.Status}\", not why");
        Assert.True(Action(nuget, "Lib", "Install") is { IsEnabled: true }, "Lib's Install is left disabled after the refused install");
        Assert.Equal(before, File.ReadAllText(lib));
        nuget.Packages.SaveFirst = saves;
        window.Close();
    }

    /// <summary>A project of the folder's own, with the item group given.</summary>
    private string Project(string name, string items)
    {
        Directory.CreateDirectory(Path.Combine(folder, name));
        string path = Path.Combine(folder, name, $"{name}.csproj");
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n{items}</Project>\n");
        return path;
    }

    /// <summary>A window on the folder with the NuGet window shown; without the C# language server, which nothing here asks
    /// of, so none starts and restores the projects.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.Perform(AppActions.ShowNuGet);
        Ui.Settle();
        return window;
    }

    /// <summary>The stand-in's package found and chosen, its versions read.</summary>
    private static void Search(NuGetView nuget)
    {
        nuget.Query = "vulnerable";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} and its versions: {nuget.Status}");
    }

    private static ProjectModel Named(MainWindow window, string name) => window.Projects.Single(p => p.Name == name);

    /// <summary>The button of a label on a project's line among the chosen package's projects.</summary>
    private static Button? Action(NuGetView nuget, string project, string label) => nuget.GetLogicalDescendants().OfType<StackPanel>()
        .Where(line => line.Children.FirstOrDefault() is TextBlock { Text: { } text } && text == project)
        .SelectMany(line => line.Children.OfType<Button>())
        .FirstOrDefault(button => (string?)button.Content == label);
}
