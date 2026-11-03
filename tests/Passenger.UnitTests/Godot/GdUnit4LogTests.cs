using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>Why a game's GdUnit4 tests that need Godot's engine never ran, read from what GdUnit4's adapter says as it
/// builds its test runner into the game: the compiler's errors it says among its informational words, and the failed
/// build that ends its engine's run.</summary>
public class GdUnit4LogTests
{
    private static readonly string Generated = Path.GetFullPath("/game/.godot/mono/temp/obj/Debug/Godot.SourceGenerators/Godot.SourceGenerators.TrampolineCollectorDispatchGenerator/GdUnit4.TestRunner.GdUnit4TestRunnerScene_TrampolineCollectorDispatch.generated.cs");

    private static readonly string Project = Path.GetFullPath("/game/Spinner.csproj");

    private static string OlderBase => $"[GdUnit4] [Spinner] dotnet/out: {Generated}(23,73): error CS0117: 'GdUnit4TestRunnerSceneCore' does not contain a definition for 'GodotInternal' [{Project}]";

    [Fact]
    public void A_failed_build_of_the_runner_is_said_once_with_the_compilers_errors_the_adapter_said_only_as_information()
    {
        GdUnit4Log log = new();
        Assert.Null(log.Heard("[GdUnit4] [Spinner] Running dotnet build to ensure dependencies are available..."));
        Assert.Null(log.Heard($"[GdUnit4] [Spinner] dotnet/out: {Project} : warning NU1603: something approximate"));
        Assert.Null(log.Heard($"[GdUnit4] [Spinner] dotnet/out: {Path.GetFullPath("/game/Main.cs")}(4,9): error CS1002: ; expected [{Project}]"));
        // MSBuild says each error again under Build FAILED.
        Assert.Null(log.Heard($"[GdUnit4] [Spinner] dotnet/out: {Path.GetFullPath("/game/Main.cs")}(4,9): error CS1002: ; expected [{Project}]"));
        Assert.Null(log.Why("Spinner"));

        string? said = log.Heard("[GdUnit4] [Spinner] dotnet build failed with exit code: 1");
        Assert.Equal("GdUnit4's dotnet build of its test runner into Spinner failed, so Godot ran none of its tests that need the engine: Main.cs(4,9): error CS1002: ; expected.", said);
        Assert.Equal(said, log.Why("Spinner"));
        Assert.Null(log.Heard("[GdUnit4] [Spinner] dotnet build failed with exit code: 1"));
        Assert.Null(log.Why("Other"));
    }

    [Fact]
    public void A_runner_whose_base_an_older_godot_built_says_this_godot_cannot_build_it()
    {
        GdUnit4Log log = new();
        Assert.Null(log.Heard(OlderBase));
        string said = log.Heard("[GdUnit4] [Spinner] dotnet build failed with exit code: 1")!;
        Assert.StartsWith("GdUnit4's dotnet build of its test runner into Spinner failed, so Godot ran none of its tests that need the engine: "
            + "GdUnit4.TestRunner.GdUnit4TestRunnerScene_TrampolineCollectorDispatch.generated.cs(23,73): error CS0117: 'GdUnit4TestRunnerSceneCore' does not contain a definition for 'GodotInternal'.", said, StringComparison.Ordinal);
        Assert.Contains("This Godot's C# source generators cannot build a class whose base class an older Godot's built", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_build_through_godots_editor_is_said_and_one_that_ended_well_is_not()
    {
        GdUnit4Log log = new();
        Assert.Null(log.Heard("[GdUnit4] [Spinner] Rebuilding Godot Project ends with exit code: 0"));
        Assert.Null(log.Why("Spinner"));
        Assert.Null(log.Heard("[GdUnit4] [Spinner] Godot /err: ERROR: something Godot said"));
        Assert.Equal("GdUnit4's build through Godot's editor of its test runner into Spinner failed, so Godot ran none of its tests that need the engine: Rebuilding Godot Project ends with exit code: 1.",
            log.Heard("[GdUnit4] [Spinner] Rebuilding Godot Project ends with exit code: 1"));
    }

    [Fact]
    public void Words_of_another_adapter_or_another_assembly_are_their_own()
    {
        GdUnit4Log log = new();
        Assert.Null(log.Heard($"[xUnit.net 00:00:01] dotnet/out: {Path.GetFullPath("/game/Main.cs")}(4,9): error CS1002: ; expected"));
        Assert.Null(log.Heard("dotnet build failed with exit code: 1"));
        Assert.Null(log.Heard(OlderBase));
        Assert.DoesNotContain("older Godot", log.Heard("[GdUnit4] [Other] dotnet build failed with exit code: 1"), StringComparison.Ordinal);
    }
}
