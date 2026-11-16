using Passenger.App.Run;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a folder offers to build as and what each build is given for the one chosen: a folder with no solution
/// offers each configuration and platform its projects declare (Godot's game Debug, ExportDebug and ExportRelease, never
/// Release), the one MSBuild takes given none being the default, which nothing is given for; under a solution each project is
/// given what the solution maps it to, and the solution's build the solution configuration itself.</summary>
public class BuildConfigurationsTests
{
    private const string Game = "/work/Game/Game.csproj";
    private const string Library = "/work/Library/Library.csproj";
    private const string Solution = "/work/All.sln";

    [Fact]
    public void Projects_declaring_nothing_offer_debug_and_release_for_anycpu_debug_by_default()
    {
        IReadOnlyList<BuildConfiguration> offered = BuildConfigurations.Declared([(null, null)]);
        Assert.Equal(["Debug|AnyCPU", "Release|AnyCPU"], offered.Select(o => o.Key));
        Assert.Equal("Debug|AnyCPU", BuildConfigurations.DefaultOf(offered)?.Key);
        Assert.Equal(["Debug", "Release"], offered.Select(o => o.Shown(offered)));
    }

    [Fact]
    public void A_godot_game_offers_its_sdks_three_configurations_and_never_release()
    {
        IReadOnlyList<BuildConfiguration> offered = BuildConfigurations.Declared([("Debug;ExportDebug;ExportRelease", "AnyCPU")]);
        Assert.Equal(["Debug", "ExportDebug", "ExportRelease"], offered.Select(o => o.Configuration));
        Assert.DoesNotContain(offered, o => o.Configuration == "Release");
        Assert.Equal("Debug|AnyCPU", BuildConfigurations.DefaultOf(offered)?.Key);
    }

    [Fact]
    public void Projects_offer_every_configuration_and_platform_any_declares_once_shown_with_its_platform_where_several_are()
    {
        IReadOnlyList<BuildConfiguration> offered = BuildConfigurations.Declared([("Debug;Release", "AnyCPU"), ("Debug;Staging", "x64;ARM64")]);
        Assert.Equal(["Debug|AnyCPU", "Debug|x64", "Debug|ARM64", "Release|AnyCPU", "Release|x64", "Release|ARM64", "Staging|AnyCPU", "Staging|x64", "Staging|ARM64"],
            offered.Select(o => o.Key));
        Assert.Equal("Staging | x64", offered[7].Shown(offered));
    }

    [Fact]
    public void A_choice_kept_stands_while_offered_and_falls_back_to_the_default_once_gone()
    {
        IReadOnlyList<BuildConfiguration> offered = BuildConfigurations.Declared([("Debug;Release", null)]);
        BuildConfiguration? fallback = BuildConfigurations.DefaultOf(offered);
        Assert.Equal("Release|AnyCPU", BuildConfigurations.Chosen(offered, fallback, "release|anycpu")?.Key);
        Assert.Equal("Debug|AnyCPU", BuildConfigurations.Chosen(offered, fallback, "Staging|AnyCPU")?.Key);
        Assert.Equal("Debug|AnyCPU", BuildConfigurations.Chosen(offered, fallback, "not a key")?.Key);
    }

    [Fact]
    public void With_no_solution_a_project_is_given_the_configuration_and_only_a_platform_it_declares()
    {
        BuildChoice choice = new(new BuildConfiguration("Release", "x64"), OfSolution: false);
        Assert.Equal(new Dictionary<string, string> { ["Configuration"] = "Release", ["Platform"] = "x64" }, choice.PropertiesOf(Game, "AnyCPU;x64"));
        Assert.Equal(new Dictionary<string, string> { ["Configuration"] = "Release" }, choice.PropertiesOf(Library, "AnyCPU"));
        Assert.Equal(["-c", "Release", "-p:Platform=x64"], choice.ArgumentsFor(Game, solution: false, "x64"));
        Assert.Equal(["-c", "Release"], choice.ArgumentsFor(Library, solution: false, null));
        Assert.Equal(["-c", "Release"], new BuildChoice(new BuildConfiguration("Release", "AnyCPU"), OfSolution: false).ArgumentsFor(Game, solution: false, "AnyCPU"));
    }

