using System.Text.Json;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A package version's deprecation as a feed's registration gives it, read as the NuGet window marks it: why, the
/// owner's words, and the package to use instead with its versions in words.</summary>
public class DeprecationTests
{
    private static JsonElement Json(string text) => JsonElement.Parse(text);

    /// <summary>A page's deprecated versions are those asked, each keyed as asked though the page writes it otherwise; a
    /// version with no deprecation, or not asked, is left out.</summary>
    [Fact]
    public void A_registration_pages_deprecated_versions_are_those_asked_however_the_page_writes_them()
    {
        JsonElement page = Json("""
            { "lower": "1.0.0", "upper": "1.2.0", "items": [
                { "catalogEntry": { "version": "1.0.0", "deprecation": { "reasons": ["Legacy", "CriticalBugs"], "message": " Kept no more. ",
                    "alternatePackage": { "id": "Passenger.Modern", "range": "[2.0.0, )" } } } },
                { "catalogEntry": { "version": "1.1.0", "listed": true } },
                { "catalogEntry": { "version": "1.2.0+build.7", "deprecation": { "reasons": ["Other"] } } },
                { "catalogEntry": { "version": "1.3.0", "deprecation": { "reasons": ["Legacy"] } } } ] }
            """);
        Dictionary<string, Deprecation> read = Deprecation.OnPage(page, ["1.0", "1.1.0", "1.2.0"]).ToDictionary(d => d.Version, d => d.Deprecation);
        Assert.Equal(["1.0", "1.2.0"], read.Keys.Order());
        Deprecation legacy = read["1.0"];
        Assert.True(legacy.Why == "legacy, no longer maintained, critical bugs" && legacy.Message == "Kept no more." && legacy.Instead == "Passenger.Modern 2.0.0 or later",
            $"1.0.0's deprecation reads as \"{legacy.Why}\", \"{legacy.Message}\", instead {legacy.Instead}");
        Assert.True(read["1.2.0"] is { Why: "other", Instead: null, Message: "" }, $"1.2.0's deprecation reads as {read["1.2.0"]}");
    }

    /// <summary>The package to use instead is said with its versions in words: any version alone, a least version as that or
    /// later, a pin as that version, any other range as written.</summary>
    [Theory]
    [InlineData("*", "Passenger.Modern")]
    [InlineData("", "Passenger.Modern")]
    [InlineData("[2.0.0, )", "Passenger.Modern 2.0.0 or later")]
    [InlineData("2.0.0", "Passenger.Modern 2.0.0 or later")]
    [InlineData("[2.1.0]", "Passenger.Modern 2.1.0")]
    [InlineData("[2.0.0, 3.0.0)", "Passenger.Modern [2.0.0, 3.0.0)")]
    [InlineData("2.*", "Passenger.Modern 2.*")]
    public void The_package_to_use_instead_is_said_with_its_versions_in_words(string range, string said) =>
        Assert.Equal(said, new Deprecation(["Legacy"], string.Empty, "Passenger.Modern", range).Instead);

    /// <summary>What a feed writes otherwise than NuGet's shape gives what can be read of it, never a failure: a deprecation
    /// that is no object is none, reasons that are no list give none, a reason NuGet does not name is said as written, and an
    /// alternative with no id is none.</summary>
    [Fact]
    public void A_deprecation_written_otherwise_gives_what_can_be_read_of_it()
    {
        Assert.Null(Deprecation.Read(Json("\"yes\"")));
        Deprecation odd = Deprecation.Read(Json("""{ "reasons": "Legacy", "alternatePackage": { "range": "*" } }"""))!;
        Assert.True(odd is { Why: "no reason given", Alternative: null, Instead: null, AlternativeVersions: null }, $"An odd deprecation reads as {odd}, why {odd.Why}");
        Assert.Equal("Security, legacy, no longer maintained", Deprecation.Read(Json("""{ "reasons": ["Security", "legacy", "Legacy"] }"""))!.Why);
    }

    /// <summary>A page the index gives by reference is fetched only where it may hold a version asked: one whose lowest and
    /// highest versions are around it, or that names neither.</summary>
    [Fact]
    public void A_page_by_reference_may_hold_only_the_versions_between_its_lowest_and_highest()
    {
        JsonElement page = Json("""{ "@id": "https://feed/registrations/a/page/1.0.0/2.0.0.json", "lower": "1.0.0", "upper": "2.0.0" }""");
        Assert.True(Deprecation.MayHold(page, ["3.0.0", "2.0.0"]), "A page whose highest version is the one asked was passed over");
        Assert.True(Deprecation.MayHold(page, ["1.5.0-beta"]), "A page around a prerelease asked was passed over");
        Assert.False(Deprecation.MayHold(page, ["0.9.0", "2.0.1"]), "A page holding no version asked would be fetched");
        Assert.True(Deprecation.MayHold(Json("""{ "@id": "https://feed/p.json" }"""), ["9.0.0"]), "A page naming no bounds was passed over");
    }
}
