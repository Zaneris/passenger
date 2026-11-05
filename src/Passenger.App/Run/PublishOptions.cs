using Passenger.Core;
using Passenger.Host;

namespace Passenger.App.Run;

/// <summary>What a publish does, as the Publish dialog shows it and a profile keeps it: the configuration, the target
/// framework (a multi-targeted project's), the runtime (none for a portable app), framework-dependent or self-contained, the
/// SDK's single-file, ReadyToRun and trimming, the output folder as the profile names it (null while it is MSBuild's own
/// default, which the profile then leaves to MSBuild), and a container profile's image (null for a folder's).</summary>
public sealed record PublishOptions(
    string Configuration,
    string? Framework,
    string? Runtime,
    bool SelfContained,
    bool SingleFile,
    bool ReadyToRun,
    bool Trimmed,
    string? Folder,
    ContainerImage? Image = null);

/// <summary>A container profile's image as its profile names it: its repository and tag, several tags between semicolons
/// (empty where it leaves them to the SDK: the assembly's name, latest), the registry it is pushed to (null for the engine
/// on this machine), and the runtime it is made for, several for an image of more than one platform (empty: the SDK's own).</summary>
public sealed record ContainerImage(string Repository, string Tag, string? Registry, string Runtimes = "");

/// <summary>What a profile's evaluation and the Publish dialog's options make of each other, from their values alone.</summary>
public static partial class PublishProfiles
{
    /// <summary>The method a profile that publishes the project's container image names (the SDK's DefaultContainer's).</summary>
    public const string ContainerMethod = "Container";

    /// <summary>What a profile's evaluation makes of the dialog's options: its configuration (one it sets itself, else Release,
    /// as dotnet publish takes), framework, runtime, deployment and the SDK's options; the folder is what the file names.</summary>
    public static PublishOptions Options(IReadOnlyDictionary<string, string> evaluated, IReadOnlyDictionary<string, string> raw, string? framework)
    {
        string configuration = raw.ContainsKey(MSBuildNames.Configuration) ? evaluated.GetValueOrDefault(MSBuildNames.Configuration) ?? "Release"
            : evaluated.GetValueOrDefault(MSBuildNames.LastUsedBuildConfiguration) ?? "Release";
        string? runtime = evaluated.GetValueOrDefault(MSBuildNames.RuntimeIdentifier);
        bool selfContained = True(evaluated, MSBuildNames.SelfContained) && runtime is not null;
        string? folder = raw.GetValueOrDefault(MSBuildNames.PublishDir);
        ContainerImage? image = string.Equals(raw.GetValueOrDefault(MSBuildNames.WebPublishMethod) ?? raw.GetValueOrDefault(MSBuildNames.PublishProtocol), ContainerMethod,
            StringComparison.OrdinalIgnoreCase)
            ? new ContainerImage(evaluated.GetValueOrDefault(CarriedProperties.ContainerRepository) ?? string.Empty,
                Given(evaluated.GetValueOrDefault(MSBuildNames.ContainerImageTags)) ?? evaluated.GetValueOrDefault(MSBuildNames.ContainerImageTag) ?? string.Empty,
                evaluated.GetValueOrDefault(MSBuildNames.ContainerRegistry) is { Length: > 0 } registry ? registry : null,
                Given(evaluated.GetValueOrDefault(MSBuildNames.ContainerRuntimeIdentifiers)) ?? evaluated.GetValueOrDefault(MSBuildNames.ContainerRuntimeIdentifier) ?? string.Empty)
            : null;
        return new PublishOptions(configuration, framework, runtime, selfContained, True(evaluated, MSBuildNames.PublishSingleFile) && runtime is not null,
            True(evaluated, MSBuildNames.PublishReadyToRun) && runtime is not null, True(evaluated, MSBuildNames.PublishTrimmed) && selfContained, image is null ? folder : null, image);
    }

    private static string? Given(string? value) => value?.Trim() is { Length: > 0 } given ? given : null;

    private static bool True(IReadOnlyDictionary<string, string> values, string name) =>
        string.Equals(values.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a profile is changed by for the options: its configuration always (the publish gives it), SelfContained
    /// whenever a runtime is chosen (left out, the SDK publishes a single file or a trimmed app self-contained), and each other
    /// property the dialog shows (an image's name, tag and registry too) where the file sets it already or the option differs
    /// from what the profile (or, for a new one, the project) made of it before. A multi-targeted project's framework is named;
    /// a single one's is never pinned in a profile. A new profile names its folder target, or the SDK's Container method.</summary>
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
        if (options.Image is { } image)
        {
            Changed(CarriedProperties.ContainerRepository, Given(image.Repository), Given(before?.Image?.Repository));
            Either(MSBuildNames.ContainerImageTag, MSBuildNames.ContainerImageTags, Given(image.Tag), Given(before?.Image?.Tag));
            Changed(MSBuildNames.ContainerRegistry, Given(image.Registry), Given(before?.Image?.Registry));
            Either(MSBuildNames.ContainerRuntimeIdentifier, MSBuildNames.ContainerRuntimeIdentifiers, Given(image.Runtimes), Given(before?.Image?.Runtimes));
            // An image of several platforms is restored for each: the restore reads RuntimeIdentifiers, never the container's.
            Changed(MSBuildNames.RuntimeIdentifiers, Several(image.Runtimes), Several(before?.Image?.Runtimes));
        }
        if (fresh && options.Image is not null)
        {
            set.Add((MSBuildNames.WebPublishMethod, ContainerMethod));
        }
        else if (fresh)
        {
            set.Add((MSBuildNames.PublishProtocol, "FileSystem"));
            set.Add((MSBuildNames.PublishTargetId, "Folder"));
        }
        return set;

        static string? Several(string? value) => Given(value) is { } given && given.Contains(';', StringComparison.Ordinal) ? given : null;

        // One value in the property of one, several between semicolons in the property of several, the other taken out.
        void Either(string one, string several, string? value, string? was)
        {
            bool many = value?.Contains(';', StringComparison.Ordinal) == true;
            bool wasMany = was?.Contains(';', StringComparison.Ordinal) == true;
            Changed(one, many ? null : value, wasMany ? null : was);
            Changed(several, many ? value : null, wasMany ? was : null);
        }

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