    [Fact]
    public void Under_a_solution_each_project_is_given_what_its_configuration_maps_it_to_and_the_solution_the_configuration_itself()
    {
        BuildChoice choice = new(new BuildConfiguration("Staging", "Any CPU"), OfSolution: true)
        {
            Mapped = [new SolutionBuilt(Game, "Release", "x64"), new SolutionBuilt(Library, "Staging", "AnyCPU")],
        };
        Assert.Equal(["-c", "Staging", "-p:Platform=Any CPU"], choice.ArgumentsFor(Solution, solution: true, null));
        Assert.Equal(["-c", "Release", "-p:Platform=x64"], choice.ArgumentsFor(Game, solution: false, null));
        Assert.Equal(new Dictionary<string, string> { ["Configuration"] = "Staging", ["Platform"] = "AnyCPU" }, choice.PropertiesOf(Library, null));
        Assert.Equal(new Dictionary<string, string> { ["Configuration"] = "Staging" }, choice.PropertiesOf("/work/Tool/Tool.csproj", "x64"));
    }

    [Fact]
    public void A_workspace_gives_nothing_for_the_default_and_its_choice_to_each_build_target()
    {
        ProjectModel game = Project(Game, "AnyCPU;x64");
        WorkspaceModel plain = new("/work", null, [game]);
        Assert.Empty(plain.BuildArgumentsFor(Game));
        Assert.Empty(plain.BuildPropertiesOf(Game));
        WorkspaceModel chosen = plain with { Build = new BuildChoice(new BuildConfiguration("Release", "x64"), OfSolution: false) };
        Assert.Equal(["-c", "Release", "-p:Platform=x64"], chosen.BuildArgumentsFor(Game));
        Assert.Empty(chosen.BuildArgumentsFor("/work"));
        Assert.Equal("Release", chosen.BuildPropertiesOf(Game)["Configuration"]);
    }

    /// <summary>What Hot Reload checks a program's build by: the configuration and platform each workspace builds a project
    /// and those it references as, the SDK's Debug and AnyCPU where given none, a solution's Any CPU being the project's
    /// AnyCPU, and the first of them two workspaces build unlike.</summary>
    [Fact]
    public void Two_workspaces_build_a_program_unlike_where_its_project_or_a_reference_takes_another_configuration_or_platform()
    {
        WorkspaceModel plain = new("/work", null, [Project(Game, "AnyCPU;x64") with { ProjectReferences = [Library] }, Project(Library, null)]);
        Assert.Equal("Debug|AnyCPU", plain.BuiltAs(Game).Key);
        Assert.Null(plain.BuiltUnlike(plain with { }, Game));

        WorkspaceModel x64 = plain with { Build = new BuildChoice(new BuildConfiguration("Debug", "x64"), OfSolution: false) };
        Assert.Equal((Game, "Debug|x64", "Debug|AnyCPU"), x64.BuiltUnlike(plain, Game) is { } game ? (game.Project, game.Here.Key, game.There.Key) : default);
        Assert.Null(x64.BuiltUnlike(plain, Library));

        WorkspaceModel release = plain with { Build = new BuildChoice(new BuildConfiguration("Release", "AnyCPU"), OfSolution: false) };
        Assert.Equal((Game, "Release|AnyCPU", "Debug|AnyCPU"), release.BuiltUnlike(plain, Game) is { } built ? (built.Project, built.Here.Key, built.There.Key) : default);

        BuildChoice staging = new(new BuildConfiguration("Staging", "Any CPU"), OfSolution: true)
        {
            Mapped = [new SolutionBuilt(Game, "Debug", "Any CPU"), new SolutionBuilt(Library, "Release", "AnyCPU")],
        };
        WorkspaceModel mapped = plain with { Build = staging };
        Assert.Equal("Debug|AnyCPU", mapped.BuiltAs(Game).Key);
        Assert.Equal(Library, mapped.BuiltUnlike(plain, Game)?.Project);
    }

    /// <summary>A solution's default gives builds nothing only where it maps every project as a build of it given nothing
    /// builds it: Debug and AnyCPU, a solution's Any CPU being AnyCPU.</summary>
    [Fact]
    public void A_solution_configuration_changes_nothing_given_none_only_where_it_maps_every_project_to_debug_and_anycpu()
    {
        Assert.True(BuildConfigurations.MapsAsGivenNone([new SolutionBuilt(Game, "Debug", "AnyCPU"), new SolutionBuilt(Library, "debug", "Any CPU")]));
        Assert.True(BuildConfigurations.MapsAsGivenNone([]));
        Assert.False(BuildConfigurations.MapsAsGivenNone([new SolutionBuilt(Game, "Release", "AnyCPU"), new SolutionBuilt(Library, "Debug", "AnyCPU")]));
        Assert.False(BuildConfigurations.MapsAsGivenNone([new SolutionBuilt(Game, "Debug", "x64")]));
    }

