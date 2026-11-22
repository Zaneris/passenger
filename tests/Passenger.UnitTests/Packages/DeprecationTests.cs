using System.Text.Json;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A package version's deprecation as a feed's registration gives it, read as the NuGet window marks it: why, the
/// owner's words, and the package to use instead with its versions in words.</summary>
public class DeprecationTests
{
    private static JsonElement Json(string text) => JsonElement.Parse(text);

    [Fact]
    public void A_versions_deprecation_is_read_from_its_feeds_registration_as_the_NuGet_window_marks_it() => Cases.All(
        ("a registration page's deprecated versions are those asked however the page writes them", () =>
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
        }),
        ("the package to use instead is said with its versions in words", () => Cases.Each(
        [("*", "Passenger.Modern"), ("[2.0.0, )", "Passenger.Modern 2.0.0 or later"), ("[2.1.0]", "Passenger.Modern 2.1.0"), ("[2.0.0, 3.0.0)", "Passenger.Modern [2.0.0, 3.0.0)")],
        row => Assert.Equal(row.Item2, new Deprecation(["Legacy"], string.Empty, "Passenger.Modern", row.Item1).Instead))),
        ("a deprecation written otherwise gives what can be read of it", () =>
        {
            Assert.Null(Deprecation.Read(Json("\"yes\"")));
            Deprecation odd = Deprecation.Read(Json("""{ "reasons": "Legacy", "alternatePackage": { "range": "*" } }"""))!;
            Assert.True(odd is { Why: "no reason given", Alternative: null, Instead: null, AlternativeVersions: null }, $"An odd deprecation reads as {odd}, why {odd.Why}");
            Assert.Equal("Security, legacy, no longer maintained", Deprecation.Read(Json("""{ "reasons": ["Security", "legacy", "Legacy"] }"""))!.Why);
        }),
        ("a page by reference may hold only the versions between its lowest and highest", () =>
        {
            JsonElement page = Json("""{ "@id": "https://feed/registrations/a/page/1.0.0/2.0.0.json", "lower": "1.0.0", "upper": "2.0.0" }""");
            Assert.True(Deprecation.MayHold(page, ["3.0.0", "2.0.0"]), "A page whose highest version is the one asked was passed over");
            Assert.True(Deprecation.MayHold(page, ["1.5.0-beta"]), "A page around a prerelease asked was passed over");
            Assert.False(Deprecation.MayHold(page, ["0.9.0", "2.0.1"]), "A page holding no version asked would be fetched");
            Assert.True(Deprecation.MayHold(Json("""{ "@id": "https://feed/p.json" }"""), ["9.0.0"]), "A page naming no bounds was passed over");
        }));

}
