using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>What a publish profile is written with for the Publish dialog's options: SelfContained said whenever a runtime
/// is chosen, since the SDK publishes a single file or a trimmed app self-contained where a profile leaves it out.</summary>
public class PublishOptionsTests
{
    private static readonly PublishOptions Project = new("Release", null, null, false, false, false, false, null);

    private static string? WhyNotPlatform(string runtimes, string? registry) => new ContainerImage("web", "latest", registry, runtimes).WhyNotPlatform();

    [Fact]
    public void A_container_publish_goes_only_where_its_platform_can_run() => Cases.All(
        ("a registry takes a container of Windows or Linux", () => Cases.Each<string>(
        [
            "win-x64",
            "linux-x64;win-x64",
        ], A_registry_takes_a_container_of_Windows_or_Linux)),
        ("an image for Windows goes into no engine here and the words say where it can go", An_image_for_Windows_goes_into_no_engine_here_and_the_words_say_where_it_can_go),
        ("a name that is no containers platform is refused wherever it goes", () => Cases.Each<(string, string?)>(
        [
            ("osx-arm64", "registry.example"),
            ("x64", "registry.example"),
        ], row => A_name_that_is_no_containers_platform_is_refused_wherever_it_goes(row.Item1, row.Item2))),
        ("a new container profile names the SDKs Container method and its image and no folder target", A_new_container_profile_names_the_SDKs_Container_method_and_its_image_and_no_folder_target),
        ("a container profile of two tags and platforms writes the sdks plural properties and restores both", A_container_profile_of_two_tags_and_platforms_writes_the_sdks_plural_properties_and_restores_both));

    /// <summary>The SDK pushes an image of either system with nothing of the engine.</summary>
    private void A_registry_takes_a_container_of_Windows_or_Linux(string runtimes)
    {
        Assert.Null(WhyNotPlatform(runtimes, "registry.example"));
    }

    private void An_image_for_Windows_goes_into_no_engine_here_and_the_words_say_where_it_can_go()
    {
        Assert.Equal("The engine runs Linux containers: an image for win-x64 can only be pushed to a registry", WhyNotPlatform("linux-x64;win-x64", null));
        Assert.Null(WhyNotPlatform("linux-x64;linux-arm64", null));
    }

    private void A_name_that_is_no_containers_platform_is_refused_wherever_it_goes(string runtime, string? registry)
    {
        Assert.Equal($"{runtime} is no platform of a container (linux-x64, linux-arm64, win-x64…)", WhyNotPlatform(runtime, registry));
    }

    [Fact]
    public void A_profile_says_the_deployment_chosen_for_it() => Cases.All(
        ("a new framework dependent single file profile says SelfContained false", A_new_framework_dependent_single_file_profile_says_SelfContained_false),
        ("a profile with a runtime says its deployment however it was before", () => Cases.Each<(bool, bool, string)>(
        [
            (false, true, "false"),
            (true, false, "true"),
        ], row => A_profile_with_a_runtime_says_its_deployment_however_it_was_before(row.Item1, row.Item2, row.Item3))),
        ("a portable profile leaves SelfContained to the project and a profiles own PublishSelfContained follows the choice", A_portable_profile_leaves_SelfContained_to_the_project_and_a_profiles_own_PublishSelfContained_follows_the_choice));

