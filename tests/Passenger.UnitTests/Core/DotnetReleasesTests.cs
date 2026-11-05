using System.Text.Json;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The .NET SDK Install .NET SDK chooses from Microsoft's release metadata, as builds.dotnet.microsoft.com writes
/// it: the newest supported channel's newest SDK with nothing asked, a global.json's version as its rollForward lets it go,
/// a project's channel, each with this system's archive and its SHA-512.</summary>
public class DotnetReleasesTests
{
    private const string Index = """
        { "releases-index": [
          { "channel-version": "11.0", "support-phase": "go-live", "latest-sdk": "11.0.100-rc.3.26522.148",
            "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json" },
          { "channel-version": "10.0", "support-phase": "active", "latest-sdk": "10.0.403",
            "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json" },
          { "channel-version": "9.0", "support-phase": "maintenance", "latest-sdk": "9.0.320",
            "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json" },
          { "channel-version": "7.0", "support-phase": "eol",
            "releases.json": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/7.0/releases.json" }
        ] }
        """;

    private static string Sdk(string version, string runtime) => $$"""
        { "version": "{{version}}", "runtime-version": "{{runtime}}", "files": [
          { "name": "dotnet-sdk-osx-arm64.pkg", "rid": "osx-arm64", "url": "https://builds.dotnet.microsoft.com/dotnet/Sdk/{{version}}/dotnet-sdk-{{version}}-osx-arm64.pkg", "hash": "AA" },
          { "name": "dotnet-sdk-osx-arm64.tar.gz", "rid": "osx-arm64", "url": "https://builds.dotnet.microsoft.com/dotnet/Sdk/{{version}}/dotnet-sdk-{{version}}-osx-arm64.tar.gz", "hash": "B{{version}}" },
          { "name": "dotnet-sdk-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/Sdk/{{version}}/dotnet-sdk-{{version}}-win-x64.exe", "hash": "CC" },
          { "name": "dotnet-sdk-win-x64.zip", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/Sdk/{{version}}/dotnet-sdk-{{version}}-win-x64.zip", "hash": "D{{version}}" }
        ] }
        """;

    /// <summary>10.0's releases, newest first, the newest naming two feature bands' SDKs.</summary>
    private static readonly string Ten = $$"""
        { "channel-version": "10.0", "releases": [
          { "release-version": "10.0.14", "sdk": {{Sdk("10.0.403", "10.0.14")}}, "sdks": [ {{Sdk("10.0.403", "10.0.14")}}, {{Sdk("10.0.114", "10.0.14")}} ] },
          { "release-version": "10.0.13", "sdk": {{Sdk("10.0.402", "10.0.13")}}, "sdks": [ {{Sdk("10.0.402", "10.0.13")}}, {{Sdk("10.0.113", "10.0.13")}} ] },
          { "release-version": "10.0.0-rc.2", "sdk": {{Sdk("10.0.100-rc.2.25502.107", "10.0.0-rc.2")}} }
        ] }
        """;

    /// <summary>11.0's releases, in preview: prereleases alone.</summary>
    private static readonly string Eleven = $$"""
        { "channel-version": "11.0", "releases": [
          { "release-version": "11.0.0-rc.3.26522.148", "sdk": {{Sdk("11.0.100-rc.3.26522.148", "11.0.0-rc.3.26522.148")}} },
          { "release-version": "11.0.0-rc.2.26504.105", "sdk": {{Sdk("11.0.100-rc.2.26504.105", "11.0.0-rc.2.26504.105")}} }
        ] }
        """;

    private static IReadOnlyList<DotnetChannel> Channels()
    {
        using JsonDocument index = JsonDocument.Parse(Index);
        return DotnetReleases.Channels(index.RootElement);
    }

    private static SdkDownload? Choose(SdkWanted wanted, string system = "osx-arm64")
    {
        using JsonDocument releases = JsonDocument.Parse(Ten);
        return DotnetReleases.Choose(wanted, releases.RootElement, system);
    }

    [Fact]
    public void With_nothing_asked_the_newest_supported_channel_is_read_never_a_preview_or_one_at_its_end()
    {
        Assert.Equal(["10.0"], DotnetReleases.ChannelsFor(SdkWanted.Newest, Channels()).Select(c => c.Version));
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", Channels()[1].Releases.AbsoluteUri);
    }