    /// <summary>A project's build is told what the solution maps the projects it reaches through its references to, where a
    /// project referencing one builds as another (a reference takes its referrer's configuration unless told): the app as
    /// Staging tells its library mapped to Release, and the library below it mapped to Staging, which would otherwise take
    /// Release from the library; the library's own build tells that one alone, and built beside it the one below is told
    /// the same, so the library's build of it is the same build; a mapping alike everywhere tells nothing.</summary>
    [Fact]
    public void A_projects_build_is_told_the_mapping_of_each_reference_that_would_take_another_from_the_project_referencing_it()
    {
        const string app = "/work/App/App.csproj";
        const string shared = "/work/Shared/Shared.csproj";
        BuildChoice staging = new(new BuildConfiguration("Staging", "Any CPU"), OfSolution: true)
        {
            Mapped = [new SolutionBuilt(app, "Staging", "AnyCPU"), new SolutionBuilt(Library, "Release", "AnyCPU"), new SolutionBuilt(shared, "Staging", "AnyCPU")],
        };
        WorkspaceModel workspace = new WorkspaceModel("/work", null,
            [Project(app, null) with { ProjectReferences = [Library] }, Project(Library, null) with { ProjectReferences = [shared] }, Project(shared, null)]) with { Build = staging };
        Assert.Equal([Library, shared], staging.ToldOf(app, null, workspace.ReferencesOf).Select(t => t.Project));
        Assert.Equal(["-c", "Staging", "-p:Platform=AnyCPU", "-p:CurrentSolutionConfigurationContents=<SolutionConfiguration>"
            + "<ProjectConfiguration Project='1' AbsolutePath='/work/Library/Library.csproj'>Release|AnyCPU</ProjectConfiguration>"
            + "<ProjectConfiguration Project='2' AbsolutePath='/work/Shared/Shared.csproj'>Staging|AnyCPU</ProjectConfiguration></SolutionConfiguration>"],
            workspace.BuildArgumentsFor(app));
        Assert.StartsWith("--property:CurrentSolutionConfigurationContents=<SolutionConfiguration>", workspace.BuildArgumentsFor(app, watched: true)[^1], StringComparison.Ordinal);
        Assert.Equal([shared], staging.ToldOf(Library, null, workspace.ReferencesOf).Select(t => t.Project));
        Assert.Empty(staging.ToldOf(shared, null, workspace.ReferencesOf));
        Assert.Equal(BuildChoice.Contents(staging.ToldOf(app, null, workspace.ReferencesOf)), workspace.BuiltWith(app)["CurrentSolutionConfigurationContents"]);
        Assert.Equal(new Dictionary<string, string> { ["Configuration"] = "Staging", ["Platform"] = "AnyCPU" }, workspace.BuiltWith(shared));
        Func<string, IReadOnlyDictionary<string, string>> together = workspace.BuiltTogetherWith([Library, shared]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Configuration"] = "Staging", ["Platform"] = "AnyCPU", ["CurrentSolutionConfigurationContents"] = BuildChoice.Contents([staging.Mapped[2]]),
        }, together(shared));
        Assert.Equal(together(shared)["CurrentSolutionConfigurationContents"], together(Library)["CurrentSolutionConfigurationContents"]);

