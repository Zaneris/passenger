using System.Xml.Linq;
using NuGet.Configuration;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A nuget.config as Passenger reads and edits it, matching its names as NuGet does: one too deep for NuGet's reader
/// said not to read, before it ends the process, in the folder asked and those above it, its packages then looked for where
/// NuGet looks with no config; one in a parent another user owns, or above it, or one they put in the person's folder, left
/// out; and a feed's credentials found and changed whatever letter case their element and keys are written in.</summary>
public sealed class NuGetSettingsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private int made;

    /// <summary>A folder of its own for each check, below the test's.</summary>
    private string Fresh() => Directory.CreateDirectory(Path.Combine(root, $"check-{++made}")).FullName;

    private void NestedPastWhatIsRead(string below)
    {
        string root = Fresh();
        string nested = string.Concat(Enumerable.Repeat("<a>", 100_000)) + string.Concat(Enumerable.Repeat("</a>", 100_000));
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"<configuration><a>{nested}</a></configuration>");
        string folder = Path.Combine(root, below);
        Directory.CreateDirectory(folder);
        SmallStack.Run(() =>
        {
            Assert.Throws<NuGetConfigurationException>(() => NuGetSettings.Load(folder));
            Assert.Contains("nested too deeply", PackageSourceSet.Load(folder).Problem, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_nuget_config_is_read_and_edited_as_NuGet_matches_its_names_and_one_another_user_put_above_is_left_out() => Cases.All(
        ("a config in a parent another user owns or one they put beside the person's is left out and the person's own kept", () =>
        {
            string root = Fresh();
            string shared = Path.Combine(root, "shared");
            string own = Path.Combine(shared, "own");
            string project = Path.Combine(own, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(shared, "nuget.config"),
                "<configuration><packageSources><add key=\"planted\" value=\"https://planted.invalid/v3/index.json\" /></packageSources></configuration>");
            string owned = Path.Combine(own, "nuget.config");
            File.WriteAllText(owned,
                "<configuration><packageSources><add key=\"own\" value=\"https://own.invalid/v3/index.json\" /></packageSources></configuration>");
            File.WriteAllText(Path.Combine(root, "nuget.config"),
                "<configuration><packageSources><add key=\"above\" value=\"https://above.invalid/v3/index.json\" /></packageSources></configuration>");

            string[] read = Sources();
            Assert.Contains("own", read);
            Assert.Contains("planted", read);
            Assert.Contains("above", read);

            using (FoldersUp.HeldByAnother(shared))
            {
                read = Sources();
                Assert.Contains("own", read);
                Assert.False(read.Contains("planted") || read.Contains("above"),
                    $"A source of {shared}, which another user owns, or of a folder above it is among the project's: {string.Join(", ", read)}");
            }
            using (FoldersUp.HeldByAnother(owned))
            {
                read = Sources();
                Assert.False(read.Contains("own"), $"The source of {owned}, which another user put there, is among the project's: {string.Join(", ", read)}");
                Assert.Contains("planted", read);
            }

            string[] Sources() => [.. SettingsUtility.GetEnabledSources(NuGetSettings.Load(project)).Select(s => s.Name)];
        }),
        ("editing a feed whose credentials are written in another letter case changes that element and adds none", () =>
        {
            string root = Fresh();
            string config = Path.Combine(root, "nuget.config");
            File.WriteAllText(config, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <Add key="MyFeed" value="https://example.com/v3/index.json" />
                  </packageSources>
                  <packageSourceCredentials>
                    <myfeed>
                      <add key="username" value="alice" />
                      <add key="cleartextpassword" value="secret" />
                    </myfeed>
                  </packageSourceCredentials>
                </configuration>
                """);
            ConfigFeed read = Assert.Single(PackageSourceSet.Read(config, ConfigPlace.Solution).Feeds);
            Assert.True(read is { Name: "MyFeed", Username: "alice", HasPassword: true },
                $"The Sources tab reads {read}, where the file holds alice's credentials for MyFeed under <myfeed>");

            NuGetConfigEditor.Update(config, "MyFeed", new FeedEdit("MyFeed", "https://example.com/v3/index.json", Enabled: true, AllowInsecure: false, Username: "bob", Password: null));

            string written = File.ReadAllText(config);
            XElement[] held = [.. XDocument.Parse(written).Root!.Element("packageSourceCredentials")!.Elements()];
            Assert.True(held is [{ Name.LocalName: "MyFeed" } one]
                && one.Elements("add").Select(a => $"{a.Attribute("key")?.Value}={a.Attribute("value")?.Value}").SequenceEqual(["Username=bob", "ClearTextPassword=secret"]),
                $"Editing MyFeed's user left its credentials as:\n{written}");
            PackageSource source = Assert.Single(new PackageSourceProvider(Settings.LoadSettingsGivenConfigPaths([config])).LoadPackageSources(), s => s.Name == "MyFeed");
            Assert.True(source.Credentials is { Username: "bob", Password: "secret" },
                $"NuGet reads MyFeed's credentials from the edited file as {source.Credentials?.Username ?? "none"}, so a restore sends no password for bob:\n{written}");
        }),
        ("a config nested past what is read is said not to read and ends nothing", () => Cases.Each(["", "src/App"], NestedPastWhatIsRead, below => $"in the folder{(below.Length > 0 ? " above" : "")}")),
        ("a config that does not read in a parent another user owns is left out unread", () => Cases.Each([false, true], UnreadInAnothersParent, deep => deep ? "nested too deeply" : "malformed")),
        ("a folder whose config does not read looks for packages where NuGet does with none named", () => Cases.Each([false, true], PackagesWithNoneNamed, deep => deep ? "nested too deeply" : "malformed")),
        ("a feed's credentials are the first element its name encodes to in any letter case", () => Cases.Each(
            [("MyFeed", "<myfeed /><MyFeed />", 0), ("My Feed", "<Other /><my_x0020_feed />", 1), ("MyFeed", "<MyFeeds />", -1)],
            row => CredentialsElement(row.Item1, row.Item2, row.Item3))));

    /// <summary>A nuget.config that does not read, malformed or nested too deeply, in a parent another user owns is left out
    /// before anything reads it: the project's settings read as if it were not there, its own config's source among them,
    /// where the same config in the person's own parent is said not to read.</summary>
    private void UnreadInAnothersParent(bool deep)
    {
        string root = Fresh();
        string shared = Path.Combine(root, "shared");
        string own = Path.Combine(shared, "own");
        string project = Path.Combine(own, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(shared, "nuget.config"), deep
            ? $"<configuration>{string.Concat(Enumerable.Repeat("<a>", 100_000))}{string.Concat(Enumerable.Repeat("</a>", 100_000))}</configuration>"
            : "<configuration><packageSources><add key=\"planted\"");
        File.WriteAllText(Path.Combine(own, "nuget.config"),
            "<configuration><packageSources><add key=\"own\" value=\"https://own.invalid/v3/index.json\" /></packageSources></configuration>");
        SmallStack.Run(() =>
        {
            Assert.Throws<NuGetConfigurationException>(() => NuGetSettings.Load(project));
            using (FoldersUp.HeldByAnother(shared))
            {
                string[] read = [.. SettingsUtility.GetEnabledSources(NuGetSettings.Load(project)).Select(s => s.Name)];
                Assert.True(read.Contains("own"), $"The project's own source is not among those read past a config another user put above it: {string.Join(", ", read)}");
                Assert.Equal(Path.Combine(own, "nuget.config"), NuGetSettings.Load(project).GetConfigFilePaths()[0], PathNames.Comparer);
            }
        });
    }

    /// <summary>A folder whose nuget.config does not read, malformed or nested too deeply, names no package cache, so a
    /// project there that was never restored is looked for where NuGet looks with none named: NUGET_PACKAGES, else the
    /// user's .nuget/packages.</summary>
    private void PackagesWithNoneNamed(bool deep)
    {
        string root = Fresh();
        File.WriteAllText(Path.Combine(root, "nuget.config"), deep
            ? $"<configuration>{string.Concat(Enumerable.Repeat("<a>", 100_000))}{string.Concat(Enumerable.Repeat("</a>", 100_000))}</configuration>"
            : "<configuration><config><add key=\"globalPackagesFolder\"");
        string home = Path.Combine(root, "home");
        SmallStack.Run(() =>
        {
            Assert.Null(NuGetSettings.GlobalPackagesFolder(root));
            Assert.Equal("/caches/nuget", NuGetSettings.PackageCache(root, name => name == "NUGET_PACKAGES" ? "/caches/nuget" : null, home));
            string cache = NuGetSettings.PackageCache(root, _ => null, home);
            Assert.True(cache == Path.Combine(home, ".nuget", "packages"),
                $"A project not restored, under a nuget.config that does not read, would have none of its packages found for its XAML: looked in {cache}");
        });
    }

    /// <summary>The element holding a feed's credentials, as each place reading or editing nuget.config finds it: the first
    /// whose name, decoded as XML names are, is the feed's in any letter case.</summary>
    private static void CredentialsElement(string feed, string elements, int found)
    {
        XElement section = XElement.Parse($"<packageSourceCredentials>{elements}</packageSourceCredentials>");
        XElement? held = NuGetConfigNames.CredentialsOf(section, feed).FirstOrDefault();
        Assert.True(held == section.Elements().ElementAtOrDefault(found),
            $"{feed}'s credentials in {elements} are {held?.Name.LocalName ?? "none"}, not the element at {found}");
    }
}
