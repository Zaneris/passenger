using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Packages;
using Passenger.App.Run;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

/// <summary>What a project loads with, said as Rider says it. A package with a known vulnerability (from a stand-in feed
/// whose VulnerabilityInfo restore's audit reads, so nothing reaches nuget.org) is a warning naming the package and its
/// severity in the status bar, under the project with its advisory's link in Problems' Toolset, Environment, and marked in
/// the NuGet window, which offers the version that fixes it; updating to it takes the warning away. A project whose SDK
/// cannot be found is an error naming why, in the status bar and under the project in Problems.</summary>
public sealed class ProjectLoadTests : IDisposable
{
    private const string Advisory = "https://github.com/advisories/GHSA-pass-load-test";

    private const string Warned = "Package 'Passenger.Vulnerable' 1.0.0 has a known moderate severity vulnerability, " + Advisory;

    private readonly string folder = TestFolders.Create("load-");
    private readonly StandInFeed feed = new();
    private readonly string project;

    public ProjectLoadTests()
    {
        feed.Add("Passenger.Vulnerable", "1.0.0");
        feed.Add("Passenger.Vulnerable", "1.0.1");
        feed.Add("Passenger.Vulnerable", "1.1.0");
        feed.Advise("Passenger.Vulnerable", 1, Advisory, "[1.0.0, 1.0.1)");
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
        Directory.CreateDirectory(Path.Combine(folder, "Web"));
        project = Path.Combine(folder, "Web", "Web.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(folder, "Web", "Page.cs"), "namespace Web;\n\npublic static class Page\n{\n    public static string Title => \"Villagely\";\n}\n");
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_package_with_a_known_vulnerability_is_a_warning_in_the_status_bar_and_problems_with_its_advisory_and_nuget_offers_its_fix(bool light)
    {
        Restore.Run(project);
        MainWindow window = Open();
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        Ui.WaitFor(() => window.Status?.Contains(Warned, StringComparison.Ordinal) == true,
            $"the status bar to name the vulnerable package and its severity (it says \"{window.Status}\")");
        Assert.Equal($"Warning while loading {Path.Combine("Web", "Web.csproj")}: {Warned}", window.Status);
        Assert.True(window.StatusSeverity == ProblemSeverity.Warning, $"The status bar's load warning is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not a warning");

        // Problems' Toolset, Environment: under the project, with the advisory's link, which opens in the browser.
        ProblemsView problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ShowProblems);
        problems.Shown = ProblemsTab.Toolset;
        Ui.Settle();
        Problem warning = Assert.Single(problems.ToolsetProblems);
        Assert.True(warning is { Severity: ProblemSeverity.Warning, Project: "Web", Message: Warned } && warning.Link == new Uri(Advisory),
            $"Problems lists the load's warning as {warning.Severity} \"{warning.Message}\" under {warning.Project}, its link {warning.Link}");
        Assert.Equal(["Web", Warned], problems.Rows.Select(r => r.Label));
        Button link = Links(problems).SingleOrDefault(b => (string?)b.Content == Advisory)
            ?? throw new Xunit.Sdk.XunitException("The warning's row in Problems shows no link to the advisory");
        ListBox list = Descendants<ListBox>(problems).Single();
        ProblemRow row = Assert.Single(problems.Rows, item => item.Problem == warning);
        foreach (double width in new double[] { 1200, 683, 640, 1200, 640 })
        {
            window.Width = width;
            Ui.Settle();
            ListBoxItem item = Assert.Single(Descendants<ListBoxItem>(list), item => item.DataContext == row);
            foreach (TextBlock label in Descendants<TextBlock>(item).Where(text => !string.IsNullOrEmpty(text.Text)))
            {
                Point labelAt = label.TranslatePoint(default, list)!.Value;
                Assert.True(labelAt.X >= 0 && labelAt.X + label.Bounds.Width <= list.Bounds.Width,
                    $"The vulnerability label '{label.Text}' escapes its actual list at {width}px");
                Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, "A vulnerability's message, link and location must ellipsize inside their row");
            }
            Assert.Contains(Warned, item.GetVisualDescendants().OfType<Control>().Select(ToolTip.GetTip).OfType<string>());
            UiAudit.Capture(window, $"problem-advisory-{width:0}-{(light ? "light" : "dark")}");
        }
        list.SelectedItem = row;
        Ui.ChooseFromMenu(list, "Copy Message");
        Ui.WaitFor(() => Ui.Clipboard(window) == Warned, "the complete untrimmed vulnerability message on the clipboard");
        Point at = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Assert.True(link.IsPointerOver, "The advisory link must remain an actual pointer target in the 640px window");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.True(window.Browsed.Contains(new Uri(Advisory)), $"Clicking the advisory's link opened [{string.Join(", ", window.Browsed)}]");
        window.Width = 1200;
        Ui.Settle();

        // The NuGet window marks the package, says what Rider says of it, and offers the version that fixes it.
        NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => nuget.Selected is { Id: "Passenger.Vulnerable", IsVulnerable: true } && nuget.Versions.Count == 3, "the vulnerable package marked and its versions");
        Advisory advisory = Assert.Single(nuget.Selected!.Advisories);
        Assert.True(advisory.Severity == AdvisorySeverity.Moderate && advisory.Url == new Uri(Advisory), $"The NuGet window has the package's advisory as {advisory}");
        Assert.True(nuget.Status == "1 package installed, 1 vulnerable", $"The NuGet window's status says \"{nuget.Status}\"");
        Assert.True(nuget.ChosenVersion == "1.0.1", $"The NuGet window offers {nuget.ChosenVersion}, not 1.0.1, the version that fixes the vulnerability");
        Assert.True(Labels(nuget).Contains("Update"), $"Web's line offers [{string.Join(", ", Labels(nuget))}], not Update");
        List<string> said = Texts(nuget);
        Assert.True(said.Contains("Known security vulnerability in 1.0.0. It could be exploited in certain scenarios. See the advisories below for details.")
            && said.Contains("(Severity: Moderate)") && said.Contains("Fixed in 1.0.1"),
            $"The package's details do not say Rider's words on its vulnerability, its severity and its fix: [{string.Join(" | ", said)}]");
        Links(nuget).Single(b => (string?)b.Content == Advisory).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Browsed.Count(u => u == new Uri(Advisory)) == 2, "The NuGet window's advisory link did not open the advisory");
        window.Close();
    }

    [AvaloniaFact]
    public void Updating_a_vulnerable_package_to_the_version_that_fixes_it_takes_its_warning_away()
    {
        Restore.Run(project);
        MainWindow window = Open();
        ProblemsView problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.ToolsetProblems.Count == 1, "the vulnerable package's warning in Problems");
        NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => nuget.Selected is { IsVulnerable: true } && nuget.ChosenVersion == "1.0.1", "the vulnerable package and its fix offered");

        Button update = Descendants<StackPanel>(nuget).Where(l => l.Children.FirstOrDefault() is TextBlock { Text: "Web" })
            .SelectMany(l => l.Children.OfType<Button>()).FirstOrDefault(b => (string?)b.Content == "Update")
            ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Update on Web's line");
        update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.WaitFor(() => !nuget.IsBusy && Version(window) == "1.0.1", $"Web updated to 1.0.1:\n{nuget.Log}", TimeSpan.FromMinutes(3));

        // The server loads the project again, without the warning, as the update's restore left it.
        Ui.WaitFor(() => problems.ToolsetProblems.Count == 0, $"the warning gone from Problems once the project loaded again ({problems.ToolsetProblems.FirstOrDefault()?.Message})",
            TimeSpan.FromMinutes(2));
        Assert.True(window.Status == $"{Path.Combine("Web", "Web.csproj")} loaded without problems" && window.StatusSeverity is null,
            $"Once the project loaded without its warning the status bar says \"{window.Status}\" ({window.StatusSeverity})");
        Ui.Landed(() => nuget.Selected is { Id: "Passenger.Vulnerable", IsVulnerable: false }, "the updated package no longer marked vulnerable");
        Assert.True(nuget.Status == "1 package installed", $"The NuGet window's status says \"{nuget.Status}\" after the update");
        Assert.DoesNotContain(Descendants<Border>(nuget), b => b.Name == "Vulnerability");
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_whose_sdk_cannot_be_found_is_an_error_naming_why_in_the_status_bar_and_under_it_in_problems()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Bad"));
        string bad = Path.Combine(folder, "Bad", "Bad.csproj");
        File.WriteAllText(bad, "<Project Sdk=\"Missing.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.Delete(project);
        MainWindow window = Open();
        const string Reason = "The SDK 'Missing.Sdk' specified could not be found.";
        Assert.True(window.Status == $"Error while loading {Path.Combine("Bad", "Bad.csproj")}: {Reason}",
            $"The status bar says \"{window.Status}\" once the projects loaded, not that Bad.csproj did not load and why");
        Assert.True(window.StatusSeverity == ProblemSeverity.Error, $"The status bar's load error is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not an error");

        ProblemsView problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ShowProblems);
        problems.Shown = ProblemsTab.Toolset;
        Ui.Settle();
        Problem error = Assert.Single(problems.ToolsetProblems);
        Assert.True(error is { Severity: ProblemSeverity.Error, Project: "Bad", Message: Reason, Link: null },
            $"Problems lists the load's error as {error.Severity} \"{error.Message}\" under {error.Project}");
        Assert.Equal(["Bad", Reason], problems.Rows.Select(r => r.Label));

        // Going to it opens the project file.
        ListBox list = Descendants<ListBox>(problems).First();
        list.SelectedItem = problems.Rows.Single(r => r.Problem is not null);
        Ui.ChooseFromMenu(list, "Go to");
        Ui.Settle();
        Assert.True(window.EditorArea.Active?.Path is { } opened && Passenger.Core.PathNames.Same(opened, Passenger.Core.PathNames.Canonical(bad)),
            $"Going to the load error opened {window.EditorArea.Active?.Path}, not Bad.csproj");
        window.Close();
    }

    /// <summary>A window on the folder, its projects loaded by the language server.</summary>
    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})");
        Ui.Settle();
        return window;
    }

    private static IEnumerable<T> Descendants<T>(Control parent) => parent.GetVisualDescendants().OfType<T>();

    private static IEnumerable<Button> Links(Control parent) => Descendants<Button>(parent).Where(b => b.Classes.Contains("link"));

    /// <summary>The words on the window's buttons, the projects' Install, Update, Downgrade and Remove among them.</summary>
    private static List<string> Labels(NuGetView nuget) => [.. Descendants<Button>(nuget).Select(b => b.Content).OfType<string>()];

    private static List<string> Texts(NuGetView nuget) => [.. nuget.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).OfType<string>()];

    private static string? Version(MainWindow window) =>
        window.Projects.SelectMany(p => p.Packages).Where(p => p.Name == "Passenger.Vulnerable").Select(p => p.Version).FirstOrDefault();
}
