using Passenger.App.Mobile;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>The edits an iOS app's head needs to build here, from its project's text, its Info.plist and what the iOS
/// workload answers at the framework it builds on CoreCLR: Avalonia's cross-platform template's head (net10.0-ios, iOS
/// 13.0, no scene manifest) needs all three, said in the banner's words and made in the project's and Info.plist's text,
/// every other character kept; one whose least iOS is set outside its own properties is left to the person.</summary>
public class IosHeadsTests
{
    private const string Project = "/src/XApp.iOS/XApp.iOS.csproj";

    private const string Manifest = "/src/XApp.iOS/Info.plist";

    /// <summary>Avalonia 12.1.6's avalonia.xplat iOS head, as the template writes it.</summary>
    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0-ios</TargetFramework>
            <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
            <Nullable>enable</Nullable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Avalonia.iOS" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>Its Info.plist, tabs and all.</summary>
    private const string ManifestText = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        + "<plist version=\"1.0\">\n<dict>\n\t<key>CFBundleDisplayName</key>\n\t<string>XApp</string>\n\t<key>LSRequiresIPhoneOS</key>\n\t<true/>\n"
        + "\t<key>MinimumOSVersion</key>\n\t<string>13.0</string>\n\t<key>UIDeviceFamily</key>\n\t<array>\n\t\t<integer>1</integer>\n\t</array>\n</dict>\n</plist>\n";

    /// <summary>The iOS workload's answer of the head at net11.0-ios with Xcode 27.2, as MSBuild gave it.</summary>
    private const string Answered = """
        {
          "Properties": {
            "_SdkVersion": "27.2",
            "AppBundleManifest": "Info.plist",
            "SupportedOSPlatformVersion": "13.0",
            "MinSupportedOSPlatformVersion": "15.0",
            "ValidateSceneManifest": "true",
            "_CanOutputAppBundle": "true",
            "IsAppExtension": "false"
          },
          "Items": {
            "PartialAppManifest": [],
            "PackageReference": [ { "Identity": "Avalonia.iOS" } ],
            "PackageVersion": [ { "Identity": "Avalonia", "Version": "12.1.6" }, { "Identity": "Avalonia.iOS", "Version": "12.1.6" } ]
          }
        }
        """;

    private static IosHeadAnswer Answer(string json = Answered) => IosHeads.Answer(new MSBuildAnswer(new ChildResult(0, json, string.Empty)), "/src/XApp.iOS")!;

    private static IosHeadEdits? Edits(string project, string manifest, IosHeadAnswer answer, string framework = "net10.0-ios") =>
        IosHeads.Edits(Project, "XApp.iOS", framework, IosHeads.OnCoreClr(framework), answer, path => path == Project ? project : path == Manifest ? manifest : null);