    [Fact]
    public void With_nothing_asked_the_channels_newest_release_SDK_is_chosen_with_this_systems_archive_and_its_hash()
    {
        SdkDownload chosen = Assert.IsType<SdkDownload>(Choose(SdkWanted.Newest));
        Assert.Equal(("10.0.403", "10.0.14"), (chosen.Version, chosen.Runtime));
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-osx-arm64.tar.gz", chosen.Url.AbsoluteUri);
        Assert.Equal("b10.0.403", chosen.Sha512);
        Assert.Equal("dotnet-sdk-10.0.403-osx-arm64.tar.gz", chosen.FileName);
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip", Choose(SdkWanted.Newest, "win-x64")!.Url.AbsoluteUri);
        Assert.Null(Choose(SdkWanted.Newest, "linux-riscv64"));
    }

    [Fact]
    public void A_global_json_version_takes_the_newest_patch_of_its_feature_band_by_default_and_itself_alone_with_rollForward_disable()
    {
        Assert.Equal("10.0.114", Choose(new SdkWanted("10.0.100"))!.Version);
        Assert.Equal("10.0.113", Choose(new SdkWanted("10.0.113", "disable"))!.Version);
        Assert.Null(Choose(new SdkWanted("10.0.110", "disable")));
        Assert.Equal("10.0.403", Choose(new SdkWanted("10.0.100", "latestFeature"))!.Version);
        Assert.Null(Choose(new SdkWanted("10.0.500")));
    }

    [Fact]
    public void A_prerelease_is_chosen_only_where_global_json_asks_for_one()
    {
        Assert.Equal("10.0.100-rc.2.25502.107", Choose(new SdkWanted("10.0.100-rc.2.25502.107", "disable"))!.Version);
        Assert.False(DotnetReleases.Satisfies(new SdkWanted("10.0.100"), "10.0.100-rc.2.25502.107"));
        Assert.True(DotnetReleases.Satisfies(new SdkWanted("10.0.100-rc.1.25451.107"), "10.0.100-rc.2.25502.107"));
    }

