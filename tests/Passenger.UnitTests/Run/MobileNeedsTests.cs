using System.Text;
using System.Xml;
using Passenger.App.Mobile;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>What a mobile project needs, worked out from what its workloads answer and what is on disk: the workloads the
/// SDK names, the Android SDK's packages and the JDK the Android workload wants, the Xcode an Apple workload could not
/// find, a project on Mono, and the Android SDK's licences of what is to be installed.</summary>
public class MobileNeedsTests
{
    /// <summary>_GetRequiredWorkloads of a project targeting tvOS and macOS where neither workload is installed.</summary>
    private const string Required = """
        {
          "TargetResults": {
            "_GetRequiredWorkloads": {
              "Result": "Success",
              "Items": [
                { "Identity": "tvos", "AdditionalProperties": "TargetFramework=net11.0-tvos", "VisualStudioComponentId": "tvos" },
                { "Identity": "macos", "AdditionalProperties": "TargetFramework=net11.0-macos", "VisualStudioComponentId": "macos" },
                { "Identity": "tvos", "AdditionalProperties": "TargetFramework=net11.0-tvos" }
              ]
            }
          }
        }
        """;

    /// <summary>GetAndroidDependencies of the MAUI template's Android head on a Mac with no JDK, as its output says it.</summary>
    private const string AndroidSaid = """
        {
          "Properties": {
            "AndroidSdkDirectory": "",
            "JavaSdkDirectory": "",
            "JavaSdkVersion": "21.0.8"
          },
          "Items": {
            "AndroidDependency": [
              { "Identity": "platforms/android-37.0" },
              { "Identity": "build-tools/37.0.0", "Version": "37.0.0" },
              { "Identity": "platform-tools", "Version": "37.0.1" },
              { "Identity": "cmdline-tools/21.0", "Version": "21.0" }
            ],
            "JavaDependency": [ { "Identity": "jdk", "Version": "21.0.8" } ]
          }
        }
        """;

    /// <summary>Its warnings, as its error stream says them.</summary>
    private const string AndroidWarned = """
        /dotnet/packs/Microsoft.Android.Sdk.Darwin/37.2.31/tools/Xamarin.Android.Tooling.targets(55,5): warning XA5300: The Java SDK directory could not be found. Install the Java SDK by following the instructions at: https://aka.ms/dotnet-android-install-sdk [/src/MauiApp1/MauiApp1.csproj]
        /dotnet/packs/Microsoft.Android.Sdk.Darwin/37.2.31/targets/Microsoft.Android.Sdk.Tooling.targets(35,5): warning XA5300: The Android SDK directory could not be found. Install the Android SDK by following the instructions at: https://aka.ms/dotnet-android-install-sdk [/src/MauiApp1/MauiApp1.csproj]
        """;

    private static readonly string Sdk = Path.Combine("/home", "Library", "Android", "sdk");

    private const string Maui = "MauiApp1";

    private static readonly string[] MauiFrameworks = ["net11.0-android", "net11.0-ios", "net11.0-maccatalyst"];

    /// <summary>A Mac's Android SDK with its platform tools and API 36 alone, where Xcode is in Applications but not chosen.</summary>
    private static readonly HashSet<string> OnDisk = [Path.Combine(Sdk, "platform-tools"), Path.Combine(Sdk, "platforms"), Path.Combine(Sdk, "platforms", "android-36", "android.jar"),
        Path.Combine(Sdk, "build-tools", "36.1.0")];

    private static MobileWant Want(IReadOnlyList<ProjectNeeds> projects, bool onMac = true, IReadOnlySet<string>? disk = null, string? androidHome = null) =>
        MobileWant.Of(projects, onMac, name => name == "ANDROID_HOME" ? androidHome : null, Sdk, (disk ?? OnDisk).Contains);