    [Fact]
    public void Avalonias_template_head_needs_its_framework_moved_its_least_ios_raised_in_both_files_and_a_scene_manifest()
    {
        IosHeadEdits edits = Edits(ProjectText, ManifestText, Answer())!;

        Assert.Equal(("net10.0-ios", "net11.0-ios", "15.0", "13.0", "13.0", "27.2"), (edits.Framework, edits.Moved, edits.Least, edits.Version, edits.ManifestMinimum, edits.SceneSdk));
        Assert.True(edits is { ScenesDelegated: true, ByHand: null, Makes: true }, $"The template's head was not offered its edits: {edits}");
        Assert.Equal([Project, Manifest], edits.Files);
        Assert.Equal("XApp.iOS needs edits to build here: net11.0-ios in place of net10.0-ios, whose iOS apps run on Mono (Passenger builds, runs and debugs mobile "
            + "apps from .NET 11, on CoreCLR); iOS 15.0 at the least, the least .NET 11's iOS workload builds for, in place of 13.0 in its project and Info.plist; "
            + "and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app", edits.Said);

        Assert.Equal(ProjectText.Replace("net10.0-ios", "net11.0-ios", StringComparison.Ordinal).Replace(">13.0<", ">15.0<", StringComparison.Ordinal),
            IosHeads.ProjectText(ProjectText, edits).Text);
        Assert.Equal(ManifestText.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal).Replace("</dict>\n</plist>",
            "\t<key>UIApplicationSceneManifest</key>\n\t<dict>\n\t\t<key>UIApplicationSupportsMultipleScenes</key>\n\t\t<false/>\n\t</dict>\n</dict>\n</plist>",
            StringComparison.Ordinal), IosHeads.ManifestText(ManifestText, edits));
    }

    [Fact]
    public void A_head_on_CoreCLR_with_its_least_ios_and_a_scene_manifest_needs_nothing_and_one_lacking_only_the_manifest_needs_that_alone()
    {
        IosHeadAnswer moved = Answer(Answered.Replace("\"13.0\"", "\"15.0\"", StringComparison.Ordinal));
        string project = ProjectText.Replace("net10.0-ios", "net11.0-ios", StringComparison.Ordinal).Replace(">13.0<", ">15.0<", StringComparison.Ordinal);
        string manifest = ManifestText.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal);
        string scened = AppManifests.WithSceneManifest(manifest)!;

        Assert.Null(Edits(project, scened, moved, "net11.0-ios"));
        IosHeadEdits edits = Edits(project, manifest, moved, "net11.0-ios")!;
        Assert.True(edits is { Moved: null, Least: null, ManifestMinimum: null, SceneSdk: "27.2" }, $"A head lacking only its scene manifest was asked more: {edits}");
        Assert.Equal([Manifest], edits.Files);
        Assert.Equal("XApp.iOS needs an edit to build here: a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app", edits.Said);
        // An Xcode before 27's SDK asks for none.
        Assert.Null(Edits(project, manifest, moved with { SdkVersion = "26.2" }, "net11.0-ios"));
    }

    [Fact]
    public void An_app_whose_delegate_hands_ios_no_scene_delegate_is_told_it_needs_one_and_its_manifest_is_left_to_the_person()
    {
        IosHeadAnswer older = Answer() with { Avalonia = null };
        IosHeadEdits edits = Edits(ProjectText, ManifestText, older)!;

        Assert.False(edits.ScenesDelegated);
        Assert.EndsWith($"; and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app, and a scene delegate of the app's own ({IosHeadEdits.SceneGuide})",
            edits.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("UIApplicationSceneManifest", IosHeads.ManifestText(ManifestText, edits), StringComparison.Ordinal);
    }

    [Fact]
    public void A_least_ios_set_outside_the_projects_own_properties_is_left_to_the_person_saying_where_and_the_rest_made()
    {
        string conditioned = ProjectText.Replace("<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>", string.Empty, StringComparison.Ordinal)
            .Replace("</Project>", "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">\n    <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>\n  </PropertyGroup>\n</Project>",
                StringComparison.Ordinal);
        IosHeadEdits edits = Edits(conditioned, ManifestText, Answer())!;

        Assert.Equal("Its SupportedOSPlatformVersion 13.0 is set outside the project's own properties: set 15.0 there", edits.ByHand);
        Assert.Equal([Project, Manifest], edits.Files);
        Assert.Contains("<TargetFramework>net11.0-ios</TargetFramework>", IosHeads.ProjectText(conditioned, edits).Text, StringComparison.Ordinal);
        Assert.Contains("<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>", IosHeads.ProjectText(conditioned, edits).Text, StringComparison.Ordinal);
        Assert.EndsWith(". Its SupportedOSPlatformVersion 13.0 is set outside the project's own properties: set 15.0 there", edits.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frameworks_list_moves_its_ios_framework_alone_and_an_app_extension_is_no_head()
    {
        string several = ProjectText.Replace("<TargetFramework>net10.0-ios</TargetFramework>", "<TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>", StringComparison.Ordinal);
        IosHeadEdits edits = Edits(several, ManifestText, Answer())!;

        Assert.Contains("<TargetFrameworks>net10.0;net11.0-ios</TargetFrameworks>", IosHeads.ProjectText(several, edits).Text, StringComparison.Ordinal);
        Assert.Equal("net10.0-ios", IosHeads.HeadOf(["net10.0", "net10.0-ios"]));
        Assert.Null(IosHeads.HeadOf(["net11.0-android", "net11.0-ios"]));
        Assert.Null(Edits(ProjectText, ManifestText, Answer() with { MakesApp = false }));
    }

    [Fact]
    public void A_mobile_want_says_an_ios_heads_edits_in_place_of_its_move_from_mono_and_names_it_where_it_lacks_more()
    {
        IosHeadEdits edits = Edits(ProjectText, ManifestText, Answer())!;
        MobileWant want = MobileWant.Of([new ProjectNeeds("XApp.iOS", ["net10.0-ios"]) { Ios = edits }], onMac: true, _ => null, "/sdk", _ => false);

        Assert.Empty(want.OnMono);
        Assert.Equal(edits.Said, want.Said);

        MobileWant lacking = MobileWant.Of([new ProjectNeeds("XApp.iOS", ["net11.0-ios"]) { Ios = edits, Apple = new AppleAnswer(null) { Simulators = ["iOS 27.2"] } }],
            onMac: true, _ => null, "/sdk", _ => false);
        Assert.StartsWith("XApp.iOS needs the iOS 27.2 simulator runtime, which Xcode installs", lacking.Said, StringComparison.Ordinal);
        Assert.EndsWith($". {edits.Said}", lacking.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_info_plists_keys_are_read_past_its_doctype_and_text_that_is_no_property_list_is_passed_over()
    {
        IReadOnlyDictionary<string, string?> keys = AppManifests.Keys(ManifestText)!;

        Assert.Equal(["CFBundleDisplayName", "LSRequiresIPhoneOS", "MinimumOSVersion", "UIDeviceFamily"], keys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("13.0", null), (keys["MinimumOSVersion"], keys["LSRequiresIPhoneOS"]));
        Assert.Null(AppManifests.Keys("<Project />"));
        Assert.Null(AppManifests.Keys("not xml"));
        Assert.Null(AppManifests.WithSceneManifest(AppManifests.WithSceneManifest(ManifestText)!));
        string crlf = ManifestText.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.DoesNotContain("\n", AppManifests.WithSceneManifest(crlf)!.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
