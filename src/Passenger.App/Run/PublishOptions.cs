using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>What a folder publish does, as the Publish dialog shows it and a profile keeps it: the configuration, the target
/// framework (a multi-targeted project's), the runtime (none for a portable app), framework-dependent or self-contained, the
/// SDK's single-file, ReadyToRun and trimming, and the output folder as the profile names it (null while it is MSBuild's
/// own default, which the profile then leaves to MSBuild).</summary>
public sealed record PublishOptions(
    string Configuration,
    string? Framework,
    string? Runtime,
    bool SelfContained,
    bool SingleFile,
    bool ReadyToRun,
    bool Trimmed,
    string? Folder);

/// <summary>What a profile's evaluation and the Publish dialog's options make of each other, from their values alone.</summary>
public static partial class PublishProfiles
{
    /// <summary>What a profile's evaluation makes of the dialog's options: its configuration (one it sets itself, else Release,
    /// as dotnet publish takes), framework, runtime, deployment and the SDK's options; the folder is what the file names.</summary>
    public static PublishOptions Options(IReadOnlyDictionary<string, string> evaluated, IReadOnlyDictionary<string, string> raw, string? framework)
    {
        string configuration = raw.ContainsKey(MSBuildNames.Configuration) ? evaluated.GetValueOrDefault(MSBuildNames.Configuration) ?? "Release"
            : evaluated.GetValueOrDefault(MSBuildNames.LastUsedBuildConfiguration) ?? "Release";
        string? runtime = evaluated.GetValueOrDefault(MSBuildNames.RuntimeIdentifier);
        bool selfContained = True(evaluated, MSBuildNames.SelfContained) && runtime is not null;
        string? folder = raw.GetValueOrDefault(MSBuildNames.PublishDir);
        return new PublishOptions(configuration, framework, runtime, selfContained, True(evaluated, MSBuildNames.PublishSingleFile) && runtime is not null,
            True(evaluated, MSBuildNames.PublishReadyToRun) && runtime is not null, True(evaluated, MSBuildNames.PublishTrimmed) && selfContained, folder);
    }

    private static bool True(IReadOnlyDictionary<string, string> values, string name) =>
        string.Equals(values.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a profile is changed by for the options: its configuration always (the publish gives it), SelfContained
    /// whenever a runtime is chosen (left out, the SDK publishes a single file or a trimmed app self-contained), and each other
    /// property the dialog shows where the file sets it already or the option differs from what the profile (or, for a new
    /// one, the project) made of it before. A multi-targeted project's framework is named; a single one's is the project's
    /// own, never pinned in a profile that would outlive a change of it. A new profile names its folder target.</summary>
    public static IReadOnlyList<(string Name, string? Value)> Properties(PublishOptions options, PublishOptions? before, IReadOnlyDictionary<string, string> raw,
        bool multiTargeted, bool fresh)
    {
        List<(string, string?)> set = [(MSBuildNames.Configuration, options.Configuration)];
        if (raw.ContainsKey(MSBuildNames.LastUsedBuildConfiguration))
        {
            set.Add((MSBuildNames.LastUsedBuildConfiguration, options.Configuration));
        }
        if (multiTargeted)
        {
            Changed(MSBuildNames.TargetFramework, options.Framework, before?.Framework);
        }
        bool runtime = options.Runtime is not null;
        Changed(MSBuildNames.RuntimeIdentifier, options.Runtime, before?.Runtime);
        string selfContained = Word(options.SelfContained && runtime);
        if (runtime)
        {
            set.Add((MSBuildNames.SelfContained, selfContained));
        }
        else
        {
            Changed(MSBuildNames.SelfContained, selfContained, Word(before?.SelfContained == true));
        }
        if (raw.ContainsKey(MSBuildNames.PublishSelfContained))
        {
            set.Add((MSBuildNames.PublishSelfContained, selfContained));
        }
        Changed(MSBuildNames.PublishSingleFile, Word(options.SingleFile && runtime), Word(before?.SingleFile == true));
        Changed(MSBuildNames.PublishReadyToRun, Word(options.ReadyToRun && runtime), Word(before?.ReadyToRun == true));
        Changed(MSBuildNames.PublishTrimmed, Word(options.Trimmed && options.SelfContained && runtime), Word(before?.Trimmed == true));
        if (options.Folder is { Length: > 0 } folder)
        {
            Changed(MSBuildNames.PublishDir, folder, before?.Folder);
        }
        if (fresh)
        {
            set.Add((MSBuildNames.PublishProtocol, "FileSystem"));
            set.Add((MSBuildNames.PublishTargetId, "Folder"));
        }
        return set;

        void Changed(string name, string? value, string? was)
        {
            if (raw.ContainsKey(name) || !string.Equals(value, was, StringComparison.OrdinalIgnoreCase))
            {
                // None where the project sets one (its own RuntimeIdentifier) is said as empty, which takes it away.
                set.Add((name, value ?? (raw.ContainsKey(name) ? null : string.Empty)));
            }
        }

        static string Word(bool value) => value ? "true" : "false";
    }
}