    [Fact]
    public void The_workloads_the_SDK_names_are_each_taken_once_with_the_framework_that_needs_it()
    {
        IReadOnlyList<WorkloadLacked> workloads = MobileAnswers.Workloads(Required);

        Assert.Equal([new WorkloadLacked("tvos", "net11.0-tvos"), new WorkloadLacked("macos", "net11.0-macos")], workloads);
        Assert.Empty(MobileAnswers.Workloads("""{ "TargetResults": { "_GetRequiredWorkloads": { "Result": "Success", "Items": [] } } }"""));
        Assert.Empty(MobileAnswers.Workloads("MSBUILD : error MSB1009: Project file does not exist."));
    }

    [Fact]
    public void The_Android_workloads_answer_names_its_packages_as_sdkmanager_does_and_hears_that_it_found_no_JDK()
    {
        AndroidAnswer answer = MobileAnswers.Android(AndroidSaid, AndroidWarned)!;

        Assert.Equal(["platforms;android-37.0", "build-tools;37.0.0", "platform-tools", "cmdline-tools;21.0"], answer.Packages);
        Assert.Equal("21.0.8", answer.JavaVersion);
        Assert.True(answer.JavaUnfound, "The workload's XA5300 said it found no JDK");
        Assert.Null(answer.Sdk);
        Assert.Null(answer.Java);
        Assert.False(MobileAnswers.Android(AndroidSaid, string.Empty)!.JavaUnfound, "With no XA5300, the workload found its JDK");
    }

    [Fact]
    public void The_MAUI_template_on_a_Mac_without_Java_says_the_JDK_the_SDK_packages_lacking_and_the_Xcode_not_chosen()
    {
        ProjectNeeds maui = new(Maui, MauiFrameworks)
        {
            Android = MobileAnswers.Android(AndroidSaid, AndroidWarned),
            Apple = new AppleAnswer("Could not find a valid Xcode developer path", "/Applications/Xcode-beta.app"),
        };

        MobileWant want = Want([maui]);

        Assert.Equal(Sdk, want.AndroidSdk);
        Assert.False(want.NoAndroidSdk, "The usual folder holds an Android SDK");
        Assert.Equal(["platforms;android-37.0", "build-tools;37.0.0"], want.AndroidPackages);
        Assert.Equal("21.0.8", want.Java);
        Assert.True(want.AndroidLacks);
        Assert.Equal([Maui], want.Projects);
        Assert.Equal("MauiApp1 needs a JDK (Java 21.0.8), the Android SDK's platforms;android-37.0 and build-tools;37.0.0 and Xcode, which is in "
            + "/Applications/Xcode-beta.app but not chosen (sudo xcode-select -s /Applications/Xcode-beta.app)", want.Said);
    }

    [Fact]
    public void An_Android_SDK_in_ANDROID_HOME_is_the_one_looked_in_and_with_none_there_the_SDK_is_said_lacking_whole()
    {
        ProjectNeeds app = new("AndroidApp", ["net11.0-android"]) { Android = MobileAnswers.Android(AndroidSaid, AndroidWarned) };

        MobileWant elsewhere = Want([app], androidHome: "/elsewhere/sdk");

        Assert.Equal("/elsewhere/sdk", elsewhere.AndroidSdk);
        Assert.True(elsewhere.NoAndroidSdk, "Nothing is in ANDROID_HOME");
        Assert.Equal(["platforms;android-37.0", "build-tools;37.0.0", "platform-tools"], elsewhere.AndroidPackages);
        Assert.Equal("AndroidApp needs a JDK (Java 21.0.8) and the Android SDK", elsewhere.Said);
    }

    [Fact]
    public void A_platform_in_the_folder_named_by_its_API_level_alone_counts_and_a_found_JDK_and_SDK_say_nothing()
    {
        HashSet<string> disk = [.. OnDisk, Path.Combine(Sdk, "platforms", "android-37", "android.jar"), Path.Combine(Sdk, "build-tools", "37.0.0")];
        AndroidAnswer found = MobileAnswers.Android(AndroidSaid.Replace("\"AndroidSdkDirectory\": \"\"", $"\"AndroidSdkDirectory\": \"{Sdk.Replace("\\", "\\\\")}\"")
            .Replace("\"JavaSdkDirectory\": \"\"", "\"JavaSdkDirectory\": \"/jdk\""), string.Empty)!;

        MobileWant want = Want([new ProjectNeeds("AndroidApp", ["net11.0-android"]) { Android = found }], disk: disk);

        Assert.False(want.AndroidLacks, $"Nothing is lacking, but it said {want.Said}");
        Assert.False(want.Says);
        Assert.Null(want.Said);
    }

