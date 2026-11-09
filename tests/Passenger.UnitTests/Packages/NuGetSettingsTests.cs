using System.Xml.Linq;
using NuGet.Configuration;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A nuget.config as Passenger reads and edits it, matching its names as NuGet does: one too deep for NuGet's reader
/// said not to read, before it ends the process, in the folder asked and those above it; and a feed's credentials found and
/// changed whatever letter case their element and keys are written in.</summary>
public sealed class NuGetSettingsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("")]
    [InlineData("src/App")]
    public void A_config_nested_past_what_is_read_is_said_not_to_read_and_ends_nothing(string below)
    {
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

    /// <summary>A feed whose credentials the file holds under its name and keys in other letter cases (an add element
    /// written Add too): the Sources tab reads the user and that a password is kept, and an edit giving another user and no
    /// password changes that one element, under the feed's name and NuGet's keys, keeping its password, so NuGet reads the
    /// file and sends the new user with the password kept.</summary>
    [Fact]
    public void Editing_a_feed_whose_credentials_are_written_in_another_letter_case_changes_that_element_and_adds_none()
    {
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
    }

    /// <summary>The element holding a feed's credentials, as each place reading or editing nuget.config finds it: the first
    /// whose name, decoded as XML names are, is the feed's in any letter case.</summary>
    [Theory]
    [InlineData("MyFeed", "<MyFeed />", 0)]
    [InlineData("MyFeed", "<myfeed /><MyFeed />", 0)]
    [InlineData("My Feed", "<Other /><my_x0020_feed />", 1)]
    [InlineData("MyFeed", "<MyFeeds />", -1)]
    public void A_feeds_credentials_are_the_first_element_its_name_encodes_to_in_any_letter_case(string feed, string elements, int found)
    {
        XElement section = XElement.Parse($"<packageSourceCredentials>{elements}</packageSourceCredentials>");
        XElement? held = NuGetConfigNames.CredentialsOf(section, feed).FirstOrDefault();
        Assert.True(held == section.Elements().ElementAtOrDefault(found),
            $"{feed}'s credentials in {elements} are {held?.Name.LocalName ?? "none"}, not the element at {found}");
    }
}
