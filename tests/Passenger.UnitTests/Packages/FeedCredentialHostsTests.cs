using Passenger.Core;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A feed's credentials go only to the host they were stored for, matched by the feed's URL, never by its name: a
/// nuget.config nearer the folder pointing a feed of the person's at another server takes none of them, whether they are
/// kept in the person's own file or NuGet's environment variable; one pointing it at the same host, or a file giving the feed
/// its URL and credentials both, takes them.</summary>
public sealed class FeedCredentialHostsTests : IDisposable
{
    private const string Company = "https://pkgs.company.example/feed/v3/index.json";
    private const string Elsewhere = "https://collector.example/v3/index.json";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A file of NuGet's order, its feeds by name and URL and the feeds it holds credentials for.</summary>
    private static ConfigFile File(string path, ConfigPlace place, (string Name, string Url)[] feeds, params string[] credentialed) =>
        new(path, place, [.. feeds.Select(f => new ConfigFeed(f.Name, f.Url, false, null, false, false))], [], false, null, null, null)
        {
            Credentialed = credentialed,
        };

    [Fact]
    public void A_feeds_credentials_go_only_to_the_host_they_were_stored_for() => Cases.All(
        ("a repository's file pointing the person's feed at another host takes none of its credentials", () =>
        {
            ConfigFile repository = File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Elsewhere)]);
            ConfigFile user = File("/home/ada/.nuget/NuGet/NuGet.Config", ConfigPlace.User, [("Company", Company)], "Company");
            string? why = FeedCredentialHosts.Withholds("Company", Elsewhere, [repository, user], fromEnvironment: false);
            Assert.True(why == "Company's credentials in /home/ada/.nuget/NuGet/NuGet.Config are for https://pkgs.company.example, not "
                + "https://collector.example, where a nuget.config nearer the folder points Company",
                $"The person's credentials for Company went to the host a repository's nuget.config points it at: {why ?? "sent"}");
            Assert.Null(FeedCredentialHosts.Withholds("Company", "https://pkgs.company.example/other/v3/index.json", [repository, user], fromEnvironment: false));
        }),
        ("credentials kept beside the feed's url go to it and those naming no url go nowhere", () =>
        {
            ConfigFile both = File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Company)], "Company");
            Assert.Null(FeedCredentialHosts.Withholds("Company", Company, [both], fromEnvironment: false));

            // The machine's file gives the URL the user's credentials are for, as it comes after the user's in NuGet's order.
            ConfigFile user = File("/home/ada/.nuget/NuGet/NuGet.Config", ConfigPlace.User, [], "Company");
            ConfigFile machine = File("/etc/nuget/NuGet.Config", ConfigPlace.Machine, [("Company", Company)]);
            Assert.Null(FeedCredentialHosts.Withholds("Company", Company, [user, machine], fromEnvironment: false));

            ConfigFile repository = File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Company)]);
            string? why = FeedCredentialHosts.Withholds("Company", Company, [repository, user], fromEnvironment: false);
            Assert.True(why == "Company's credentials in /home/ada/.nuget/NuGet/NuGet.Config are for no URL: neither that file nor one after it gives Company one, so they go to none",
                $"Credentials naming no URL went to whatever a nearer nuget.config points their feed at: {why ?? "sent"}");
        }),
        ("credentials from the environment go only to the url the person's own files give the feed", () =>
        {
            ConfigFile repository = File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Elsewhere)]);
            ConfigFile user = File("/home/ada/.nuget/NuGet/NuGet.Config", ConfigPlace.User, [("Company", Company)]);
            string? why = FeedCredentialHosts.Withholds("Company", Elsewhere, [repository, user], fromEnvironment: true);
            Assert.True(why?.StartsWith($"Company's credentials in the {FeedCredentialHosts.Variable}Company variable are for https://pkgs.company.example, not https://collector.example", StringComparison.Ordinal) == true,
                $"The environment's credentials for Company went to the host a repository's nuget.config points it at: {why ?? "sent"}");
            Assert.Null(FeedCredentialHosts.Withholds("Company", Company, [File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Company)]), user], fromEnvironment: true));
            Assert.NotNull(FeedCredentialHosts.Withholds("Company", Company, [File("/work/shop/nuget.config", ConfigPlace.Solution, [("Company", Company)])], fromEnvironment: true));
        }),
        ("a folder's feeds read through NuGet keep credentials only where their url is the one they were kept for", () =>
        {
            string folder = Path.Combine(root, "shop");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="passenger-unit-company" value="{Company}" />
                    <add key="passenger-unit-kept" value="{Company}" />
                  </packageSources>
                  <packageSourceCredentials>
                    <passenger-unit-company>
                      <add key="Username" value="ada" />
                      <add key="ClearTextPassword" value="kept-for-the-company" />
                    </passenger-unit-company>
                    <passenger-unit-kept>
                      <add key="Username" value="ada" />
                      <add key="ClearTextPassword" value="kept-for-the-company" />
                    </passenger-unit-kept>
                  </packageSourceCredentials>
                </configuration>
                """);
            System.IO.File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="passenger-unit-company" value="{Elsewhere}" />
                  </packageSources>
                </configuration>
                """);
            PackageSourceSet sources = PackageSourceSet.Load(folder);
            FeedSource pointed = Assert.Single(sources.Feeds, f => f.Name == "passenger-unit-company");
            Assert.True(pointed.Source == Elsewhere && pointed.Credentials is null && pointed.Withheld?.Contains("are for https://pkgs.company.example, not https://collector.example", StringComparison.Ordinal) == true,
                $"The feed a nearer nuget.config points at another host was given {pointed.Credentials?.ToString() ?? "no credentials"}, saying {pointed.Withheld ?? "nothing"}");
            FeedSource kept = Assert.Single(sources.Feeds, f => f.Name == "passenger-unit-kept");
            Assert.True(kept.Credentials is { Username: "ada", Password: "kept-for-the-company" } && kept.Withheld is null,
                $"The feed left where its credentials were kept for lost them: {kept.Withheld}");
        }));

}
