using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Packages;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The NuGet window over two projects and a stand-in feed whose VulnerabilityInfo the audit reads, so nothing
/// reaches nuget.org: a package is shown, compared and audited at each of a project's frameworks' versions, a range at the
/// version its restore resolved, else its least, and a change of a pinned version keeps it pinned; a change runs one at a
/// time, the projects' actions disabled meanwhile, and one refused says why.</summary>
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
        lib = Project("Lib", TestProjects.Framework, string.Empty);
        Project("App", TestProjects.Framework, string.Empty);
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    /// <summary>A project pinning the vulnerable 1.0.0 ("[1.0.0]") has it shown as 1.0.0 and marked, is offered the fix as
    /// an Update and nothing for 1.0.0 itself, and the Update pins 1.0.1, never turning the pin into a minimum.</summary>
    [AvaloniaFact]
    public void A_pinned_version_is_shown_audited_and_compared_as_that_version_and_an_update_keeps_it_pinned()
    {
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"[1.0.0]\" />\n  </ItemGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(nuget.Selected!.IsVulnerable && ShownVersion(nuget, "Lib") == "1.0.0",
            $"Lib's pinned [1.0.0] is shown as {ShownVersion(nuget, "Lib")}, {(nuget.Selected.IsVulnerable ? "marked" : "unmarked")}, not as the vulnerable 1.0.0");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Labels(nuget, "Lib").SequenceEqual(["Update", "Remove"]),
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1, which fixes 1.0.0, as an Update");

        Choose(nuget, "1.0.0");
        Assert.True(Labels(nuget, "Lib").SequenceEqual(["Remove"]), $"The version Lib pins is offered as [{string.Join(", ", Labels(nuget, "Lib"))}]");
        Choose(nuget, "1.0.1");
        Ui.Click(Action(nuget, "Lib", "Update")!, settle: false);
        Ui.Landed(() => !nuget.IsBusy && ShownVersion(nuget, "Lib") == "1.0.1", $"Lib updated to 1.0.1:\n{nuget.Log}");
        Assert.True(File.ReadAllText(lib).Contains($"Include=\"{Id}\" Version=\"[1.0.1]\"", StringComparison.Ordinal),
            $"The update of a pinned version did not pin the new one:\n{File.ReadAllText(lib)}");
        Assert.False(nuget.Selected?.IsVulnerable, "The package pinned to 1.0.1, which fixes 1.0.0, is still marked vulnerable");
        window.Close();
    }

    /// <summary>A range whose least no feed holds ("[0.9.0, 2.0.0)") is shown and audited as the 1.0.0 its restore
    /// resolved, read from the project's project.assets.json, and offered the fix as an Update.</summary>
    [AvaloniaFact]
    public void A_range_is_shown_and_audited_as_the_version_its_restore_resolved()
    {
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"[0.9.0, 2.0.0)\" />\n  </ItemGroup>\n");
        Restore.Run(lib);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(ShownVersion(nuget, "Lib") == "1.0.0" && nuget.Selected!.IsVulnerable,
            $"Lib's [0.9.0, 2.0.0), restored as 1.0.0, is shown as {ShownVersion(nuget, "Lib")}, {(nuget.Selected!.IsVulnerable ? "marked" : "unmarked")}");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Action(nuget, "Lib", "Update") is not null,
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1 as an Update");
        window.Close();
    }

    /// <summary>A project built for two frameworks, the package at 1.1.0 for its first and at a vulnerable 1.0.0 for its
    /// second, shows both versions on its line, marked, the package's row and the status counting it vulnerable, and
    /// offers the version that fixes it as an Update.</summary>
    [AvaloniaFact]
    public void A_package_whose_version_differs_by_framework_shows_each_and_is_marked_vulnerable_when_any_is()
    {
        Project("Lib", $"net11.0;{TestProjects.Framework}",
            $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageReference Include=\"{Id}\" Version=\"1.1.0\" />\n  </ItemGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == '{TestProjects.Framework}'\">\n    <PackageReference Include=\"{Id}\" Version=\"1.0.0\" />\n  </ItemGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(nuget.Selected!.IsVulnerable && nuget.Status == "1 package installed, 1 vulnerable",
            $"With {TestProjects.Framework}'s 1.0.0 vulnerable, the package's row is {(nuget.Selected.IsVulnerable ? "marked" : "unmarked")} and the status says \"{nuget.Status}\"");
        Assert.True(ShownVersion(nuget, "Lib") == "1.1.0, 1.0.0", $"Lib's line shows {ShownVersion(nuget, "Lib")}, not each framework's version");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Action(nuget, "Lib", "Update") is not null,
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1, which fixes {TestProjects.Framework}'s 1.0.0, as an Update");

        // The second framework's version alone changed on disk, as a person edits the project.
        File.WriteAllText(lib, File.ReadAllText(lib).Replace("Version=\"1.0.0\"", "Version=\"1.0.1\"", StringComparison.Ordinal));
        Ui.WaitFor(() => ShownVersion(nuget, "Lib") == "1.1.0, 1.0.1",
            () => $"Lib's line to show {TestProjects.Framework}'s 1.0.1 written on disk once the project is evaluated again (it shows {ShownVersion(nuget, "Lib")})");
        Assert.False(nuget.Selected?.IsVulnerable, $"With {TestProjects.Framework} at 1.0.1, which fixes 1.0.0, the package is still marked vulnerable");
        window.Close();
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

    /// <summary>A project of the folder's own for its frameworks, with the item groups given.</summary>
    private string Project(string name, string frameworks, string items)
    {
        Directory.CreateDirectory(Path.Combine(folder, name));
        string path = Path.Combine(folder, name, $"{name}.csproj");
        string property = frameworks.Contains(';', StringComparison.Ordinal) ? "TargetFrameworks" : "TargetFramework";
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <{property}>{frameworks}</{property}>\n  </PropertyGroup>\n{items}</Project>\n");
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

    /// <summary>A version chosen in the package's version picker, as a person picks it.</summary>
    private static void Choose(NuGetView nuget, string version)
    {
        ComboBox picker = nuget.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "Versions");
        picker.SelectedItem = version;
        Ui.Settle();
    }

    private static ProjectModel Named(MainWindow window, string name) => window.Projects.Single(p => p.Name == name);

    /// <summary>A project's line among the chosen package's projects.</summary>
    private static StackPanel? Line(NuGetView nuget, string project) => nuget.GetLogicalDescendants().OfType<StackPanel>()
        .FirstOrDefault(line => line.Children.FirstOrDefault() is TextBlock { Text: { } text } && text == project);

    /// <summary>The button of a label on a project's line.</summary>
    private static Button? Action(NuGetView nuget, string project, string label) =>
        Line(nuget, project)?.Children.OfType<Button>().FirstOrDefault(button => (string?)button.Content == label);

    /// <summary>The words on a project's line's buttons.</summary>
    private static IEnumerable<string> Labels(NuGetView nuget, string project) =>
        Line(nuget, project)?.Children.OfType<Button>().Select(b => b.Content).OfType<string>() ?? [];

    /// <summary>The version a project's line shows for the chosen package: the one in force there, or nothing.</summary>
    private static string? ShownVersion(NuGetView nuget, string project) => Line(nuget, project)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text;
}