    [Fact]
    public void Workloads_lacking_are_said_first_each_once_across_projects()
    {
        ProjectNeeds maui = new(Maui, MauiFrameworks) { Workloads = [new("maui-android", "net11.0-android"), new("maui-ios", "net11.0-ios")] };
        ProjectNeeds library = new("MauiLib", MauiFrameworks) { Workloads = [new("maui-android", "net11.0-android")] };

        MobileWant want = Want([maui, library]);

        Assert.Equal(["maui-android", "maui-ios"], want.Workloads);
        Assert.Equal("MauiApp1 and MauiLib need the .NET workloads maui-android and maui-ios", want.Said);
        Assert.Equal("Tv needs the .NET workload tvos", Want([new ProjectNeeds("Tv", ["net11.0-tvos"]) { Workloads = [new("tvos", "net11.0-tvos")] }]).Said);
    }

    [Fact]
    public void A_mobile_project_on_dotnet_10_is_said_to_run_on_Mono_which_Passenger_does_not_run()
    {
        MobileWant want = Want([new ProjectNeeds("OldApp", ["net10.0-android", "net10.0-ios"])]);

        Assert.Equal([("OldApp", "net10.0-android")], want.OnMono);
        Assert.Equal("OldApp targets net10.0-android, whose mobile apps run on Mono: Passenger builds, runs and debugs mobile apps from .NET 11, on CoreCLR", want.Said);
        Assert.Empty(Want([new ProjectNeeds("Desktop", ["net10.0", "net10.0-windows"])]).OnMono);
    }

    [Fact]
    public void Off_a_Mac_an_app_only_of_Apple_platforms_builds_nowhere_asks_no_Xcode_and_is_offered_only_its_platforms_own_workloads()
    {
        ProjectNeeds ios = new("IosApp", ["net11.0-ios"]) { Apple = new AppleAnswer("Could not find a valid Xcode developer path") };

        Assert.Equal("IosApp builds only on a Mac, with Xcode", Want([ios], onMac: false).Said);
        Assert.Null(Want([new ProjectNeeds(Maui, MauiFrameworks)], onMac: false).Said);

        // Linux's SDK names wasm-tools for a tvOS app, which names no framework of it; Windows' tvos lets it load to be edited.
        ProjectNeeds onLinux = new("Tv", ["net11.0-tvos"]) { Workloads = [new WorkloadLacked("wasm-tools", null)] };
        MobileWant offMac = Want([onLinux, new ProjectNeeds("Droid", ["net11.0-android"]) { Workloads = [new WorkloadLacked("android", null)] }], onMac: false);
        Assert.Equal(["android"], offMac.Workloads);
        Assert.Equal("Droid needs the .NET workload android. Tv builds only on a Mac, with Xcode", offMac.Said);
        ProjectNeeds tv = new("Tv", ["net11.0-tvos"]) { Workloads = [new WorkloadLacked("tvos", "net11.0-tvos")] };
        Assert.Equal(["tvos"], Want([tv], onMac: false).Workloads);
        Assert.Equal(["tvos"], Want([tv]).Workloads);
        Assert.Equal("IosApp needs Xcode (Could not find a valid Xcode developer path)", Want([ios]).Said);
    }

