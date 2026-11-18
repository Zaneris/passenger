using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The newest of folders named by versions (a runtime's, a pack's, a tool's), as SemVer orders them: a release
/// above its release candidates and previews, which share its numbers, whatever order the folder lists them in.</summary>
public sealed class VersionOrderTests
{
    [Theory]
    [InlineData("36.1.0-preview.7", "36.1.0", "36.1.0")]
    [InlineData("36.1.0", "36.1.0-preview.7", "36.1.0")]
    [InlineData("11.0.0-rc.2.26504.105", "11.0.0", "11.0.0")]
    [InlineData("11.0.0", "11.0.0-rc.2.26504.105", "11.0.0")]
    [InlineData("11.0.0-rc.2.26504.105", "11.0.0-rc.10.26600.1", "11.0.0-rc.10.26600.1")]
    [InlineData("9.0.10", "9.0.9", "9.0.10")]
    [InlineData("10.0.0", "9.0.21", "10.0.0")]
    public void A_release_is_newer_than_its_release_candidate_whichever_the_folder_lists_first(string first, string second, string newest)
    {
        string packs = Path.Combine("dotnet", "packs", "Microsoft.Android.Sdk.Linux");

        string? chosen = VersionOrder.Newest([Path.Combine(packs, first), Path.Combine(packs, second)]);

        Assert.True(chosen == Path.Combine(packs, newest),
            $"Of {first} and {second}, listed in that order, {chosen} was taken as the newest, not {newest}: which build tools or runtime answers would hang on the listing's order");
    }

    [Fact]
    public void Folders_of_one_version_written_two_ways_are_told_apart_by_name_never_by_the_listing()
    {
        string[] listed = ["1.0+b", "1.0.0", "1.0+a"];

        Assert.Equal("1.0+a", VersionOrder.Newest(listed));
        Assert.Equal("1.0+a", VersionOrder.Newest(listed.Reverse()));
    }

    [Fact]
    public void A_folder_whose_name_is_no_version_is_passed_over_and_none_left_is_none()
    {
        Assert.Equal(Path.Combine("store", "8.0.1"), VersionOrder.Newest([Path.Combine("store", "temp"), Path.Combine("store", "8.0.1"), Path.Combine("store", ".hidden")]));
        Assert.Null(VersionOrder.Newest([Path.Combine("store", "temp")]));
        Assert.Null(VersionOrder.Newest([]));
    }

    [Theory]
    [InlineData("0.4.1", "0.4.0", true)]
    [InlineData("0.4.0", "0.4.2", true)]
    [InlineData("0.4", "0.4.3", true)]
    [InlineData("0.4.1-rc.1", "0.4.0", true)]
    [InlineData("0.4.0", "0.3.4", false)]
    [InlineData("1.4.0", "0.4.0", false)]
    public void Versions_share_a_minor_version_where_their_first_two_numbers_agree(string one, string other, bool same) =>
        Assert.True(VersionOrder.SameMinor(one, other) == same, same
            ? $"{one} and {other} were taken as of different minor versions, so a patch release's What's New would leave out the earlier notes of its own"
            : $"{one} and {other} were taken as of one minor version, so a patch release's What's New would show another minor version's notes");

    [Fact]
    public void Newest_first_lists_every_version_folder_from_the_newest()
    {
        Assert.Equal(["10.0.1", "10.0.0", "10.0.0-rc.2", "10.0.0-preview.7", "9.0.21"],
            VersionOrder.NewestFirst(["10.0.0-preview.7", "9.0.21", "10.0.0", "notes", "10.0.1", "10.0.0-rc.2"]));
    }
}
