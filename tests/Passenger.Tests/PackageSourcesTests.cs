using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Packages;
using Passenger.App.ToolWindows;
using Passenger.Packages;

namespace Passenger.Tests;

/// <summary>The NuGet window takes its feeds from the folder's nuget.config: a folder of packages and a private v3 feed on
/// a loopback port that asks for the credentials the file gives. Sources lists them, adds a feed leaving the rest of the
/// file as it was, and disables one, which leaves the chooser and search; the chooser searches All Feeds or one; an
/// install restores with the credentials, which nothing logs. The run's NuGet home stands in for the person's, so their
/// nuget.config is never read or written.</summary>
public sealed class PackageSourcesTests : NuGetFeedsFolder
{
    /// <summary>The Sources tab and the chooser list the folder's two feeds; a search of one feed finds only its packages and
    /// All Feeds both feeds'; a feed disabled leaves search until enabled again; and a feed added is written into the
    /// folder's nuget.config, the rest of the file kept, Local History keeping the file as it was before. One window, the
    /// feed added last, since nothing answers at its address.</summary>
    [AvaloniaFact]
    public void The_folders_feeds_are_listed_searched_one_or_all_disabled_and_enabled_and_a_new_one_written_into_its_nuget_config()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Assert.True(nuget.FeedChoices.SequenceEqual([FeedSet.All, "local", "private"]),
            $"The chooser beside the search offers [{string.Join(", ", nuget.FeedChoices)}], not All Feeds and the folder's two feeds");

        ShowSources(nuget);
        ConfigFile solution = nuget.Sources.Files[0];
        Assert.True(Core.PathNames.Same(solution.Path, Core.PathNames.Canonical(config)) && solution.Place == ConfigPlace.Solution,
            $"The Sources tab's first file is {solution.Path} ({solution.Place}), not the folder's own nuget.config");
        Assert.True(nuget.Sources.Chosen == solution, $"The Sources tab shows the feeds of {nuget.Sources.Chosen?.Path}, not the folder's own file");
        Assert.True(FeedRows(nuget).SequenceEqual(["local", "private"]), $"The folder's file shows the feeds [{string.Join(", ", FeedRows(nuget))}]");
        Assert.True(Toggle(nuget, "local").IsChecked == true && Toggle(nuget, "private").IsChecked == true, "A feed the file enables is shown disabled");
        Assert.True(Locks(nuget, "private") == 1 && Locks(nuget, "local") == 0, "Only the private feed holds credentials in the file, but its row does not say so");

        // The person's own nuget.config is never one of a test's: the run's NuGet home stands in for theirs.
        if (!OperatingSystem.IsWindows())
        {
            ConfigFile user = Assert.Single(nuget.Sources.Files, f => f.Place == ConfigPlace.User);
            Assert.True(Core.PathNames.IsUnder(user.Path, Core.PathNames.Canonical(TestRun.Root)),
                $"The NuGet window read the user's nuget.config at {user.Path}, which is the person's own, not the test run's");
        }

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

        byte[] bytes = File.ReadAllBytes(config);
        string before = File.ReadAllText(config);
        ShowSources(nuget);
        Button add = Descendants<Button>(nuget.Sources).Single(b => b.Name == "AddFeed");
        Ui.Click(add);
        FeedDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<FeedDialog>()));
        Assert.Equal("New feed", dialog.Title);
        dialog.FeedName = "team";
        dialog.Url = "https://nuget.example.com/v3/index.json";
        Ui.ClickButton(dialog, "OK");
        Ui.Landed(() => nuget.FeedChoices.Contains("team"), "the new feed in the chooser");

        string line = $"    <add key=\"private\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />";
        string expected = before.Replace(line + "\n", line + "\n    <add key=\"team\" value=\"https://nuget.example.com/v3/index.json\" />\n", StringComparison.Ordinal);
        Assert.True(File.ReadAllText(config) == expected, $"Adding the feed made the file:\n{File.ReadAllText(config)}\nnot:\n{expected}");
        Assert.True(FeedRows(nuget).SequenceEqual(["local", "private", "team"]), $"The Sources tab shows [{string.Join(", ", FeedRows(nuget))}] after the add");
        string path = Passenger.Core.PathNames.Canonical(config);
        Ui.WaitFor(() => window.LocalHistory!.Snapshot().Events.Any(e => e.Path == path && e.Before is { } hash && window.LocalHistory.Content(hash) is { } kept && kept.AsSpan().SequenceEqual(bytes)),
            "Local History to keep the nuget.config as it was before the feed went in");
        window.Close();
    }

    /// <summary>A feed's answers can send Passenger to any host: a search there that asks for credentials is refused, and
    /// the feed's own credentials never go to it.</summary>
    [AvaloniaFact]
    public void A_feeds_search_on_another_host_that_asks_for_credentials_is_refused_and_never_sent_the_feeds_credentials()
    {
        using StandInFeed elsewhere = new("someone", "else");
        feed.SearchAt = new Uri($"{elsewhere.Root}/query");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        nuget.ChosenFeed = "private";
        nuget.Query = "private";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted, "the private feed's search");
        Assert.True(nuget.Status.Contains("asks for credentials (401 Unauthorized)", StringComparison.Ordinal) && nuget.Rows.Count == 0,
            $"The private feed's search on another host that asks for credentials found [{string.Join(", ", Ids(nuget))}] and said \"{nuget.Status}\"");
        Assert.True(feed.Requests.Any(r => r.Path == "/v3/index.json" && r.Authorized), "The private feed's own service index was not read with its credentials");
        Assert.True(elsewhere.Requests.Count > 0 && elsewhere.Requests.All(r => r.Authorization is null),
            $"The other host was sent [{string.Join(", ", elsewhere.Requests.Select(r => r.Authorization ?? "nothing"))}] for the private feed's search");
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
        Ui.Click(install, settle: false);
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
}