    private void A_new_framework_dependent_single_file_profile_says_SelfContained_false()
    {
        PublishOptions chosen = Project with { Runtime = "osx-arm64", SingleFile = true };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(chosen, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True(Value(written, "SelfContained") == "false",
            $"A framework-dependent single file's profile writes [{Shown(written)}], so the SDK publishes it self-contained, the runtime and all");
        Assert.Equal(("osx-arm64", "true"), (Value(written, "RuntimeIdentifier"), Value(written, "PublishSingleFile")));
    }

    private void A_profile_with_a_runtime_says_its_deployment_however_it_was_before(bool selfContained, bool wasSelfContained, string said)
    {
        PublishOptions before = Project with { Runtime = "linux-x64", SelfContained = wasSelfContained };
        Dictionary<string, string> raw = new() { ["RuntimeIdentifier"] = "linux-x64", ["PublishTrimmed"] = "false" };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(before with { SelfContained = selfContained }, before, raw, multiTargeted: false, fresh: false);
        Assert.True(Value(written, "SelfContained") == said, $"A profile {(selfContained ? "self-contained" : "framework-dependent")} for linux-x64 writes [{Shown(written)}]");
    }

    private void A_portable_profile_leaves_SelfContained_to_the_project_and_a_profiles_own_PublishSelfContained_follows_the_choice()
    {
        IReadOnlyList<(string Name, string? Value)> portable = PublishProfiles.Properties(Project, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True(portable.All(p => p.Name != "SelfContained"), $"A new portable profile writes [{Shown(portable)}]");

        PublishOptions before = Project with { Runtime = "win-x64", SelfContained = true, SingleFile = true };
        Dictionary<string, string> raw = new() { ["RuntimeIdentifier"] = "win-x64", ["PublishSingleFile"] = "true", ["PublishSelfContained"] = "true" };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(before with { SelfContained = false }, before, raw, multiTargeted: false, fresh: false);
        Assert.True((Value(written, "SelfContained"), Value(written, "PublishSelfContained")) == ("false", "false"),
            $"A profile setting PublishSelfContained made framework-dependent writes [{Shown(written)}], which the publish still makes self-contained");
    }

    private void A_new_container_profile_names_the_SDKs_Container_method_and_its_image_and_no_folder_target()
    {
        PublishOptions image = Project with { Image = new ContainerImage("web", "t1", null) };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(image, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True((Value(written, "WebPublishMethod"), Value(written, "ContainerRepository"), Value(written, "ContainerImageTag")) == ("Container", "web", "t1")
            && written.All(p => p.Name is not ("ContainerRegistry" or "PublishProtocol" or Passenger.Core.MSBuildNames.PublishTargetId)),
            $"A new container profile for web:t1 in the engine writes [{Shown(written)}], which the SDK would not publish as that image there");
    }

    private void A_container_profile_of_two_tags_and_platforms_writes_the_sdks_plural_properties_and_restores_both()
    {
        PublishOptions image = Project with { Image = new ContainerImage("web", "t1;t2", null, "linux-x64;linux-arm64") };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(image, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True((Value(written, "ContainerImageTags"), Value(written, "ContainerRuntimeIdentifiers"), Value(written, "RuntimeIdentifiers"))
            == ("t1;t2", "linux-x64;linux-arm64", "linux-x64;linux-arm64") && written.All(p => p.Name is not ("ContainerImageTag" or "ContainerRuntimeIdentifier")),
            $"A container profile of two tags and platforms writes [{Shown(written)}], which the SDK would not publish or restore as both");

        Dictionary<string, string> raw = new() { ["WebPublishMethod"] = "Container", ["ContainerImageTags"] = "t1;t2", ["ContainerRuntimeIdentifiers"] = "linux-x64;linux-arm64",
            ["RuntimeIdentifiers"] = "linux-x64;linux-arm64" };
        IReadOnlyList<(string Name, string? Value)> one = PublishProfiles.Properties(image with { Image = new ContainerImage("web", "t1", null, "linux-arm64") }, image, raw,
            multiTargeted: false, fresh: false);
        Assert.True((Value(one, "ContainerImageTag"), Value(one, "ContainerRuntimeIdentifier")) == ("t1", "linux-arm64")
            && one.Any(p => p.Name == "ContainerImageTags" && p.Value is null) && one.Any(p => p.Name == "ContainerRuntimeIdentifiers" && p.Value is null)
            && one.Any(p => p.Name == "RuntimeIdentifiers" && p.Value is null),
            $"Made one tag and platform again, the profile writes [{Shown(one)}], leaving the several in place");
    }

    private static string? Value(IReadOnlyList<(string Name, string? Value)> written, string name) => written.LastOrDefault(p => p.Name == name).Value;

    private static string Shown(IReadOnlyList<(string Name, string? Value)> written) => string.Join(", ", written.Select(p => $"{p.Name}={p.Value ?? "(removed)"}"));
}