    [Fact]
    public void A_version_reads_its_own_channel_and_a_rollForward_past_its_minor_the_newer_supported_ones_newest_first()
    {
        Assert.Equal(["10.0"], DotnetReleases.ChannelsFor(new SdkWanted("10.0.100"), Channels()).Select(c => c.Version));
        Assert.Equal(["9.0", "10.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "major", AllowPrerelease: false), Channels()).Select(c => c.Version));
        Assert.Equal(["9.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "latestMinor"), Channels()).Select(c => c.Version));
    }

    [Fact]
    public void A_latestMajor_rollForward_reads_the_newest_channel_first_its_own_in_its_place()
    {
        Assert.Equal(["10.0", "9.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "latestMajor", AllowPrerelease: false), Channels()).Select(c => c.Version));
        Assert.Equal(["10.0", "9.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "LatestMajor", AllowPrerelease: false), Channels()).Select(c => c.Version));
    }

    [Fact]
    public void A_global_json_allowing_prereleases_takes_the_preview_its_rollForward_reaches_where_dotnet_itself_would()
    {
        SdkWanted wanted = new("10.0.500", "latestMajor", AllowPrerelease: true);
        Assert.True(DotnetReleases.Satisfies(wanted, "11.0.100-rc.3.26522.148"), "A preview global.json allows is refused, though dotnet takes it");
        Assert.Equal(["11.0", "10.0"], DotnetReleases.ChannelsFor(wanted, Channels()).Select(c => c.Version));
        using JsonDocument releases = JsonDocument.Parse(Eleven);
        Assert.Equal("11.0.100-rc.3.26522.148", DotnetReleases.Choose(wanted, releases.RootElement, "osx-arm64")?.Version);
    }

    [Fact]
    public void A_global_json_allowing_prereleases_still_rolls_only_as_far_as_its_rollForward_lets_it()
    {
        Assert.False(DotnetReleases.Satisfies(new SdkWanted("10.0.500", "latestMinor", AllowPrerelease: true), "11.0.100-rc.3.26522.148"));
        Assert.False(DotnetReleases.Satisfies(new SdkWanted("11.0.100", "latestMajor", AllowPrerelease: true), "11.0.100-rc.3.26522.148"));
        Assert.Equal(["10.0"], DotnetReleases.ChannelsFor(new SdkWanted("10.0.500", "latestFeature", AllowPrerelease: true), Channels()).Select(c => c.Version));
        Assert.Equal(["10.0"], DotnetReleases.ChannelsFor(new SdkWanted("10.0.500", "latestMinor", AllowPrerelease: true), Channels()).Select(c => c.Version));
    }

    [Fact]
    public void A_channel_at_its_end_is_read_for_no_prerelease()
    {
        IReadOnlyList<DotnetChannel> ended = [new("9.0", "active", new Uri("https://example.test/9.0.json")), new("10.0", "eol", new Uri("https://example.test/10.0.json"))];
        Assert.Equal(["9.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "latestMajor", AllowPrerelease: true), ended).Select(c => c.Version));
    }

    [Fact]
    public void A_global_json_saying_nothing_of_prereleases_takes_one_as_dotnet_does_but_reads_every_release_first()
    {
        SdkWanted wanted = new("10.0.500", "latestMajor");
        Assert.True(DotnetReleases.Satisfies(wanted, "11.0.100-rc.3.26522.148"), "A preview dotnet takes where global.json says nothing is refused");
        Assert.Equal(["10.0", "11.0"], DotnetReleases.ChannelsFor(wanted, Channels()).Select(c => c.Version));
        Assert.Equal(["10.0", "9.0", "11.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "latestMajor"), Channels()).Select(c => c.Version));
        Assert.Equal(["9.0", "10.0", "11.0"], DotnetReleases.ChannelsFor(new SdkWanted("9.0.100", "major"), Channels()).Select(c => c.Version));
    }

    [Fact]
    public void A_global_json_refusing_prereleases_reads_no_preview_unless_the_version_it_asks_for_is_one()
    {
        SdkWanted wanted = new("10.0.500", "latestMajor", AllowPrerelease: false);
        Assert.False(DotnetReleases.Satisfies(wanted, "11.0.100-rc.3.26522.148"));
        Assert.Equal(["10.0"], DotnetReleases.ChannelsFor(wanted, Channels()).Select(c => c.Version));
        Assert.True(DotnetReleases.Satisfies(new SdkWanted("11.0.100-rc.2.26504.105", "latestPatch", AllowPrerelease: false), "11.0.100-rc.3.26522.148"),
            "dotnet passes over allowPrerelease false for a version that is a prerelease, as this does not");
    }

    [Fact]
    public void A_projects_channel_is_read_even_in_preview_and_its_newest_SDK_taken()
    {
        Assert.Equal(["11.0"], DotnetReleases.ChannelsFor(new SdkWanted(Channel: "11.0"), Channels()).Select(c => c.Version));
        Assert.Equal("10.0.403", Choose(new SdkWanted(Channel: "10.0"))!.Version);
        Assert.Empty(DotnetReleases.ChannelsFor(new SdkWanted(Channel: "12.0"), Channels()));
    }

    [Fact]
    public void A_projects_channel_global_json_refuses_prereleases_of_takes_a_release_alone_and_says_why_where_it_has_none()
    {
        SdkWanted releases = new(Channel: "10.0", AllowPrerelease: false);
        Assert.False(DotnetReleases.Satisfies(releases, "10.0.100-rc.2.25502.107"), "An RC global.json then refuses was taken for the project's channel");
        Assert.Equal("10.0.403", Choose(releases)!.Version);
        SdkWanted previewOnly = new(Channel: "11.0", AllowPrerelease: false);
        using JsonDocument eleven = JsonDocument.Parse(Eleven);
        Assert.Null(DotnetReleases.Choose(previewOnly, eleven.RootElement, "osx-arm64"));
        Assert.Equal("Microsoft publishes no .NET SDK .NET 11.0's newest for osx-arm64 but prereleases, which global.json's allowPrerelease refuses",
            DotnetReleases.NonePublished(previewOnly, "osx-arm64"));
        Assert.Equal("Microsoft publishes no .NET SDK 10.0.999 for osx-arm64 (global.json's rollForward disable)",
            DotnetReleases.NonePublished(new SdkWanted("10.0.999", "disable"), "osx-arm64"));
    }

    [Fact]
    public void This_system_is_named_as_Microsoft_names_its_archives()
    {
        Assert.Matches(@"^(osx|win|linux|linux-musl|freebsd)-(arm64|x64|x86|arm|s390x|ppc64le|riscv64|loongarch64)$", DotnetReleases.ThisSystem());
    }
}
