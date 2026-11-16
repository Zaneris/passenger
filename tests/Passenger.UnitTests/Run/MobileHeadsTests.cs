using Passenger.App.Mobile;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>The edits a mobile project's heads need to build here, from its project's text, its Info.plist files and what
/// each head's workload answers at the framework it builds on CoreCLR: Avalonia's cross-platform template's iOS head needs
/// three, its Android head two, a MAUI app made before its template gave its Apple heads scenes needs every framework moved,
/// Android's least API raised where its own condition sets it and a scene manifest naming a SceneDelegate of MAUI's in each
/// Apple head, and a Mac Catalyst head its LSMinimumSystemVersion mapped as its workload maps versions; each said in the
/// banner's words and made in the files' text, every other character kept, and placed on the lines it changes.</summary>
public class MobileHeadsTests
{
    private static readonly string Folder = Rooted("/src/XApp.iOS");

    private static readonly string Project = Rooted("/src/XApp.iOS/XApp.iOS.csproj");

    private static readonly string Manifest = Rooted("/src/XApp.iOS/Info.plist");

    /// <summary>A path of the tests' made-up folders as this system writes it, canonical as a head's answer makes its files'.</summary>
    private static string Rooted(string path) => PathNames.Canonical(path);

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
            "IsAppExtension": "false",
            "_XamarinSdkRootDirectory": "/dotnet/packs/Microsoft.iOS.Sdk.net11.0_27.2/27.2.11039/",
            "UseMaui": "",
            "RootNamespace": "XApp.iOS"
          },
          "Items": {
            "PartialAppManifest": [],
            "PackageReference": [ { "Identity": "Avalonia.iOS" } ],
            "PackageVersion": [ { "Identity": "Avalonia", "Version": "12.1.6" }, { "Identity": "Avalonia.iOS", "Version": "12.1.6" } ]
          }
        }
        """;

    private static HeadAnswer Answer(string json = Answered, string platform = FrameworkNames.Ios, string? folder = null) =>
        MobileHeads.Answer(platform, $"net11.0-{platform}", new MSBuildAnswer(new ChildResult(0, json, string.Empty)), folder ?? Folder)!;

    private static HeadEdits? Edits(string project, string manifest, params HeadAnswer[] answers) =>
        MobileHeads.Edits(Project, "XApp.iOS", answers, path => path == Project ? project : path == Manifest ? manifest : null);

    [Fact]
    public void Avalonias_template_ios_head_needs_its_framework_moved_its_least_ios_raised_in_both_files_and_a_scene_manifest()
    {
        HeadEdits edits = Edits(ProjectText, ManifestText, Answer())!;
        ManifestEdit manifest = Assert.Single(edits.Manifests);

        Assert.Equal([("net10.0-ios", "net11.0-ios")], edits.Moves);
        Assert.Equal([new HeadMinimum(FrameworkNames.Ios, "13.0", "15.0")], edits.Raised);
        Assert.True(manifest is { Now: "15.0", Was: "13.0", SceneSdk: "27.2", Scenes: SceneDelegation.AppDelegate, DelegateFile: null }, $"The plist's edits: {manifest}");
        Assert.True(edits is { ByHand: null, Makes: true }, $"The template's head was not offered its edits: {edits}");
        Assert.Equal([Project, Manifest], edits.Files);
        Assert.Equal("XApp.iOS needs edits to build and run here: net11.0-ios in place of net10.0-ios, whose iOS apps run on Mono (Passenger builds, runs and debugs mobile "
            + "apps from .NET 11, on CoreCLR); iOS 15.0 at the least, the least .NET 11's iOS workload builds for, in place of 13.0 in its project and Info.plist; "
            + "and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app", edits.Said);

        Assert.Equal(ProjectText.Replace("net10.0-ios", "net11.0-ios", StringComparison.Ordinal).Replace(">13.0<", ">15.0<", StringComparison.Ordinal),
            MobileHeads.ProjectText(ProjectText, edits).Text);
        Assert.Equal(ManifestText.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal).Replace("</dict>\n</plist>",
            "\t<key>UIApplicationSceneManifest</key>\n\t<dict>\n\t\t<key>UIApplicationSupportsMultipleScenes</key>\n\t\t<false/>\n\t</dict>\n</dict>\n</plist>",
            StringComparison.Ordinal), MobileHeads.ManifestText(ManifestText, manifest));
    }

    [Fact]
    public void The_fix_stands_on_the_lines_an_edit_changes_in_the_project_and_its_info_plist()
    {
        HeadEdits edits = Edits(ProjectText, ManifestText, Answer())!;

        Assert.Equal(["<TargetFramework>net10.0-ios</TargetFramework>", "<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>"],
            MobileHeads.Places(Project, ProjectText, edits).Select(p => ProjectText[p.Start..p.End].Trim()));
        Assert.Equal(["<string>13.0</string>", "<dict>"], MobileHeads.Places(Manifest, ManifestText, edits).Select(p => ManifestText[p.Start..p.End].Trim()));
        Assert.Empty(MobileHeads.Places(Rooted("/src/XApp/App.axaml.cs"), ProjectText, edits));
    }

    [Fact]
    public void A_head_on_CoreCLR_with_its_least_ios_and_a_scene_manifest_needs_nothing_and_one_lacking_only_the_manifest_needs_that_alone()
    {
        HeadAnswer moved = Answer(Answered.Replace("\"13.0\"", "\"15.0\"", StringComparison.Ordinal));
        string project = ProjectText.Replace("net10.0-ios", "net11.0-ios", StringComparison.Ordinal).Replace(">13.0<", ">15.0<", StringComparison.Ordinal);
        string manifest = ManifestText.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal);

        Assert.Null(Edits(project, AppManifests.WithSceneManifest(manifest)!, moved));
        HeadEdits edits = Edits(project, manifest, moved)!;
        Assert.True(edits is { Moves: [], Raised: [], WritesProject: false, Manifests: [{ Now: null, SceneSdk: "27.2" }] }, $"A head lacking only its scene manifest was asked more: {edits}");
        Assert.Equal([Manifest], edits.Files);
        Assert.Equal("XApp.iOS needs an edit to build and run here: a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app", edits.Said);
        // An Xcode before 27's SDK asks for none.
        Assert.Null(Edits(project, manifest, moved with { SdkVersion = "26.2" }));
    }

    [Fact]
    public void An_app_whose_delegate_hands_ios_no_scene_delegate_is_told_it_needs_one_and_its_manifest_is_left_to_the_person()
    {
        HeadEdits edits = Edits(ProjectText, ManifestText, Answer() with { Avalonia = null })!;
        ManifestEdit manifest = Assert.Single(edits.Manifests);

        Assert.Equal(SceneDelegation.OwnToWrite, manifest.Scenes);
        Assert.EndsWith($"; and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS 27.2 SDK asks of every app, and a scene delegate of the app's own ({HeadEdits.SceneGuide})",
            edits.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("UIApplicationSceneManifest", MobileHeads.ManifestText(ManifestText, manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void A_least_ios_set_outside_the_projects_own_properties_is_left_to_the_person_saying_where_and_the_rest_made()
    {
        string conditioned = ProjectText.Replace("<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>", string.Empty, StringComparison.Ordinal)
            .Replace("</Project>", "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">\n    <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>\n  </PropertyGroup>\n</Project>",
                StringComparison.Ordinal);
        HeadEdits edits = Edits(conditioned, ManifestText, Answer())!;

        Assert.Equal("Its iOS SupportedOSPlatformVersion 13.0 is set outside the project's own properties: set 15.0 there", edits.ByHand);
        Assert.Equal([Project, Manifest], edits.Files);
        string made = MobileHeads.ProjectText(conditioned, edits).Text;
        Assert.Contains("<TargetFramework>net11.0-ios</TargetFramework>", made, StringComparison.Ordinal);
        Assert.Contains("<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>", made, StringComparison.Ordinal);
        Assert.EndsWith(". Its iOS SupportedOSPlatformVersion 13.0 is set outside the project's own properties: set 15.0 there", edits.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frameworks_list_moves_its_ios_framework_alone_and_an_app_extension_is_no_head()
    {
        string several = ProjectText.Replace("<TargetFramework>net10.0-ios</TargetFramework>", "<TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>", StringComparison.Ordinal);
        HeadEdits edits = Edits(several, ManifestText, Answer())!;

        Assert.Contains("<TargetFrameworks>net10.0;net11.0-ios</TargetFrameworks>", MobileHeads.ProjectText(several, edits).Text, StringComparison.Ordinal);
        Assert.Equal(["net10.0-ios"], MobileHeads.HeadsOf(["net10.0", "net10.0-ios"], onMac: true));
        Assert.Equal(["net11.0-android", "net11.0-ios"], MobileHeads.HeadsOf(["net11.0-android", "net11.0-ios"], onMac: true));
        Assert.Equal(["net11.0-android"], MobileHeads.HeadsOf(["net11.0-android", "net11.0-ios"], onMac: false));
        Assert.Empty(MobileHeads.HeadsOf(["net11.0-ios", "net11.0-tvos"], onMac: true));
        Assert.True(Edits(ProjectText, ManifestText, Answer() with { MakesApp = false }) is { Manifests: [] }, "An app extension's Info.plist was edited");
    }

    /// <summary>Avalonia 12.1.6's avalonia.xplat Android head, as the template writes it.</summary>
    private const string AndroidText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0-android</TargetFramework>
            <SupportedOSPlatformVersion>23</SupportedOSPlatformVersion>
            <ApplicationId>com.CompanyName.XApp</ApplicationId>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Avalonias_template_android_head_needs_its_framework_moved_and_its_least_api_raised_written_as_its_own()
    {
        string android = Rooted("/src/XApp.Android/XApp.Android.csproj");
        HeadAnswer answer = Answer("""{ "Properties": { "SupportedOSPlatformVersion": "23", "AndroidMinimumSupportedApiLevel": "24", "UseMaui": "", "RootNamespace": "XApp.Android" } }""",
            FrameworkNames.Android, Rooted("/src/XApp.Android"));
        HeadEdits edits = MobileHeads.Edits(android, "XApp.Android", [answer], path => path == android ? AndroidText : null)!;

        Assert.Equal("XApp.Android needs edits to build and run here: net11.0-android in place of net10.0-android, whose Android apps run on Mono (Passenger builds, runs and "
            + "debugs mobile apps from .NET 11, on CoreCLR); and Android API 24 at the least, the least .NET 11's Android workload builds for, in place of 23 in its project", edits.Said);
        Assert.Equal([android], edits.Files);
        Assert.Equal(AndroidText.Replace("net10.0-android", "net11.0-android", StringComparison.Ordinal).Replace(">23<", ">24<", StringComparison.Ordinal),
            MobileHeads.ProjectText(AndroidText, edits).Text);
    }

    private static readonly string MauiProject = Rooted("/src/M10/M10.csproj");

    private static readonly string MauiIos = Rooted("/src/M10/Platforms/iOS/Info.plist");

    private static readonly string MauiCatalyst = Rooted("/src/M10/Platforms/MacCatalyst/Info.plist");

    /// <summary>A MAUI app's property group as the net10.0 template writes it, its frameworks by system.</summary>
    private const string MauiText = """
        <Project Sdk="Microsoft.NET.Sdk">
        	<PropertyGroup>
        		<TargetFrameworks>net10.0-android</TargetFrameworks>
        		<TargetFrameworks Condition="!$([MSBuild]::IsOSPlatform('linux'))">$(TargetFrameworks);net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
        		<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('windows'))">$(TargetFrameworks);net10.0-windows10.0.19041.0</TargetFrameworks>
        		<OutputType>Exe</OutputType>
        		<UseMaui>true</UseMaui>
        		<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
        		<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">17.0</SupportedOSPlatformVersion>
        		<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">21.0</SupportedOSPlatformVersion>
        	</PropertyGroup>
        </Project>
        """;

    /// <summary>An Apple head's Info.plist of a MAUI app made before its template gave it scenes.</summary>
    private const string MauiPlist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>UIDeviceFamily</key>\n\t<array>\n\t\t<integer>2</integer>\n"
        + "\t</array>\n</dict>\n</plist>\n";

    /// <summary>A MAUI app's Apple head's answer, as MSBuild writes it: its object begun on a line of its own.</summary>
    private static HeadAnswer MauiAnswer(string platform, string version, string least, string plist) => Answer($$"""
        {
          "Properties": { "_SdkVersion": "27.2", "AppBundleManifest": "{{plist}}", "SupportedOSPlatformVersion": "{{version}}", "MinSupportedOSPlatformVersion": "{{least}}",
            "ValidateSceneManifest": "true", "_CanOutputAppBundle": "true", "IsAppExtension": "false", "UseMaui": "true", "RootNamespace": "M10" },
          "Items": { "PartialAppManifest": [], "PackageReference": [ { "Identity": "Microsoft.Maui.Controls", "Version": "10.0.0" } ], "PackageVersion": [] }
        }
        """, platform, Rooted("/src/M10"));

    [Fact]
    public void A_maui_app_made_before_its_apple_heads_had_scenes_moves_every_framework_raises_android_where_its_condition_sets_it_and_names_mauis_scene_delegate()
    {
        HeadAnswer android = Answer("""{ "Properties": { "SupportedOSPlatformVersion": "21.0", "AndroidMinimumSupportedApiLevel": "24", "UseMaui": "true", "RootNamespace": "M10" } }""",
            FrameworkNames.Android, Rooted("/src/M10"));
        HeadEdits edits = MobileHeads.Edits(MauiProject, "M10", [android, MauiAnswer(FrameworkNames.Ios, "15.0", "15.0", "Platforms/iOS/Info.plist"),
            MauiAnswer(FrameworkNames.MacCatalyst, "17.0", "17.0", "Platforms/MacCatalyst/Info.plist")],
            path => path == MauiProject ? MauiText : path == MauiIos || path == MauiCatalyst ? MauiPlist : null)!;

        Assert.Equal([("net10.0-android", "net11.0-android"), ("net10.0-ios", "net11.0-ios"), ("net10.0-maccatalyst", "net11.0-maccatalyst"),
            ("net10.0-windows10.0.19041.0", "net11.0-windows10.0.19041.0")], edits.Moves);
        Assert.Equal([new HeadMinimum(FrameworkNames.Android, "21", "24")], edits.Raised);
        Assert.Equal([MauiProject, MauiIos, MauiCatalyst, Rooted("/src/M10/Platforms/iOS/SceneDelegate.cs"),
            Rooted("/src/M10/Platforms/MacCatalyst/SceneDelegate.cs")], edits.Files);
        Assert.Equal("M10 needs edits to build and run here: net11.0-android, net11.0-ios, net11.0-maccatalyst and net11.0-windows10.0.19041.0 in place of net10.0-android, "
            + "net10.0-ios, net10.0-maccatalyst and net10.0-windows10.0.19041.0, whose mobile apps run on Mono (Passenger builds, runs and debugs mobile apps from .NET 11, "
            + "on CoreCLR); Android API 24 at the least, the least .NET 11's Android workload builds for, in place of 21 in its project; a scene manifest "
            + "(UIApplicationSceneManifest) in Platforms/iOS/Info.plist, which the iOS 27.2 SDK asks of every app, naming a SceneDelegate of MAUI's MauiUISceneDelegate "
            + "written beside it; and a scene manifest (UIApplicationSceneManifest) in Platforms/MacCatalyst/Info.plist, which the Mac Catalyst 27.2 SDK asks of every app, "
            + "naming a SceneDelegate of MAUI's MauiUISceneDelegate written beside it", edits.Said);

        Assert.Equal(MauiText.Replace("net10.0-", "net11.0-", StringComparison.Ordinal).Replace("'android'\">21.0<", "'android'\">24.0<", StringComparison.Ordinal),
            MobileHeads.ProjectText(MauiText, edits).Text);
        Assert.Equal(MauiPlist.Replace("</dict>\n</plist>", "\t<key>UIApplicationSceneManifest</key>\n\t<dict>\n\t\t<key>UIApplicationSupportsMultipleScenes</key>\n\t\t<false/>\n"
            + "\t\t<key>UISceneConfigurations</key>\n\t\t<dict>\n\t\t\t<key>UIWindowSceneSessionRoleApplication</key>\n\t\t\t<array>\n\t\t\t\t<dict>\n"
            + "\t\t\t\t\t<key>UISceneConfigurationName</key>\n\t\t\t\t\t<string>__MAUI_DEFAULT_SCENE_CONFIGURATION__</string>\n"
            + "\t\t\t\t\t<key>UISceneDelegateClassName</key>\n\t\t\t\t\t<string>SceneDelegate</string>\n\t\t\t\t</dict>\n\t\t\t</array>\n\t\t</dict>\n\t</dict>\n</dict>\n</plist>",
            StringComparison.Ordinal), MobileHeads.ManifestText(MauiPlist, edits.Manifests[0]));
        Assert.Equal("using Foundation;\n\nnamespace M10;\n\n[Register(\"SceneDelegate\")]\npublic class SceneDelegate : MauiUISceneDelegate\n{\n}\n",
            MobileHeads.DelegateText(edits.Manifests[0].Namespace));
    }

    [Fact]
    public void A_mac_catalyst_heads_LSMinimumSystemVersion_is_set_to_the_macOS_its_workload_maps_its_least_version_to()
    {
        string catalyst = Rooted("/src/Cat/Cat.csproj");
        string plist = Rooted("/src/Cat/Info.plist");
        string versions = Rooted("/packs/catalyst/Versions.plist");
        string project = ProjectText.Replace("net10.0-ios", "net10.0-maccatalyst", StringComparison.Ordinal).Replace(">13.0<", ">15.0<", StringComparison.Ordinal);
        string manifest = AppManifests.WithSceneManifest(ManifestText.Replace("MinimumOSVersion", "LSMinimumSystemVersion", StringComparison.Ordinal)
            .Replace("<string>13.0</string>", "<string>12.0</string>", StringComparison.Ordinal))!;
        HeadAnswer answer = Answer(Answered.Replace("\"13.0\"", "\"15.0\"", StringComparison.Ordinal).Replace("\"15.0\",\n    \"ValidateSceneManifest\"", "\"17.0\",\n    \"ValidateSceneManifest\"", StringComparison.Ordinal),
            FrameworkNames.MacCatalyst, Rooted("/src/Cat")) with { SdkRoot = Rooted("/packs/catalyst") };
        string map = "<plist version=\"1.0\">\n<dict>\n\t<key>MacCatalystVersionMap</key>\n\t<dict>\n\t\t<key>15.0</key>\n\t\t<string>12.0</string>\n"
            + "\t\t<key>17.0</key>\n\t\t<string>14.0</string>\n\t</dict>\n</dict>\n</plist>\n";
        HeadEdits edits = MobileHeads.Edits(catalyst, "Cat", [answer], path => path == catalyst ? project : path == plist ? manifest : path == versions ? map : null)!;

        Assert.Equal("17.0", answer.Least);
        ManifestEdit edit = Assert.Single(edits.Manifests);
        Assert.True(edit is { Key: "LSMinimumSystemVersion", Was: "12.0", Now: "14.0", SceneSdk: null }, $"The Catalyst head's plist edits: {edit}");
        Assert.Equal("Cat needs edits to build and run here: net11.0-maccatalyst in place of net10.0-maccatalyst, whose Mac Catalyst apps run on Mono (Passenger builds, runs "
            + "and debugs mobile apps from .NET 11, on CoreCLR); Mac Catalyst 17.0 at the least, the least .NET 11's Mac Catalyst workload builds for, in place of 15.0 "
            + "in its project; and macOS 14.0 as Info.plist's LSMinimumSystemVersion, Mac Catalyst 17.0's, in place of 12.0", edits.Said);
        Assert.Contains("<key>LSMinimumSystemVersion</key>\n\t<string>14.0</string>", MobileHeads.ManifestText(manifest, edit), StringComparison.Ordinal);
        Assert.Equal("Its LSMinimumSystemVersion 12.0 is to be the macOS version of Mac Catalyst 17.0",
            MobileHeads.Edits(catalyst, "Cat", [answer], path => path == catalyst ? project : path == plist ? manifest : null)!.ByHand);
    }

    [Fact]
    public void A_mac_catalyst_heads_trimmed_debug_build_is_kept_out_of_the_banner_and_made_whole_with_mtouchlink_none_for_debug_alone()
    {
        string catalyst = Rooted("/src/Cat/Cat.csproj");
        string project = ProjectText.Replace("net10.0-ios", "net11.0-maccatalyst", StringComparison.Ordinal).Replace(">13.0<", ">17.0<", StringComparison.Ordinal);
        HeadAnswer answer = Answer(Answered.Replace("\"13.0\"", "\"17.0\"", StringComparison.Ordinal).Replace("\"15.0\"", "\"17.0\"", StringComparison.Ordinal),
            FrameworkNames.MacCatalyst, Rooted("/src/Cat")) with { LinkMode = "SdkOnly", Manifest = null };
        HeadEdits edits = MobileHeads.Edits(catalyst, "Cat", [answer], path => path == catalyst ? project : null)!;

        Assert.True(edits is { TrimsDebug: true, TrimmedPlatform: null, Needed: false, Makes: false, Files: [] }, $"The trimmed Debug build was taken for an edit the banner makes: {edits}");
        MobileWant want = MobileWant.Of([new ProjectNeeds("Cat", ["net11.0-maccatalyst"]) { Heads = edits }], onMac: true, _ => null, "/sdk", _ => false);
        Assert.True(want is { Said: null, Heads: [], Trimmed: [{ Name: "Cat" }] }, $"The banner said the trimmed Debug build: {want.Said}");
        Assert.Contains("    <Nullable>enable</Nullable>\n    <MtouchLink Condition=\"'$(Configuration)' == 'Debug'\">None</MtouchLink>\n  </PropertyGroup>",
            MobileHeads.WholeText(project, edits.TrimmedPlatform), StringComparison.Ordinal);
        Assert.Equal("Cat's Mac Catalyst Debug build keeps .NET's own assemblies whole (<MtouchLink>None</MtouchLink>): delete its obj and bin folders before its next "
            + "build, which fails on what a trimmed one left there", MobileHeads.Whole("Cat"));
        Assert.Equal(["<TargetFramework>net11.0-maccatalyst</TargetFramework>"], MobileHeads.CatalystPlaces(project).Select(p => project[p.Start..p.End].Trim()));

        string debugGroup = project.Replace("</Project>", "  <PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">\n    <DebugType>full</DebugType>\n  </PropertyGroup>\n</Project>",
            StringComparison.Ordinal);
        Assert.Contains("<DebugType>full</DebugType>\n    <MtouchLink>None</MtouchLink>", MobileHeads.WholeText(debugGroup, null), StringComparison.Ordinal);
        Assert.Null(MobileHeads.WholeText(MobileHeads.WholeText(project, null)!, null));
        Assert.Null(Edits(project, ManifestText, answer with { LinkMode = "None", Manifest = null }));
        string several = project.Replace("<TargetFramework>net11.0-maccatalyst</TargetFramework>", "<TargetFrameworks>net11.0-ios;net11.0-maccatalyst</TargetFrameworks>",
            StringComparison.Ordinal);
        HeadEdits both = MobileHeads.Edits(catalyst, "Cat", [answer], path => path == catalyst ? several : null)!;
        Assert.Contains("<MtouchLink Condition=\"'$(Configuration)' == 'Debug' And $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'\">None</MtouchLink>",
            MobileHeads.WholeText(several, both.TrimmedPlatform), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mobile_want_says_a_projects_head_edits_in_place_of_its_move_from_mono_and_names_it_where_it_lacks_more()
    {
        HeadEdits edits = Edits(ProjectText, ManifestText, Answer())!;
        MobileWant want = MobileWant.Of([new ProjectNeeds("XApp.iOS", ["net10.0-ios"]) { Heads = edits }], onMac: true, _ => null, "/sdk", _ => false);

        Assert.Empty(want.OnMono);
        Assert.Equal(edits.Said, want.Said);

        MobileWant lacking = MobileWant.Of([new ProjectNeeds("XApp.iOS", ["net11.0-ios"]) { Heads = edits, Apple = new AppleAnswer(null) { Simulators = ["iOS 27.2"] } }],
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
