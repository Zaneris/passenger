using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The NuGet window over a folder feed, offline: a search finds a package and its versions, Install puts it in
/// a project at the version chosen, Update moves it, Remove takes it out, each through the .NET command line and each
/// read back from the project file. Versions are offered in SemVer 2's order and without those nuget.org has unlisted
/// (its answers recorded), and a package's details are shown once however fast the choices change. Under central
/// package management a version is read from Directory.Packages.props, and an install writes its version there.</summary>
public sealed class PackagesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("nuget-");
    private readonly string feed;
    private readonly string project;

    public PackagesTests()
    {
        feed = Path.Combine(folder, "feed");
        Directory.CreateDirectory(feed);
        Package("Passenger.Sample", "1.0.0");
        Package("Passenger.Sample", "1.1.0");
        Package("Passenger.Other", "2.0.0");
        // Only the folder feed, and restores kept out of the user's own package cache.
        File.WriteAllText(Path.Combine(folder, "nuget.config"), """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
              </packageSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        Directory.CreateDirectory(Path.Combine(folder, "Lib"));
        project = Path.Combine(folder, "Lib", "Lib.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>In a window opened on the folder above the project, which no solution names: a package is installed at the
    /// version chosen, updated and removed, then installed again from the Install on the project's line.</summary>
    [AvaloniaFact]
    public void A_package_found_in_the_feed_is_installed_updated_and_removed_in_a_project_below_a_folder_with_no_solution()
    {
        MainWindow window = Open(new FolderFeed(feed), folder);
        NuGetView nuget = window.NuGet;
        nuget.Query = "sample";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Versions.Count == 2, "the package and its versions");
        Assert.Equal("Passenger.Sample", Assert.Single(nuget.Rows).Id);
        Assert.Equal(["1.1.0", "1.0.0"], nuget.Versions);

        ProjectModel lib = Assert.Single(window.Projects);
        nuget.ChosenVersion = "1.0.0";
        Run(nuget, nuget.InstallAsync(lib));
        Assert.Equal("1.0.0", Version(window));

        Ui.Landed(() => nuget.Selected?.IsInstalled == true && nuget.Versions.Count == 2, "the package marked installed");
        nuget.ChosenVersion = "1.1.0";
        Run(nuget, nuget.InstallAsync(Assert.Single(window.Projects)));
        Assert.Equal("1.1.0", Version(window));

        Ui.Landed(() => nuget.Selected?.IsInstalled == true, "the package still chosen");
        Run(nuget, nuget.RemoveAsync(Assert.Single(window.Projects)));
        Assert.Null(Version(window));
        Assert.DoesNotContain("Passenger.Sample", File.ReadAllText(project), StringComparison.Ordinal);

        Ui.Landed(() => !nuget.IsBusy && nuget.Versions.Count == 2, "the package's versions once it was removed");
        nuget.ChosenVersion = "1.1.0";
        Button install = InstallButton(nuget, "Lib") ?? throw new Xunit.Sdk.XunitException("The NuGet window lists no Lib project to install the package in");
        Ui.Click(install, settle: false);
        Ui.Landed(() => !nuget.IsBusy && Version(window) is not null, "the package installed in Lib");
        Assert.Equal("1.1.0", Version(window));
        Assert.Contains("Passenger.Sample", File.ReadAllText(project), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>The Install button on a project's line among the chosen package's projects.</summary>
    private static Button? InstallButton(NuGetView nuget, string name) => nuget.GetLogicalDescendants().OfType<StackPanel>()
        .Where(line => line.Children.FirstOrDefault() is TextBlock { Text: { } text } && text == name)
        .SelectMany(line => line.Children.OfType<Button>())
        .FirstOrDefault(button => (string?)button.Content == "Install");

    [AvaloniaFact]
    public void Prereleases_are_offered_as_semver_orders_them_so_beta_10_is_an_update_from_beta_2()
    {
        // SemVer 2's own example of precedence, with beta.10 where it has beta.11.
        foreach (string version in new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.10", "1.0.0-rc.1" })
        {
            Package("Passenger.Sample", version);
        }
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0-beta.2\" />\n  </ItemGroup>\n</Project>\n");
        MainWindow window = Open(new FolderFeed(feed));
        NuGetView nuget = window.NuGet;
        Prerelease(nuget).IsChecked = true;
        Ui.Landed(() => nuget.Versions.Count == 9, "the installed package's versions, prereleases too");
        Assert.Equal(["1.1.0", "1.0.0", "1.0.0-rc.1", "1.0.0-beta.10", "1.0.0-beta.2", "1.0.0-beta", "1.0.0-alpha.beta", "1.0.0-alpha.1", "1.0.0-alpha"], nuget.Versions);

        ComboBox picker = Assert.Single(Pickers(nuget));
        picker.SelectedItem = "1.0.0-beta.10";
        Ui.Settle();
        Assert.True(Labels(nuget).Contains("Update"), $"Moving from beta.2 to beta.10 was offered as [{string.Join(", ", Labels(nuget))}]");
        picker.SelectedItem = "1.0.0-beta";
        Ui.Settle();
        Assert.True(Labels(nuget).Contains("Downgrade"), $"Moving from beta.2 to beta was offered as [{string.Join(", ", Labels(nuget))}]");
        window.Close();
    }

    [AvaloniaFact]
    public void Choosing_a_package_another_and_the_first_again_before_their_versions_arrive_shows_its_details_once()
    {
        HeldFeed held = new(new FolderFeed(feed));
        MainWindow window = Open(held);
        NuGetView nuget = window.NuGet;
        nuget.Query = "passenger";
        // The window waits on the versions held back, as background work, so nothing settles until they are let go.
        Ui.PumpUntil(() => nuget.Rows.Count == 2 && nuget.Selected is not null, "both packages listed, the first chosen");

        PackageRow first = nuget.Selected!;
        ListBox list = Descendants<ListBox>(nuget).Single();
        list.SelectedItem = nuget.Rows.Single(r => r.Id != first.Id);
        list.SelectedItem = first;
        held.Release();
        Ui.Landed(() => held.Answered == 3, "the versions of all three choices");

        List<ComboBox> pickers = [.. Pickers(nuget)];
        Assert.True(pickers.Count == 1, $"The details of {first.Id} were shown {pickers.Count} times");
        Assert.Equal(first.Id == "Passenger.Sample" ? ["1.1.0", "1.0.0"] : ["2.0.0"], pickers[0].Items.OfType<string>());
        window.Close();
    }

    [AvaloniaFact]
    public void Versions_unlisted_on_nuget_org_are_not_offered()
    {
        MainWindow window = Open(new V3Feed(new FeedClient("nuget.org", V3Feed.NuGetOrg, new HttpClient(new RecordedNuGetOrg()))));
        NuGetView nuget = window.NuGet;
        nuget.Query = "moq";
        Ui.Landed(() => nuget.Versions.Count > 0, "Moq's versions");
        Assert.Equal(["4.21.0", "4.20.72", "4.20.70", "4.20.69", "4.18.4", "4.7.0", "4.5.30"], nuget.Versions);
        Assert.Equal("4.21.0", nuget.ChosenVersion);

        Prerelease(nuget).IsChecked = true;
        Ui.Landed(() => nuget.Versions.Contains("4.6.62-alpha"), "Moq's listed prerelease");
        Assert.Equal(["4.21.0", "4.20.72", "4.20.70", "4.20.69", "4.18.4", "4.7.0", "4.6.62-alpha", "4.5.30"], nuget.Versions);
        window.Close();
    }

    [AvaloniaFact]
    public void Under_central_package_management_the_window_shows_the_version_set_centrally_and_an_install_puts_its_version_there()
    {
        string props = Path.Combine(folder, "Directory.Packages.props");
        File.WriteAllText(props, """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Passenger.Sample" Version="1.1.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" />\n  </ItemGroup>\n</Project>\n");
        MainWindow window = Open(new FolderFeed(feed));
        NuGetView nuget = window.NuGet;
        nuget.Query = "sample";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.IsInstalled == true, "Passenger.Sample listed as installed");
        Assert.Equal("1.1.0", ShownVersion(nuget, "Lib"));

        nuget.Query = "other";
        searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == "Passenger.Other" && nuget.Versions.Count == 1, "Passenger.Other and its version");
        Button install = InstallButton(nuget, "Lib") ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on Lib's line");
        Ui.Click(install, settle: false);
        Ui.Landed(() => !nuget.IsBusy && ShownVersion(nuget, "Lib") == "2.0.0", "Passenger.Other installed in Lib at 2.0.0");
        System.Xml.Linq.XElement? central = System.Xml.Linq.XDocument.Load(props).Descendants("PackageVersion").SingleOrDefault(e => (string?)e.Attribute("Include") == "Passenger.Other");
        Assert.True((string?)central?.Attribute("Version") == "2.0.0", $"The install did not put Passenger.Other's version in Directory.Packages.props:\n{File.ReadAllText(props)}");
        System.Xml.Linq.XElement reference = Assert.Single(System.Xml.Linq.XDocument.Load(project).Descendants("PackageReference"), e => (string?)e.Attribute("Include") == "Passenger.Other");
        Assert.True(reference.Attribute("Version") is null, $"The install wrote a version into the project, which central package management refuses:\n{File.ReadAllText(project)}");
        window.Close();
    }

    /// <summary>The version a project's line shows for the chosen package: the one installed there, or nothing.</summary>
    private static string? ShownVersion(NuGetView nuget, string name) => nuget.GetLogicalDescendants().OfType<StackPanel>()
        .Where(line => line.Children.FirstOrDefault() is TextBlock { Text: { } text } && text == name)
        .Select(line => line.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text)
        .FirstOrDefault();

    /// <summary>A window on a folder (the Lib project's unless given) with the NuGet window open on a feed; without the C#
    /// language server, which nothing here asks of, so none starts and restores the project.</summary>
    private MainWindow Open(IPackageFeed packages, string? opened = null)
    {
        MainWindow window = Ui.OpenFolder(opened ?? Path.Combine(folder, "Lib"), languageServer: false);
        window.NuGet.Feed = packages;
        window.Perform(AppActions.ShowNuGet);
        // Shown before a query is typed, so the window's first list is its own and not a second search for that query.
        Ui.Settle();
        return window;
    }

    private static IEnumerable<T> Descendants<T>(NuGetView nuget) => nuget.GetVisualDescendants().OfType<T>();

    /// <summary>The chosen package's version pickers, among its details: the feed chooser beside the search is not one.</summary>
    private static IEnumerable<ComboBox> Pickers(NuGetView nuget) => Descendants<ComboBox>(nuget).Where(c => c.Name == "Versions");

    private static CheckBox Prerelease(NuGetView nuget) => Descendants<CheckBox>(nuget).Single(c => c.Content as string == "Prerelease");

    /// <summary>The words on the window's buttons, the projects' Install, Update, Downgrade and Remove among them.</summary>
    private static List<string> Labels(NuGetView nuget) => [.. Descendants<Button>(nuget).Select(b => b.Content).OfType<string>()];

    private static void Run(NuGetView nuget, Task<bool> change)
    {
        Ui.Landed(() => change.IsCompleted, "the dotnet command's end");
        Assert.True(change.Result, nuget.Log);
    }

    private static string? Version(MainWindow window) =>
        Assert.Single(window.Projects).Packages.Where(p => p.Name == "Passenger.Sample").Select(p => p.Version).FirstOrDefault();

    /// <summary>A package with nothing in it but its manifest and an empty lib folder, so any framework takes it.</summary>
    private void Package(string id, string version)
    {
        using ZipArchive zip = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        using (StreamWriter spec = new(zip.CreateEntry($"{id}.nuspec").Open()))
        {
            spec.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{version}</version>
                    <authors>Passenger Tests</authors>
                    <description>A package for the tests.</description>
                  </metadata>
                </package>
                """);
        }
        zip.CreateEntry("lib/netstandard2.0/_._");
    }

    /// <summary>A feed slow to say a package's versions: every question is held until released, then answered however
    /// long ago it was asked or given up.</summary>
    private sealed class HeldFeed(IPackageFeed inner) : IPackageFeed
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int answered;

        public string Name => inner.Name;

        public int Answered => Volatile.Read(ref answered);

        public void Release() => released.TrySetResult();

        public Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel) => inner.SearchAsync(query, prerelease, cancel);

        public async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel)
        {
            await released.Task;
            IReadOnlyList<string> versions = await inner.VersionsAsync(id, prerelease, CancellationToken.None);
            Interlocked.Increment(ref answered);
            return versions;
        }
    }

    /// <summary>nuget.org's answers about Moq as recorded on October 12, 2026, trimmed to a few versions, among them
    /// those Moq's owner unlisted: 4.20.0-rc, 4.20.0 to 4.20.2, 4.20.71 and 4.6.0. The registrations come gzipped, as
    /// nuget.org sends them, and the first page by reference, as nuget.org gives a package of more versions.</summary>
    private sealed class RecordedNuGetOrg : HttpMessageHandler
    {
        private const string Registrations = "https://api.nuget.org/v3/registration5-gz-semver2/";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.GetLeftPart(UriPartial.Path) switch
            {
                "https://api.nuget.org/v3/index.json" => Answer(ServiceIndex, gzipped: false),
                "https://azuresearch-usnc.nuget.org/query" => Answer(Search, gzipped: false),
                Registrations + "moq/index.json" => Answer(Registration, gzipped: true),
                Registrations + "moq/page/4.5.30/4.7.0.json" => Answer(FirstPage, gzipped: true),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        private static HttpResponseMessage Answer(string json, bool gzipped)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (gzipped)
            {
                using MemoryStream packed = new();
                using (GZipStream zip = new(packed, CompressionLevel.Fastest))
                {
                    zip.Write(body);
                }
                body = packed.ToArray();
            }
            ByteArrayContent content = new(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (gzipped)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private const string ServiceIndex = """
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "https://azuresearch-usnc.nuget.org/query", "@type": "SearchQueryService" },
                { "@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl" },
                { "@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0" },
                { "@id": "https://azuresearch-usnc.nuget.org/query", "@type": "SearchQueryService/3.5.0" },
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver1/", "@type": "RegistrationsBaseUrl/3.4.0" },
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" }
              ]
            }
            """;

        private const string Search = """
            {
              "totalHits": 456,
              "data": [
                { "id": "Moq", "version": "4.21.0", "description": "Moq is the most popular and friendly mocking framework for .NET.", "authors": ["Daniel Cazzulino, kzu"], "totalDownloads": 1249123849 }
              ]
            }
            """;

        private const string Registration = """
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/index.json",
              "count": 2,
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/page/4.5.30/4.7.0.json",
                  "@type": "catalog:CatalogPage",
                  "count": 4,
                  "lower": "4.5.30",
                  "upper": "4.7.0"
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/index.json#page/4.18.4/4.21.0",
                  "@type": "catalog:CatalogPage",
                  "count": 10,
                  "items": [
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.18.4.json", "catalogEntry": {"id": "Moq", "version": "4.18.4", "listed": true, "published": "2022-12-30T11:22:01.4+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.0-rc.json", "catalogEntry": {"id": "Moq", "version": "4.20.0-rc", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.0.json", "catalogEntry": {"id": "Moq", "version": "4.20.0", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.1.json", "catalogEntry": {"id": "Moq", "version": "4.20.1", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.2.json", "catalogEntry": {"id": "Moq", "version": "4.20.2", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.69.json", "catalogEntry": {"id": "Moq", "version": "4.20.69", "listed": true, "published": "2023-08-11T19:50:32.77+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.70.json", "catalogEntry": {"id": "Moq", "version": "4.20.70", "listed": true, "published": "2023-11-28T15:31:50.26+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.71.json", "catalogEntry": {"id": "Moq", "version": "4.20.71", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.20.72.json", "catalogEntry": {"id": "Moq", "version": "4.20.72", "listed": true, "published": "2024-09-07T01:37:52.233+00:00"}},
                    {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.21.0.json", "catalogEntry": {"id": "Moq", "version": "4.21.0", "listed": true, "published": "2026-09-24T15:51:05.193+00:00"}}
                  ],
                  "lower": "4.18.4",
                  "parent": "https://api.nuget.org/v3/registration5-gz-semver2/moq/index.json",
                  "upper": "4.21.0"
                }
              ]
            }
            """;

        private const string FirstPage = """
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/page/4.5.30/4.7.0.json",
              "@type": "catalog:CatalogPage",
              "count": 4,
              "items": [
                {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.5.30.json", "catalogEntry": {"id": "Moq", "version": "4.5.30", "listed": true, "published": "2017-01-09T04:04:32.56+00:00"}},
                {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.6.0.json", "catalogEntry": {"id": "Moq", "version": "4.6.0", "listed": false, "published": "1900-01-01T00:00:00+00:00"}},
                {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.6.62-alpha.json", "catalogEntry": {"id": "Moq", "version": "4.6.62-alpha", "listed": true, "published": "2017-02-21T20:43:34.95+00:00"}},
                {"@id": "https://api.nuget.org/v3/registration5-gz-semver2/moq/4.7.0.json", "catalogEntry": {"id": "Moq", "version": "4.7.0", "listed": true, "published": "2017-02-22T03:05:12.363+00:00"}}
              ],
              "lower": "4.5.30",
              "parent": "https://api.nuget.org/v3/registration5-gz-semver2/moq/index.json",
              "upper": "4.7.0"
            }
            """;
    }
}
