using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>A workload a project's framework needs that the .NET it builds with lacks, as the SDK names it
/// (<c>_GetRequiredWorkloads</c>, what <c>dotnet workload restore</c> installs): its id, and the framework needing it.</summary>
public sealed record WorkloadLacked(string Id, string? Framework);

/// <summary>What the Android workload says a project's app needs (<c>GetAndroidDependencies</c>, Java's asked too): the
/// Android SDK and JDK folders it found, the SDK packages as sdkmanager names them (<c>platforms;android-37.0</c>), the
/// Java version it installs, and whether it found no JDK or no SDK at all.</summary>
public sealed record AndroidAnswer(string? Sdk, string? Java, IReadOnlyList<string> Packages, string? JavaVersion, bool JavaUnfound, bool SdkUnfound);

/// <summary>What the Apple workload says of the Xcode it builds with: why it cannot (none chosen, another version than
/// it needs), in its words; null where it can. <see cref="Unchosen"/> names an Xcode in Applications that none chose.</summary>
public sealed record AppleAnswer(string? Problem, string? Unchosen = null);

/// <summary>A mobile project as asked of its workloads: its name and frameworks, the workloads it lacks, and what its
/// Android and Apple workloads say where it has them.</summary>
public sealed record ProjectNeeds(string Name, IReadOnlyList<string> Frameworks)
{
    public IReadOnlyList<WorkloadLacked> Workloads { get; init; } = [];

    public AndroidAnswer? Android { get; init; }

    public AppleAnswer? Apple { get; init; }
}

/// <summary>The platforms whose apps a .NET workload builds, by a target framework's platform (<c>net11.0-android</c>).</summary>
public static class MobilePlatforms
{
    public const string Android = "android";
    public const string Ios = "ios";
    public const string MacCatalyst = "maccatalyst";
    public const string Tvos = "tvos";
    public const string Macos = "macos";

    /// <summary>The first .NET whose mobile apps run on CoreCLR, which Passenger's debugger is built for.</summary>
    public const int FirstOnCoreClr = 11;

    /// <summary>The framework's platform among those a workload builds, lower case; null for any other.</summary>
    public static string? Of(string framework)
    {
        int dash = framework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }
        string platform = new string([.. framework[(dash + 1)..].TakeWhile(char.IsLetter)]).ToLowerInvariant();
        return platform is Android or Ios or MacCatalyst or Tvos or Macos ? platform : null;
    }

    /// <summary>Whether Xcode builds the platform's apps.</summary>
    public static bool IsApple(string platform) => platform is Ios or MacCatalyst or Tvos or Macos;

    /// <summary>The major .NET version a framework names (10 for <c>net10.0-android</c>); null where it names none.</summary>
    public static int? DotnetOf(string framework)
    {
        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string digits = new([.. framework[3..].TakeWhile(char.IsDigit)]);
        return digits.Length is > 0 and < 4 && int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out int major) && major >= 5 ? major : null;
    }

    /// <summary>The project's frameworks a workload builds, in its order.</summary>
    public static IReadOnlyList<string> FrameworksOf(IEnumerable<string> frameworks) => [.. frameworks.Where(f => Of(f) is not null)];

    /// <summary>How a person names the platform.</summary>
    public static string Named(string platform) => platform switch
    {
        Android => "Android",
        Ios => "iOS",
        MacCatalyst => "Mac Catalyst",
        Tvos => "tvOS",
        _ => "macOS",
    };
}

/// <summary>Reads the workloads' answers to Passenger's questions of a project (MSBuild's <c>-getTargetResult</c>,
/// <c>-getItem</c> and <c>-getProperty</c> JSON, and the warnings and errors around it).</summary>
public static class MobileAnswers
{
    /// <summary>The workloads <c>_GetRequiredWorkloads</c> names, each once with the first framework needing it; none for
    /// output that is no such answer.</summary>
    public static IReadOnlyList<WorkloadLacked> Workloads(string output)
    {
        if (Json(output) is not { } read)
        {
            return [];
        }
        using (read)
        {
            JsonElement? result = JsonMembers.Object(JsonMembers.Object(read.RootElement, "TargetResults"), "_GetRequiredWorkloads");
            List<WorkloadLacked> found = [];
            foreach (JsonElement item in JsonMembers.Items(result, "Items"))
            {
                if (JsonMembers.Text(item, "Identity") is not { Length: > 0 } id || found.Any(w => w.Id == id))
                {
                    continue;
                }
                string? framework = (JsonMembers.Text(item, "AdditionalProperties") ?? string.Empty).Split(';')
                    .Select(p => p.Split('=', 2)).FirstOrDefault(p => p is ["TargetFramework", _])?[1];
                found.Add(new WorkloadLacked(id, framework));
            }
            return found;
        }
    }