    [Fact]
    public void An_Apple_workloads_first_error_is_read_in_its_own_words_with_or_without_a_code()
    {
        Assert.Equal("Could not find a valid Xcode developer path",
            MobileAnswers.Apple(new ChildResult(1, "/packs/Xamarin.Shared.targets(2206,3): error : Could not find a valid Xcode developer path [/src/Ios.csproj]", string.Empty)));
        Assert.Equal("This version of .NET for iOS (27.2.11039) requires Xcode 27.2. The current version of Xcode is 27.0.",
            MobileAnswers.Apple(new ChildResult(1, "{}\n/packs/Xamarin.Shared.Sdk.targets(3190,3): error : This version of .NET for iOS (27.2.11039) requires Xcode 27.2. "
                + "The current version of Xcode is 27.0. [/src/Ios.csproj]", string.Empty)));
        Assert.Equal("The Java SDK directory could not be found.", MobileAnswers.FirstError("t.targets(55,5): error XA5300: The Java SDK directory could not be found. [/p.csproj]"));
        Assert.Null(MobileAnswers.Apple(new ChildResult(0, "{}", string.Empty)));
    }

    /// <summary>The iOS workload's check of Xcode 27.0, as it answers asked for its developer folder and SDK version.</summary>
    private const string AppleChecked = """
        {
          "Properties": {
            "_SdkDevPath": "/Applications/Xcode-beta.app/Contents/Developer",
            "_SdkVersion": "27.0"
          }
        }
        """;

    /// <summary>simctl's listing where Xcode has no simulator runtime, as Xcode 27.0's says it.</summary>
    private const string NoRuntimes = """
        {
          "runtimes" : [

          ]
        }
        """;

    /// <summary>simctl's listing of an older iOS runtime, iOS 27.1's, and a tvOS one it can no longer use.</summary>
    private const string SomeRuntimes = """
        {
          "runtimes" : [
            { "platform" : "iOS", "version" : "18.0", "isAvailable" : true, "name" : "iOS 18.0", "identifier" : "com.apple.CoreSimulator.SimRuntime.iOS-18-0" },
            { "platform" : "iOS", "version" : "27.1", "isAvailable" : true, "name" : "iOS 27.1", "identifier" : "com.apple.CoreSimulator.SimRuntime.iOS-27-1" },
            { "platform" : "tvOS", "version" : "27.0", "isAvailable" : false, "availabilityError" : "unavailable, runtime path not found", "name" : "tvOS 27.0" }
          ]
        }
        """;

    [Fact]
    public void An_Apple_workloads_check_names_its_Xcode_and_SDK_and_simctl_the_simulator_runtimes_it_lacks_of_the_SDKs_major_version()
    {
        Assert.Equal(("/Applications/Xcode-beta.app/Contents/Developer", "27.0"), MobileAnswers.AppleSdk(AppleChecked));
        Assert.Equal((null, null), MobileAnswers.AppleSdk("/packs/Xamarin.Shared.targets(2206,3): error : Could not find a valid Xcode developer path [/src/Ios.csproj]"));

        Assert.Equal(["iOS 27.0"], MobileAnswers.SimulatorsLacking(NoRuntimes, [("iOS", "27.0")]));
        Assert.Equal(["tvOS 27.0"], MobileAnswers.SimulatorsLacking(SomeRuntimes, [("iOS", "27.0"), ("tvOS", "27.0")]));
        Assert.Equal(["iOS 28.0"], MobileAnswers.SimulatorsLacking(SomeRuntimes, [("iOS", "28.0")]));
        Assert.Empty(MobileAnswers.SimulatorsLacking(SomeRuntimes, [("iOS", null)]));
        Assert.Equal(["tvOS"], MobileAnswers.SimulatorsLacking(SomeRuntimes, [("tvOS", null)]));
        Assert.Empty(MobileAnswers.SimulatorsLacking("simctl: error: unable to find a developer folder", [("iOS", "27.0")]));
    }

