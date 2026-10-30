using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>What a publish profile is written with for the Publish dialog's options: SelfContained said whenever a runtime
/// is chosen, since the SDK publishes a single file or a trimmed app self-contained where a profile leaves it out.</summary>
public class PublishOptionsTests
{
    private static readonly PublishOptions Project = new("Release", null, null, false, false, false, false, null);

    [Fact]
    public void A_new_framework_dependent_single_file_profile_says_SelfContained_false()
    {
        PublishOptions chosen = Project with { Runtime = "osx-arm64", SingleFile = true };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(chosen, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True(Value(written, "SelfContained") == "false",
            $"A framework-dependent single file's profile writes [{Shown(written)}], so the SDK publishes it self-contained, the runtime and all");
        Assert.Equal(("osx-arm64", "true"), (Value(written, "RuntimeIdentifier"), Value(written, "PublishSingleFile")));
    }

    [Theory]
    [InlineData(false, false, "false")]
    [InlineData(false, true, "false")]
    [InlineData(true, false, "true")]
    [InlineData(true, true, "true")]
    public void A_profile_with_a_runtime_says_its_deployment_however_it_was_before(bool selfContained, bool wasSelfContained, string said)
    {
        PublishOptions before = Project with { Runtime = "linux-x64", SelfContained = wasSelfContained };
        Dictionary<string, string> raw = new() { ["RuntimeIdentifier"] = "linux-x64", ["PublishTrimmed"] = "false" };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(before with { SelfContained = selfContained }, before, raw, multiTargeted: false, fresh: false);
        Assert.True(Value(written, "SelfContained") == said, $"A profile {(selfContained ? "self-contained" : "framework-dependent")} for linux-x64 writes [{Shown(written)}]");
    }

    [Fact]
    public void A_portable_profile_leaves_SelfContained_to_the_project_and_a_profiles_own_PublishSelfContained_follows_the_choice()
    {
        IReadOnlyList<(string Name, string? Value)> portable = PublishProfiles.Properties(Project, Project, new Dictionary<string, string>(), multiTargeted: false, fresh: true);
        Assert.True(portable.All(p => p.Name != "SelfContained"), $"A new portable profile writes [{Shown(portable)}]");

        PublishOptions before = Project with { Runtime = "win-x64", SelfContained = true, SingleFile = true };
        Dictionary<string, string> raw = new() { ["RuntimeIdentifier"] = "win-x64", ["PublishSingleFile"] = "true", ["PublishSelfContained"] = "true" };
        IReadOnlyList<(string Name, string? Value)> written = PublishProfiles.Properties(before with { SelfContained = false }, before, raw, multiTargeted: false, fresh: false);
        Assert.True((Value(written, "SelfContained"), Value(written, "PublishSelfContained")) == ("false", "false"),
            $"A profile setting PublishSelfContained made framework-dependent writes [{Shown(written)}], which the publish still makes self-contained");
    }

    private static string? Value(IReadOnlyList<(string Name, string? Value)> written, string name) => written.LastOrDefault(p => p.Name == name).Value;

    private static string Shown(IReadOnlyList<(string Name, string? Value)> written) => string.Join(", ", written.Select(p => $"{p.Name}={p.Value ?? "(removed)"}"));
}