/// <summary>A credential provider gives the NuGet window and restore what the folder's nuget.config does not. Alone: NuGet
/// and the IDE find a credential provider on this process's own PATH, which its test changes while it runs, so nothing may
/// run beside it and inherit that PATH.</summary>
[Collection(Alone.Name)]
public sealed class CredentialProviderTests : NuGetFeedsFolder
{
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
        // On PATH, where restore finds a provider that is a program of its own, so the restore gets the credentials too.
        string plugins = Directory.CreateDirectory(Path.Combine(folder, "plugins")).FullName;
        string plugin = Path.Combine(plugins, "nuget-plugin-passenger-tests");
        File.WriteAllText(plugin, Provider("provided", "from-provider"));
        File.SetUnixFileMode(plugin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? was = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", plugins + Path.PathSeparator + was);
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
            Environment.SetEnvironmentVariable("PATH", was);
        }
    }

    /// <summary>A credential provider as NuGet's plugin protocol has one, its own handshake included: a JSON message a line
    /// on its input, each request answered by its id; it says it authenticates and gives the feed's user and
    /// password.</summary>
    private static string Provider(string user, string password) => $$$"""
        #!/bin/sh
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"RequestId":"\([^"]*\)".*/\1/p')
          type=$(printf '%s' "$line" | sed -n 's/.*"Type":"\([^"]*\)".*/\1/p')
          method=$(printf '%s' "$line" | sed -n 's/.*"Method":"\([^"]*\)".*/\1/p')
          [ "$type" = "Request" ] || continue
          case "$method" in
            Handshake) payload='{"ResponseCode":"Success","ProtocolVersion":"2.0.0"}' ;;
            GetOperationClaims) payload='{"ResponseCode":"Success","Claims":["Authentication"]}' ;;
            GetAuthenticationCredentials) payload='{"ResponseCode":"Success","Username":"{{{user}}}","Password":"{{{password}}}","AuthenticationTypes":["Basic"]}' ;;
            Close) exit 0 ;;
            *) payload='{"ResponseCode":"Success"}' ;;
          esac
          printf '{"RequestId":"%s","Type":"Response","Method":"%s","Payload":%s}\n' "$id" "$method" "$payload"
          # The handshake goes both ways: restore waits for the provider's own, which Passenger answers as NuGet does.
          if [ "$method" = "Handshake" ]; then
            printf '{"RequestId":"handshake","Type":"Request","Method":"Handshake","Payload":{"ProtocolVersion":"2.0.0","MinimumProtocolVersion":"1.0.0"}}\n'
          fi
        done

        """;
}

/// <summary>A folder holding a project, a folder of packages and the nuget.config that names it and a private v3 feed on a
/// loopback port, which asks for the credentials the file gives; and a window's NuGet window on it.</summary>
public abstract class NuGetFeedsFolder : IDisposable
{
    protected const string User = "tester";

    protected readonly string folder = TestFolders.Create("sources-");
    protected readonly string config;
    protected readonly string project;
    protected readonly StandInFeed feed = new(User, "secret-" + Guid.NewGuid().ToString("N")[..8]);

    protected NuGetFeedsFolder()
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
    protected string Config(bool withCredentials)
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

    /// <summary>A window on the folder with the NuGet window open, its sources read from the folder's nuget.config; without
    /// the C# language server, which nothing here asks of, so none starts and restores the project from the feeds.</summary>
    protected MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => window.NuGet.PackageSources.Feeds.Count > 0, "the folder's package sources read");
        return window;
    }

    protected static void ShowSources(NuGetView nuget)
    {
        ToggleButton tab = ((Panel)nuget.Tabs).Children.OfType<ToggleButton>().Single(t => (string?)t.Content == "Sources");
        Ui.Click(tab, settle: false);
        Ui.Landed(() => nuget.ShowsSources && nuget.Sources.Files.Count > 0, "the Sources tab with the folder's nuget.config files");
    }

    protected static void Search(NuGetView nuget, string query)
    {
        nuget.Query = query;
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected is not null && nuget.Versions.Count > 0, $"what '{query}' finds on {nuget.ChosenFeed}, its first package's versions: {nuget.Status}");
    }

    protected static List<string> Ids(NuGetView nuget) => [.. nuget.Rows.Select(r => r.Id)];

    protected static IEnumerable<T> Descendants<T>(Visual parent) => parent.GetVisualDescendants().OfType<T>();

    /// <summary>The feeds the Sources tab shows for the file chosen, in order.</summary>
    protected static List<string> FeedRows(NuGetView nuget) => [.. Descendants<Grid>(nuget.Sources).Select(g => g.Tag).OfType<string>()];

    protected static Grid FeedRow(NuGetView nuget, string name) =>
        Descendants<Grid>(nuget.Sources).SingleOrDefault(g => g.Tag as string == name) ?? throw new Xunit.Sdk.XunitException($"The Sources tab shows no feed {name}");

    protected static CheckBox Toggle(NuGetView nuget, string name) => FeedRow(nuget, name).Children.OfType<CheckBox>().Single();

    protected static int Locks(NuGetView nuget, string name) =>
        FeedRow(nuget, name).Children.OfType<PathIcon>().Count(i => ToolTip.GetTip(i) is string tip && tip.StartsWith("Credentials", StringComparison.Ordinal));

    protected static string? Version(MainWindow window) =>
        window.Projects.SelectMany(p => p.Packages).Where(p => p.Name == "Passenger.Private").Select(p => p.Version).FirstOrDefault();
}