    [Fact]
    public void An_iOS_project_whose_Xcode_has_no_simulator_runtime_says_so_with_how_Xcode_installs_one()
    {
        ProjectNeeds ios = new("IosApp", ["net11.0-ios"]) { Apple = new AppleAnswer(null) { Simulators = ["iOS 27.0"] } };
        ProjectNeeds maui = new(Maui, MauiFrameworks) { Apple = new AppleAnswer(null) { Simulators = ["iOS 27.0"] } };

        MobileWant want = Want([ios, maui]);

        Assert.Equal(["iOS 27.0"], want.Simulators);
        Assert.Null(want.Xcode);
        Assert.Equal("IosApp and MauiApp1 need the iOS 27.0 simulator runtime, which Xcode installs (Xcode › Settings › Components, or xcodebuild -downloadPlatform iOS)",
            want.Said);
        Assert.Equal("Tv needs the iOS 27.0 and tvOS 27.0 simulator runtimes, which Xcode installs (Xcode › Settings › Components, "
            + "or xcodebuild -downloadPlatform iOS and xcodebuild -downloadPlatform tvOS)",
            Want([new ProjectNeeds("Tv", ["net11.0-ios", "net11.0-tvos"]) { Apple = new AppleAnswer(null) { Simulators = ["iOS 27.0", "tvOS 27.0"] } }]).Said);
        Assert.Equal("IosApp builds only on a Mac, with Xcode", Want([ios], onMac: false).Said);
    }

    [Fact]
    public void Load_problems_of_a_workload_the_Android_SDK_a_JDK_or_Xcode_are_those_the_want_explains()
    {
        Assert.True(MobileWant.Explains("The Java SDK directory could not be found. Install the Java SDK by following the instructions at: https://aka.ms/dotnet-android-install-sdk"));
        Assert.True(MobileWant.Explains("To build this project, the following workloads must be installed: maui-android"));
        Assert.True(MobileWant.Explains("Could not find a valid Xcode developer path"));
        Assert.False(MobileWant.Explains("The project file could not be loaded. Data at the root level is invalid. Line 1, position 1."));
    }

    private const string Repository = """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/03">
          <license id="android-sdk-license" type="text">Terms and Conditions

        This is the Android Software Development Kit License Agreement</license><license id="android-sdk-preview-license" type="text">Preview terms</license>
          <remotePackage path="platforms;android-37.0"><type-details/><uses-license ref="android-sdk-license"/></remotePackage>
          <remotePackage path="platforms;android-Baklava"><uses-license ref="android-sdk-preview-license"/></remotePackage>
        </sdk:sdk-repository>
        """;

    [Fact]
    public void The_licences_shown_are_those_Googles_repository_gives_the_packages_each_once_with_their_text()
    {
        using MemoryStream xml = new(Encoding.UTF8.GetBytes(Repository));
        IReadOnlyList<AndroidLicence> licences = AndroidLicences.Read(xml, ["platforms;android-37.0", "build-tools;37.0.0", "platforms;android-Baklava"]);

        Assert.Equal(["android-sdk-license", "android-sdk-preview-license"], licences.Select(l => l.Id));
        Assert.StartsWith("Terms and Conditions", licences[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("License Agreement", licences[0].Text, StringComparison.Ordinal);
        Assert.Equal("Preview terms", licences[1].Text);
    }

    [Fact]
    public void A_repository_with_a_DTD_or_without_a_licences_text_is_refused()
    {
        using MemoryStream dtd = new(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><x>&a;</x>"));
        Assert.Throws<XmlException>(() => AndroidLicences.Read(dtd, ["platforms;android-37.0"]));
        using MemoryStream bare = new(Encoding.UTF8.GetBytes("<sdk-repository><remotePackage path=\"platform-tools\"/></sdk-repository>"));
        Assert.Throws<XmlException>(() => AndroidLicences.Read(bare, ["platform-tools"]));
    }

    [Fact]
    public void A_frameworks_platform_and_dotnet_are_read_as_the_workloads_name_them()
    {
        Assert.Equal(MobilePlatforms.Android, MobilePlatforms.Of("net11.0-android36.0"));
        Assert.Equal(MobilePlatforms.MacCatalyst, MobilePlatforms.Of("net11.0-maccatalyst"));
        Assert.Null(MobilePlatforms.Of("net11.0-windows10.0.19041.0"));
        Assert.Null(MobilePlatforms.Of("net11.0"));
        Assert.Equal(10, MobilePlatforms.DotnetOf("net10.0-ios"));
        Assert.Null(MobilePlatforms.DotnetOf("netstandard2.0"));
    }
}
