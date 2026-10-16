using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Packages;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;

namespace Passenger.Tests;

/// <summary>The NuGet window takes its feeds from the folder's nuget.config, as restore does: a folder of packages and a
/// private v3 feed on a loopback port that asks for the credentials the file gives. Sources lists them, adds a feed leaving
/// the rest of the file as it was, and disables one, which leaves the chooser and search; the chooser searches All Feeds or
/// one; an install restores with the credentials, which nothing logs; a credential provider gives what the file does not.
/// The run's NuGet home stands in for the person's, so their nuget.config is never read or written.</summary>
public sealed class PackageSourcesTests : IDisposable
{
    private const string User = "tester";

    private readonly string folder = TestFolders.Create("sources-");
    private readonly string config;
    private readonly string project;
    private readonly StandInFeed feed = new(User, "secret-" + Guid.NewGuid().ToString("N")[..8]);

    public PackageSourcesTests()
    {
        string local = Path.Combine(folder, "feed");
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllBytes(Path.Combine(local, "Passenger.Sample.1.1.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.1.0"));
        File.WriteAllBytes(Path.Combine(local, "Passenger.Other.2.0.0.nupkg"), StandInFeed.Package("Passenger.Other", "2.0.0"));
        feed.Add("Passenger.Private", "3.0.0");
        feed.Add("Passenger.Private", "3.1.0");
        config = Path.Combine(folder, "nuget.config");
        File.WriteAllText(config, Config(withCredentials: true));
        Directory.CreateDirectory(Path.Combine(folder, "Lib"));
        project = Path.Combine(folder, "Lib", "Lib.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    /// <summary>The folder's nuget.config: only its own feeds, the private one over HTTP as NuGet allows it with
    /// allowInsecureConnections, and restores kept out of the person's package cache.</summary>
    private string Config(bool withCredentials)
    {
        string credentials = withCredentials ? $"""
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="{User}" />
                  <add key="ClearTextPassword" value="{feed.Password}" />
                </private>
              </packageSourceCredentials>

            """ : string.Empty;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- The tests' feeds: a folder of packages and a private feed that asks who is there. -->
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
                <add key="private" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
            {credentials}  <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>

            """.ReplaceLineEndings("\n");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_sources_tab_and_the_chooser_list_the_folder_feed_and_the_private_feed_of_the_folders_nuget_config(bool light)
    {
        const string name = "A shared development package feed whose complete readable name must remain available";
        string local = Path.Combine(folder, "package sources for development teams", "a local directory whose complete location must remain readable");
        Directory.CreateDirectory(local);
        foreach (string package in Directory.GetFiles(Path.Combine(folder, "feed")))
        {
            File.Copy(package, Path.Combine(local, Path.GetFileName(package)));
        }
        string packages = Path.Combine(folder, "global package caches", "a development package cache whose full location must remain readable");
        string repository = Path.Combine(folder, "repository packages", "a legacy package location whose full path must remain readable");
        XDocument contents = XDocument.Load(config);
        XElement source = contents.Root!.Element("packageSources")!.Elements("add").First();
        source.SetAttributeValue("key", name);
        source.SetAttributeValue("value", local);
        contents.Root.Element("config")!.Elements("add").Single().SetAttributeValue("value", packages);
        contents.Root.Element("config")!.Add(new XElement("add", new XAttribute("key", "repositoryPath"), new XAttribute("value", repository)));
        contents.Save(config);
        MainWindow window = Open();
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        NuGetView nuget = window.NuGet;
        Assert.True(nuget.FeedChoices.SequenceEqual([FeedSet.All, name, "private"]),
            $"The chooser beside the search offers [{string.Join(", ", nuget.FeedChoices)}], not All Feeds and the folder's two feeds");

        ShowSources(nuget);
        ConfigFile solution = nuget.Sources.Files[0];
        Assert.True(Core.PathNames.Same(solution.Path, Core.PathNames.Canonical(config)) && solution.Place == ConfigPlace.Solution,
            $"The Sources tab's first file is {solution.Path} ({solution.Place}), not the folder's own nuget.config");
        Assert.True(nuget.Sources.Chosen == solution, $"The Sources tab shows the feeds of {nuget.Sources.Chosen?.Path}, not the folder's own file");
        Assert.True(FeedRows(nuget).SequenceEqual([name, "private"]), $"The folder's file shows the feeds [{string.Join(", ", FeedRows(nuget))}]");
        Assert.True(Toggle(nuget, name).IsChecked == true && Toggle(nuget, "private").IsChecked == true, "A feed the file enables is shown disabled");
        Assert.True(Locks(nuget, "private") == 1 && Locks(nuget, name) == 0, "Only the private feed holds credentials in the file, but its row does not say so");

        ScrollViewer reader = Assert.Single(Descendants<ScrollViewer>(nuget.Sources), scroll => scroll.Content is StackPanel);
        Button edit = FeedRow(nuget, name).Children.OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Edit");
        Assert.True(edit.Focus());
        Ui.Settle();
        reader.Offset = default;
        List<string> problems = [];
        foreach (double width in new double[] { 1200, 683, 640 })
        {
            window.Width = width;
            Ui.Settle();
            Assert.Same(solution, nuget.Sources.Chosen);
            Assert.True(Toggle(nuget, name).IsChecked == true);
            Assert.Same(edit, window.FocusManager?.GetFocusedElement());
            Assert.Contains(Descendants<TextBlock>(nuget.Sources), text => text.Text == solution.Path);
            UiAudit.Capture(window, $"nuget-sources-{width:0}-{(light ? "light" : "dark")}");
            problems.AddRange(UiAudit.HorizontalOverflow(window, "NuGet Sources", nuget.Sources));
            foreach (TextBlock text in Descendants<TextBlock>(nuget.Sources).Where(text => text.Text is { } value && (value == name || value == local || value == packages || value == repository)))
            {
                if (text.TextTrimming != TextTrimming.None)
                {
                    problems.Add($"NuGet Sources truncates '{text.Text}' at {width}px");
                }
            }
            Point over = reader.TranslatePoint(new Point(reader.Bounds.Width / 2, reader.Bounds.Height / 2), window)!.Value;
            window.MouseMove(over);
            for (int attempt = 0; attempt < 20 && reader.Offset.X < reader.Extent.Width - reader.Viewport.Width - 1; attempt++)
            {
                window.MouseWheel(over, new Vector(-30, 0));
                Ui.Settle();
            }
            Assert.InRange(reader.Offset.X, reader.Extent.Width - reader.Viewport.Width - 1, reader.Extent.Width - reader.Viewport.Width + 1);
            double at = edit.TranslatePoint(default, reader)!.Value.X;
            Assert.True(at >= 0 && at + edit.Bounds.Width <= reader.Bounds.Width, "The feed's Edit action cannot be reached inside the Sources reading viewport");
            UiAudit.Capture(window, $"nuget-sources-actions-{width:0}-{(light ? "light" : "dark")}");
            window.MouseWheel(over, new Vector(0, -30));
            Ui.Settle();
            Assert.True(reader.Offset.Y > 0, "The Sources properties and Summary have no vertical reading access");
            UiAudit.Capture(window, $"nuget-sources-properties-{width:0}-{(light ? "light" : "dark")}");
            reader.Offset = default;
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        DockLayout layout = window.DockHost.Capture();
        window.DockHost.Apply(layout with
        {
            LeftWidth = 200,
            Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "nuget"
                ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                : pair.Value.Anchor == ToolAnchor.LeftTop ? pair.Value with { Shown = false } : pair.Value),
        });
        Ui.Settle();
        UiAudit.Capture(window, $"nuget-sources-side-200-{(light ? "light" : "dark")}");
        Assert.InRange(nuget.Bounds.Width, 190, 210);
        Assert.True(reader.Viewport.Width >= 40, $"The file list leaves only {reader.Viewport.Width}px for the Sources form's reading viewport");
        Point side = reader.TranslatePoint(new Point(reader.Bounds.Width / 2, reader.Bounds.Height / 2), window)!.Value;
        window.MouseMove(side);
        for (int attempt = 0; attempt < 20 && reader.Offset.X < reader.Extent.Width - reader.Viewport.Width - 1; attempt++)
        {
            window.MouseWheel(side, new Vector(-30, 0));
            Ui.Settle();
        }
        UiAudit.Capture(window, $"nuget-sources-side-actions-200-{(light ? "light" : "dark")}");
        double actionX = edit.TranslatePoint(default, reader)!.Value.X;
        Assert.True(actionX >= 0 && actionX + edit.Bounds.Width <= reader.Bounds.Width, "The feed's Edit action cannot be reached inside the 200px Sources pane");
        ListBox fileList = Assert.Single(Descendants<ListBox>(nuget.Sources));
        Assert.True(fileList.ContainerFromIndex(0)!.Focus());
        Ui.Press(window, "down");
        Ui.Settle();
        Assert.NotSame(solution, nuget.Sources.Chosen);
        Assert.Contains(Descendants<TextBlock>(nuget.Sources), text => text.Text == nuget.Sources.Chosen!.Path);
        Ui.Press(window, "up");
        Ui.Settle();
        Assert.Same(solution, nuget.Sources.Chosen);
        Assert.Contains(Descendants<TextBlock>(nuget.Sources), text => text.Text == solution.Path);
        Button longEdit = FeedRow(nuget, name).Children.OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Edit");
        Assert.True(longEdit.Focus());
        Ui.Press(window, "enter");
        Ui.Settle();
        FeedDialog longDialog = Assert.Single(window.OwnedWindows.OfType<FeedDialog>());
        longDialog.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        longDialog.SetRenderScaling(2);
        Ui.Settle();
        Assert.Equal(name, longDialog.FeedName);
        Assert.Equal(local, longDialog.Url);
        foreach (string value in new[] { name, local })
        {
            TextBox field = Assert.Single(Descendants<TextBox>(longDialog), box => box.Text == value);
            Assert.True(field.Focus());
            Ui.Press(longDialog, "end");
            Assert.Equal(value.Length, field.CaretIndex);
            Ui.Press(longDialog, "home");
            Assert.Equal(0, field.CaretIndex);
            Assert.Equal(value, field.Text);
        }
        UiAudit.Capture(longDialog, $"nuget-feed-long-name-{(light ? "light" : "dark")}");
        string[] clipped = [.. UiAudit.HorizontalOverflow(longDialog, "Editing a long feed name")];
        Ui.Press(longDialog, "escape");
        Ui.Settle();
        Assert.True(clipped.Length == 0, string.Join(Environment.NewLine, clipped));
        Button privateEdit = FeedRow(nuget, "private").Children.OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Edit");
        Assert.True(privateEdit.Focus());
        Ui.Press(window, "enter");
        Ui.Settle();
        FeedDialog dialog = Assert.Single(window.OwnedWindows.OfType<FeedDialog>());
        Assert.Equal("private", dialog.FeedName);
        Assert.Equal(feed.Index.AbsoluteUri, dialog.Url);
        Ui.Press(dialog, "escape");
        Ui.Settle();
        Assert.Empty(window.OwnedWindows);
        Assert.Same(solution, nuget.Sources.Chosen);
        Assert.True(Toggle(nuget, "private").IsChecked == true);

        // The person's own nuget.config is never one of a test's: the run's NuGet home stands in for theirs.
        if (!OperatingSystem.IsWindows())
        {
            ConfigFile user = Assert.Single(nuget.Sources.Files, f => f.Place == ConfigPlace.User);
            Assert.True(Core.PathNames.IsUnder(user.Path, Core.PathNames.Canonical(TestRun.Root)),
                $"The NuGet window read the user's nuget.config at {user.Path}, which is the person's own, not the test run's");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Searching_one_feed_finds_only_its_packages_and_all_feeds_finds_both_feeds()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        nuget.ChosenFeed = "private";
        Search(nuget, "passenger");
        Assert.True(Ids(nuget).SequenceEqual(["Passenger.Private"]), $"Searching the private feed found [{string.Join(", ", Ids(nuget))}]");
        Assert.True(nuget.Versions.SequenceEqual(["3.1.0", "3.0.0"]), $"The private package's versions are [{string.Join(", ", nuget.Versions)}]");
        Assert.True(feed.Requests.Any(r => r.Path.StartsWith("/query?", StringComparison.Ordinal) && r.Authorized),
            "The private feed was never searched with the credentials its nuget.config gives");

        nuget.ChosenFeed = "local";
        Search(nuget, "passenger");
        Assert.True(Ids(nuget).Order().SequenceEqual(["Passenger.Other", "Passenger.Sample"]), $"Searching the folder feed found [{string.Join(", ", Ids(nuget))}]");

        nuget.ChosenFeed = FeedSet.All;
        Search(nuget, "passenger");
        Assert.True(Ids(nuget).Order().SequenceEqual(["Passenger.Other", "Passenger.Private", "Passenger.Sample"]), $"All Feeds found [{string.Join(", ", Ids(nuget))}]");
        Assert.True(nuget.Rows.Single(r => r.Id == "Passenger.Private").Feeds.SequenceEqual(["private"]), "The private package is not said to come from the private feed");
        window.Close();
    }

    [AvaloniaFact]
    public void Installing_from_the_private_feed_restores_with_its_credentials_which_nothing_logs()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        nuget.ChosenFeed = "private";
        Search(nuget, "private");
        Assert.True(nuget.Selected?.Id == "Passenger.Private", $"The search chose {nuget.Selected?.Id}, not the private package");

        Button install = Descendants<StackPanel>(nuget).Where(l => l.Children.FirstOrDefault() is TextBlock { Text: "Lib" })
            .SelectMany(l => l.Children.OfType<Button>()).FirstOrDefault(b => (string?)b.Content == "Install")
            ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on Lib's line");
        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.WaitFor(() => !nuget.IsBusy && Version(window) is not null, "the private package installed in Lib", TimeSpan.FromMinutes(3));
        Assert.True(Version(window) == "3.1.0", $"Lib has Passenger.Private {Version(window)}, not 3.1.0:\n{nuget.Log}");
        Assert.True(File.Exists(Path.Combine(folder, "packages", "passenger.private", "3.1.0", "passenger.private.3.1.0.nupkg")),
            $"The restore put no Passenger.Private 3.1.0 in the folder's packages:\n{nuget.Log}");
        Assert.True(feed.Requests.Any(r => r.Path == "/flat/passenger.private/3.1.0/passenger.private.3.1.0.nupkg" && r.Authorized),
            "The restore never downloaded the package with the private feed's credentials");

        Assert.DoesNotContain(feed.Password!, nuget.Log, StringComparison.Ordinal);
        Assert.DoesNotContain(feed.Password!, Ui.IdeLog(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Adding_a_feed_in_sources_writes_it_into_the_solutions_nuget_config_keeping_the_rest_of_the_file()
    {
        string before = File.ReadAllText(config);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        ShowSources(nuget);
        Button add = Descendants<Button>(nuget.Sources).Single(b => b.Name == "AddFeed");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
        FeedDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<FeedDialog>()));
        Assert.Equal("New feed", dialog.Title);
        dialog.FeedName = "team";
        dialog.Url = "https://nuget.example.com/v3/index.json";
        Descendants<Button>(dialog).Single(b => (string?)b.Content == "OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Landed(() => nuget.FeedChoices.Contains("team"), "the new feed in the chooser");

        string line = $"    <add key=\"private\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />";
        string expected = before.Replace(line + "\n", line + "\n    <add key=\"team\" value=\"https://nuget.example.com/v3/index.json\" />\n", StringComparison.Ordinal);
        Assert.True(File.ReadAllText(config) == expected, $"Adding the feed made the file:\n{File.ReadAllText(config)}\nnot:\n{expected}");
        Assert.True(FeedRows(nuget).SequenceEqual(["local", "private", "team"]), $"The Sources tab shows [{string.Join(", ", FeedRows(nuget))}] after the add");
        window.Close();
    }

    [AvaloniaFact]
    public void Disabling_a_feed_takes_it_out_of_search_and_enabling_it_brings_it_back()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        ShowSources(nuget);
        Toggle(nuget, "local").IsChecked = false;
        Ui.Landed(() => !nuget.FeedChoices.Contains("local"), "the folder feed gone from the chooser");
        Assert.Contains("<add key=\"local\" value=\"true\" />", File.ReadAllText(config), StringComparison.Ordinal);
        Assert.True(Toggle(nuget, "local").IsChecked == false, "The disabled feed is shown enabled");

        nuget.ShowPackages();
        nuget.ChosenFeed = FeedSet.All;
        Search(nuget, "passenger");
        Assert.True(Ids(nuget).SequenceEqual(["Passenger.Private"]), $"With the folder feed disabled, All Feeds found [{string.Join(", ", Ids(nuget))}]");

        ShowSources(nuget);
        Toggle(nuget, "local").IsChecked = true;
        Ui.Landed(() => nuget.FeedChoices.Contains("local"), "the folder feed back in the chooser");
        Assert.DoesNotContain("<add key=\"local\" value=\"true\" />", File.ReadAllText(config), StringComparison.Ordinal);
        nuget.ShowPackages();
        Search(nuget, "passenger");
        Assert.True(Ids(nuget).Order().SequenceEqual(["Passenger.Other", "Passenger.Private", "Passenger.Sample"]), $"With the folder feed enabled again, All Feeds found [{string.Join(", ", Ids(nuget))}]");
        window.Close();
    }

    [AvaloniaFact]
    public void A_feed_without_credentials_in_its_nuget_config_gets_them_from_a_credential_provider()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stand-in credential provider is a shell script, and Windows runs no shell script as a program");
            return;
        }
        File.WriteAllText(config, Config(withCredentials: false));
        feed.Accept("provided", "from-provider");
        string plugin = Path.Combine(folder, "nuget-plugin-passenger-tests");
        File.WriteAllText(plugin, Provider("provided", "from-provider"));
        File.SetUnixFileMode(plugin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? was = Environment.GetEnvironmentVariable("NUGET_NETCORE_PLUGIN_PATHS");
        Environment.SetEnvironmentVariable("NUGET_NETCORE_PLUGIN_PATHS", plugin);
        try
        {
            MainWindow window = Open();
            NuGetView nuget = window.NuGet;
            nuget.ChosenFeed = "private";
            Search(nuget, "passenger");
            Assert.True(Ids(nuget).SequenceEqual(["Passenger.Private"]), $"The private feed, its credentials from the provider, found [{string.Join(", ", Ids(nuget))}]; {nuget.Status}");
            Assert.True(feed.Requests.Any(r => r.Path.StartsWith("/query?", StringComparison.Ordinal) && r.Authorized),
                "The private feed was never searched with the credentials the provider gave");
            Assert.DoesNotContain("from-provider", Ui.IdeLog(), StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_NETCORE_PLUGIN_PATHS", was);
        }
    }

    /// <summary>A credential provider as NuGet's plugin protocol has one: a JSON message a line on its input, each
    /// request answered by its id; it says it authenticates and gives the feed's user and password.</summary>
    private static string Provider(string user, string password) => $$"""
        #!/bin/sh
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"RequestId":"\([^"]*\)".*/\1/p')
          type=$(printf '%s' "$line" | sed -n 's/.*"Type":"\([^"]*\)".*/\1/p')
          method=$(printf '%s' "$line" | sed -n 's/.*"Method":"\([^"]*\)".*/\1/p')
          [ "$type" = "Request" ] || continue
          case "$method" in
            Handshake) payload='{"ResponseCode":"Success","ProtocolVersion":"2.0.0"}' ;;
            GetOperationClaims) payload='{"ResponseCode":"Success","Claims":["Authentication"]}' ;;
            GetAuthenticationCredentials) payload='{"ResponseCode":"Success","Username":"{{user}}","Password":"{{password}}","AuthenticationTypes":["Basic"]}' ;;
            Close) exit 0 ;;
            *) payload='{"ResponseCode":"Success"}' ;;
          esac
          printf '{"RequestId":"%s","Type":"Response","Method":"%s","Payload":%s}\n' "$id" "$method" "$payload"
        done

        """;

    /// <summary>A window on the folder with the NuGet window open, its sources read from the folder's nuget.config.</summary>
    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => window.NuGet.PackageSources.Feeds.Count > 0, "the folder's package sources read");
        return window;
    }

    private static void ShowSources(NuGetView nuget)
    {
        ToggleButton tab = ((Panel)nuget.Tabs).Children.OfType<ToggleButton>().Single(t => (string?)t.Content == "Sources");
        tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Landed(() => nuget.ShowsSources && nuget.Sources.Files.Count > 0, "the Sources tab with the folder's nuget.config files");
    }

    private static void Search(NuGetView nuget, string query)
    {
        nuget.Query = query;
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected is not null && nuget.Versions.Count > 0, $"what '{query}' finds on {nuget.ChosenFeed}, its first package's versions: {nuget.Status}");
    }

    private static List<string> Ids(NuGetView nuget) => [.. nuget.Rows.Select(r => r.Id)];

    private static IEnumerable<T> Descendants<T>(Visual parent) => parent.GetVisualDescendants().OfType<T>();

    /// <summary>The feeds the Sources tab shows for the file chosen, in order.</summary>
    private static List<string> FeedRows(NuGetView nuget) => [.. Descendants<Grid>(nuget.Sources).Select(g => g.Tag).OfType<string>()];

    private static Grid FeedRow(NuGetView nuget, string name) =>
        Descendants<Grid>(nuget.Sources).SingleOrDefault(g => g.Tag as string == name) ?? throw new Xunit.Sdk.XunitException($"The Sources tab shows no feed {name}");

    private static CheckBox Toggle(NuGetView nuget, string name) => FeedRow(nuget, name).Children.OfType<CheckBox>().Single();

    private static int Locks(NuGetView nuget, string name) =>
        FeedRow(nuget, name).Children.OfType<PathIcon>().Count(i => ToolTip.GetTip(i) is string tip && tip.StartsWith("Credentials", StringComparison.Ordinal));

    private static string? Version(MainWindow window) =>
        window.Projects.SelectMany(p => p.Packages).Where(p => p.Name == "Passenger.Private").Select(p => p.Version).FirstOrDefault();
}