        BuildChoice release = new(new BuildConfiguration("Release", "Any CPU"), OfSolution: true)
        {
            Mapped = [new SolutionBuilt(app, "Release", "AnyCPU"), new SolutionBuilt(Library, "release", "Any CPU"), new SolutionBuilt(shared, "Release", "AnyCPU")],
        };
        Assert.Equal(["-c", "Release", "-p:Platform=AnyCPU"], (workspace with { Build = release }).BuildArgumentsFor(app));
        Assert.Empty((workspace with { Build = null }).BuiltWith(app));
    }

    /// <summary>What a build is told of its references reads as MSBuild reads it whatever their paths hold: the path and the
    /// mapping written as XML, then given as any property is, the command line's separators and MSBuild's escape mark
    /// escaped.</summary>
    [Fact]
    public void What_a_build_is_told_of_its_references_keeps_a_path_of_quotes_ampersands_and_msbuilds_separators()
    {
        string contents = BuildChoice.Contents([new SolutionBuilt("/w/it's & <x>, 100%; \"q\"/L.csproj", "Release", "AnyCPU")]);
        Assert.Equal("<SolutionConfiguration><ProjectConfiguration Project='1' AbsolutePath='/w/it&apos;s &amp; &lt;x>, 100%; \"q\"/L.csproj'>Release|AnyCPU"
            + "</ProjectConfiguration></SolutionConfiguration>", contents);
        Assert.Equal("a%25b%3Bc%2Cd%22e", PropertySwitch.Escaped("a%b;c,d\"e"));
    }

    /// <summary>dotnet watch runs a project as its build does, its configuration and platform given in its own words: it takes
    /// <c>-p</c> for its project, so the platform goes as <c>--property</c>; none for the default.</summary>
    [Fact]
    public void Dotnet_watch_is_given_the_configuration_and_platform_with_the_platform_as_a_property_its_p_being_its_project()
    {
        WorkspaceModel plain = new("/work", null, [Project(Game, "AnyCPU;x64")]);
        Assert.Empty(plain.BuildArgumentsFor(Game, watched: true));
        WorkspaceModel chosen = plain with { Build = new BuildChoice(new BuildConfiguration("Debug", "x64"), OfSolution: false) };
        Assert.Equal(["-c", "Debug", "--property:Platform=x64"], chosen.BuildArgumentsFor(Game, watched: true));
        Assert.Equal(["-c", "Debug", "-p:Platform=x64"], chosen.BuildArgumentsFor(Game));
        BuildChoice solution = new(new BuildConfiguration("Staging", "Any CPU"), OfSolution: true) { Mapped = [new SolutionBuilt(Game, "Release", "x64")] };
        Assert.Equal(["-c", "Release", "--property:Platform=x64"], solution.ArgumentsFor(Game, solution: false, null, watched: true));
        Assert.Equal(["-c", "Staging", "--property:Platform=Any CPU"], solution.ArgumentsFor(Solution, solution: true, null, watched: true));
    }

    /// <summary>A publish that builds as the folder builds (a container run's image) takes the choice's arguments, else the
    /// configuration the project's build takes given none, which a publish given none would leave for Release.</summary>
    [Fact]
    public void A_publish_building_as_the_folder_builds_takes_the_choice_else_the_configuration_its_build_takes()
    {
        WorkspaceModel plain = new("/work", null, [Project(Game, "AnyCPU;x64")]);
        Assert.Equal(["-c", "Debug"], plain.PublishArgumentsFor(Game, "Debug"));
        Assert.Empty(plain.PublishArgumentsFor(Game, null));
        WorkspaceModel chosen = plain with { Build = new BuildChoice(new BuildConfiguration("Release", "x64"), OfSolution: false) };
        Assert.Equal(["-c", "Release", "-p:Platform=x64"], chosen.PublishArgumentsFor(Game, "Release"));
    }

    [Fact]
    public void A_build_takes_the_configurations_arguments_before_its_logger_switches()
    {
        IReadOnlyList<string> arguments = BuildOutput.Arguments(Game, BuildKind.Build, "net10.0", configured: ["-c", "Release"]);
        Assert.Equal(["build", Game, "--framework", "net10.0", "-c", "Release"], arguments.Take(6));
        Assert.Equal(BuildOutput.Arguments(Game, BuildKind.Build, "net10.0"), BuildOutput.Arguments(Game, BuildKind.Build, "net10.0", configured: []));
    }

    [Theory]
    [InlineData("Debug;Release", "Release")]
    [InlineData("Debug;ExportDebug;ExportRelease", "ExportRelease")]
    [InlineData("Debug;Staging", "Debug")]
    public void A_publish_takes_release_else_the_configuration_named_for_release(string declared, string taken) =>
        Assert.Equal(taken, PublishProfiles.ReleaseOf(PublishProfiles.DeclaredConfigurations(Project(Game, null, declared))));

    [Theory]
    [InlineData("Release|Any CPU", "Release", "Any CPU")]
    [InlineData(" Staging | x64 ", "Staging", "x64")]
    public void A_kept_key_reads_back_as_its_configuration_and_platform(string key, string configuration, string platform) =>
        Assert.Equal(new BuildConfiguration(configuration, platform), BuildConfiguration.Of(key));

    [Theory]
    [InlineData(null)]
    [InlineData("Release")]
    [InlineData("|x64")]
    [InlineData("a|b|c")]
    public void Text_naming_no_configuration_and_platform_reads_as_none(string? key) => Assert.Null(BuildConfiguration.Of(key));

    private static ProjectModel Project(string path, string? platforms, string configurations = "Debug;Release") =>
        ProjectModel.From(path, new EvaluatedProject
        {
            Path = path,
            Properties = new Dictionary<string, EvaluatedProperty>
            {
                [CarriedProperties.Configurations] = new(configurations, false),
                [CarriedProperties.Platforms] = new(platforms ?? "AnyCPU", false),
            },
        });
}