    /// <summary>What <c>GetAndroidDependencies</c> answered, its items' SDK paths (<c>platforms/android-37.0</c>) named
    /// as sdkmanager names them, with what its warnings say the workload could not find (XA5300); null for output that is
    /// no such answer.</summary>
    public static AndroidAnswer? Android(string output, string errors)
    {
        if (Json(output) is not { } read)
        {
            return null;
        }
        using (read)
        {
            JsonElement? properties = JsonMembers.Object(read.RootElement, "Properties");
            JsonElement? items = JsonMembers.Object(read.RootElement, "Items");
            List<string> packages = [.. JsonMembers.Items(items, "AndroidDependency").Select(d => JsonMembers.Text(d, "Identity")).OfType<string>()
                .Where(p => p.Length > 0).Select(p => p.Replace('/', ';')).Distinct(StringComparer.Ordinal)];
            string? java = JsonMembers.Items(items, "JavaDependency").Select(d => JsonMembers.Text(d, "Version")).FirstOrDefault(v => v is { Length: > 0 })
                ?? JsonMembers.Text(properties, "JavaSdkVersion");
            string said = output + "\n" + errors;
            bool javaUnfound = Warned(said, "The Java SDK directory could not be found");
            bool sdkUnfound = Warned(said, "The Android SDK directory could not be found");
            return new AndroidAnswer(Folder(JsonMembers.Text(properties, "AndroidSdkDirectory")), Folder(JsonMembers.Text(properties, "JavaSdkDirectory")),
                packages, java is { Length: > 0 } ? java : null, javaUnfound, sdkUnfound);
        }

        static bool Warned(string said, string words) => said.Split('\n').Any(l => l.Contains("XA5300", StringComparison.Ordinal) && l.Contains(words, StringComparison.Ordinal));

        static string? Folder(string? path) => path is { Length: > 0 } ? path.TrimEnd('/', '\\') : null;
    }

    /// <summary>Why an Apple workload's check of Xcode failed, its first error in its own words, less MSBuild's file and
    /// project; null where it passed.</summary>
    public static string? Apple(ChildResult checkedXcode) => checkedXcode.Succeeded ? null : FirstError(checkedXcode.Output + "\n" + checkedXcode.Error);

    /// <summary>The first error MSBuild wrote, its words alone ("error XA5300: words [project]" gives "words").</summary>
    public static string? FirstError(string said)
    {
        foreach (string line in said.Split('\n'))
        {
            int at = line.IndexOf(": error ", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            string rest = line[(at + ": error ".Length)..].Trim();
            // An error with no code ("error : words") has its colon left.
            int coded = rest.IndexOf(':', StringComparison.Ordinal);
            if (coded >= 0 && !rest[..coded].Contains(' ', StringComparison.Ordinal))
            {
                rest = rest[(coded + 1)..].TrimStart();
            }
            int project = rest.LastIndexOf(" [", StringComparison.Ordinal);
            return (project > 0 ? rest[..project] : rest).Trim();
        }
        return null;
    }

    /// <summary>The JSON object MSBuild wrote first, past any line before it; null where there is none.</summary>
    private static JsonDocument? Json(string output)
    {
        int start = output.IndexOf('{');
        if (start < 0)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(output.AsMemory(start), new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException)
        {
            // Warnings after the object: read up to its end.
            int end = output.IndexOf("\n}", start, StringComparison.Ordinal);
            try
            {
                return end > 0 ? JsonDocument.Parse(output.AsMemory(start, end - start + 2)) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>Where the Android SDK is, as the workload looks for it (ANDROID_HOME, ANDROID_SDK_ROOT, then the system's
/// usual folder), and which of a project's packages it lacks.</summary>
public static class AndroidFolders
{
    /// <summary>The folder the Android SDK is usually installed in for this user: Android Studio's, where the workload
    /// looks too.</summary>
    public static string Usual(string home, string localData) =>
        OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Android", "sdk")
        : OperatingSystem.IsWindows() ? Path.Combine(localData, "Android", "Sdk")
        : Path.Combine(home, "Android", "Sdk");

    /// <summary>The SDK folder to look in and install into: the one the workload found, else the environment's, else the
    /// usual one.</summary>
    public static string Of(AndroidAnswer answer, Func<string, string?> environment, string usual) =>
        answer.Sdk is { } found ? found
        : environment("ANDROID_HOME") is { Length: > 0 } home ? home
        : environment("ANDROID_SDK_ROOT") is { Length: > 0 } root ? root
        : usual;

    /// <summary>Of a project's packages, those the SDK at <paramref name="sdk"/> lacks: a platform without its
    /// android.jar (under either folder name the SDK gives one), any other without its folder. The command-line tools,
    /// which no build needs, are left out unless <paramref name="all"/>.</summary>
    public static IReadOnlyList<string> Lacked(string sdk, IReadOnlyList<string> packages, Func<string, bool> exists, bool all = false) =>
        [.. packages.Where(p => all || !p.StartsWith("cmdline-tools", StringComparison.Ordinal)).Where(p => !Has(sdk, p, exists))];

    private static bool Has(string sdk, string package, Func<string, bool> exists)
    {
        string[] parts = package.Split(';');
        if (parts is ["platforms", { } platform])
        {
            string major = platform.Split('.')[0];
            return exists(Path.Combine(sdk, "platforms", platform, "android.jar")) || exists(Path.Combine(sdk, "platforms", major, "android.jar"));
        }
        return exists(Path.Combine([sdk, .. parts]));
    }
}
