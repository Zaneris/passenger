using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The .NET Godot's editor on macOS is given to run on, so its own builds of the game's C# work: as it stands where
/// they do; the installer's, where Godot builds, where the .NET DOTNET_ROOT names has no released SDK there and Godot's
/// Microsoft.Build.Locator 1.2.6 would name a .NET 11 SDK's folders wrongly; else words saying why its builds fail. And
/// which installation that editor runs on, as Godot's own resolver finds it.</summary>
public sealed class GodotEditorDotnetTests
{
    private const string Home = "/Users/someone/.dotnet";

    private const string Editor = "Godot 4.7.2's editor";

    /// <summary>A .NET installed in the home folder: 11 released, 10 and 9 beside it.</summary>
    private static readonly DotnetInstallation Own = new(Home, ["11.0.100", "10.0.404"], ["11.0.0", "10.0.15", "9.0.7"]);

    /// <summary>The installer's .NET: 11 only as previews, 10 released.</summary>
    private static readonly DotnetInstallation Installed = new(GodotEditorDotnet.InstallerRoot, ["11.0.100-rc.3.26522.148", "10.0.403"],
        ["11.0.0-rc.3.26522.148", "10.0.14", "9.0.21", "8.0.17"]);

    private static readonly string Builder = Path.Combine(GodotEditorDotnet.InstallerRoot, Dotnet.HostName);

    private static readonly Version CarriedLocator = new(1, 2, 6);

    private static GodotEditorDotnet.Machine OnOwn(Version? locator = null, string? pathSdk = "11.0.100") =>
        new(Own, Installed, Builder, pathSdk, locator ?? CarriedLocator, Previews: false);

    /// <summary>Godot's editor is given the .NET its builds need, or the person is told why and what to do.</summary>
    [Fact]
    public void An_editor_is_given_the_dotnet_its_builds_need_or_told_why() => Cases.All(
        (nameof(An_editor_on_another_dotnet_with_no_released_SDK_where_it_builds_is_given_the_installers_dotnet_for_a_game_that_runs_there),
            An_editor_on_another_dotnet_with_no_released_SDK_where_it_builds_is_given_the_installers_dotnet_for_a_game_that_runs_there),
        (nameof(A_game_newer_than_every_dotnet_its_editor_builds_on_is_told_why_and_what_to_do_nothing_changed),
            A_game_newer_than_every_dotnet_its_editor_builds_on_is_told_why_and_what_to_do_nothing_changed),
        (nameof(The_persons_own_DOTNET_ROOT_is_kept_and_its_failure_said_offering_no_older_target_it_would_not_take),
            The_persons_own_DOTNET_ROOT_is_kept_and_its_failure_said_offering_no_older_target_it_would_not_take),
        (nameof(An_editor_whose_builds_work_is_left_as_it_stands), An_editor_whose_builds_work_is_left_as_it_stands));

    /// <summary>A new game targets the newest .NET its editor builds and runs on, found as the editor finds
    /// it.</summary>
    [Fact]
    public void A_new_game_targets_the_newest_dotnet_its_editor_builds_and_runs() => Cases.All(
        (nameof(A_new_game_targets_the_newest_dotnet_its_editor_builds_and_runs_started_on_its_own_and_through_passenger),
            A_new_game_targets_the_newest_dotnet_its_editor_builds_and_runs_started_on_its_own_and_through_passenger),
        (nameof(Through_passenger_a_game_targets_the_newer_dotnet_of_the_installation_the_editor_builds_with_which_passenger_hands_it),
            Through_passenger_a_game_targets_the_newer_dotnet_of_the_installation_the_editor_builds_with_which_passenger_hands_it),
        (nameof(Its_dotnet_is_the_newest_released_major_from_8_or_a_preview_where_it_rolls_to_one),
            Its_dotnet_is_the_newest_released_major_from_8_or_a_preview_where_it_rolls_to_one),
        (nameof(It_runs_on_the_processors_DOTNET_ROOT_then_DOTNET_ROOT_then_the_registered_dotnet_then_the_installers),
            It_runs_on_the_processors_DOTNET_ROOT_then_DOTNET_ROOT_then_the_registered_dotnet_then_the_installers));

    private void An_editor_on_another_dotnet_with_no_released_SDK_where_it_builds_is_given_the_installers_dotnet_for_a_game_that_runs_there()
    {
        GodotEditorDotnet.Given given = GodotEditorDotnet.Decide(OnOwn(), game: 10, [RuntimeVariables.Root], Editor);
        Assert.True(given.WhyNot is null && given.Variables.Count == 1 && given.Variables[RuntimeVariables.Root] == GodotEditorDotnet.InstallerRoot,
            $"An editor on {Home}'s .NET 11, building with the installer's dotnet that has 11 only as previews, for a net10.0 game, was given "
            + $"[{string.Join(", ", given.Variables)}] ({given.WhyNot}), so every build it starts finds no Microsoft.NET.Sdk");
    }

    private void A_game_newer_than_every_dotnet_its_editor_builds_on_is_told_why_and_what_to_do_nothing_changed()
    {
        GodotEditorDotnet.Given given = GodotEditorDotnet.Decide(OnOwn(), game: 11, [RuntimeVariables.Root], Editor);
        Assert.Empty(given.Variables);
        Assert.Equal($"{Editor} builds no C# here: it runs on .NET 11 of {Home} but builds with {Builder}, which has no released .NET 11 SDK, so it takes the PATH's "
            + "SDK 11.0.100 through the Microsoft.Build.Locator 1.2.6 it carries, which names a .NET 11 SDK's folders without their separator, and every build finds "
            + $"no Microsoft.NET.Sdk: install the .NET 11 SDK into {GodotEditorDotnet.InstallerRoot} (Microsoft's installer puts it there), or target the game at "
            + "net10.0, which it then runs on from there", given.WhyNot);
    }

    private void The_persons_own_DOTNET_ROOT_is_kept_and_its_failure_said_offering_no_older_target_it_would_not_take()
    {
        GodotEditorDotnet.Given given = GodotEditorDotnet.Decide(OnOwn(), game: 10, [], Editor);
        Assert.Empty(given.Variables);
        Assert.True(given.WhyNot is { } why && why.StartsWith($"{Editor} builds no C# here", StringComparison.Ordinal) && !why.Contains("target the game", StringComparison.Ordinal),
            $"The person's own DOTNET_ROOT breaking the build said: {given.WhyNot}");
    }

    private void An_editor_whose_builds_work_is_left_as_it_stands() => Cases.Each<(string With, bool Released, string Locator, string? PathSdk)>(
    [
        ("a released SDK of its .NET where it builds", true, "1.2.6", "11.0.100"),
        ("a locator that names a .NET 11 SDK's folders rightly", false, "1.6.0", "11.0.100"),
        ("a .NET 10 SDK on the PATH, its base path ending in a separator", false, "1.2.6", "10.0.404"),
        ("no dotnet on the PATH, so no SDK registered", false, "1.2.6", null),
    ], row => An_editor_whose_builds_work_is_left_as_it_stands(row.With, row.Released, row.Locator, row.PathSdk));

    private void An_editor_whose_builds_work_is_left_as_it_stands(string with, bool released, string locator, string? pathSdk)
    {
        DotnetInstallation builds = released ? Installed with { Sdks = ["11.0.100", .. Installed.Sdks], Runtimes = ["11.0.0", .. Installed.Runtimes] } : Installed;
        GodotEditorDotnet.Given given = GodotEditorDotnet.Decide(new(Own, builds, Builder, pathSdk, Version.Parse(locator), false), game: 11, [RuntimeVariables.Root], Editor);
        Assert.True(given == GodotEditorDotnet.Given.AsItStands, $"An editor with {with} was given [{string.Join(", ", given.Variables)}] ({given.WhyNot})");
    }

    /// <summary>The editor on its own, as from the Dock: no DOTNET_ROOT, so on the installer's .NET, which /etc/dotnet registers.</summary>
    private static GodotEditorDotnet.Start Alone(DotnetInstallation installed, Version? locator = null) =>
        new(new GodotEditorDotnet.Machine(installed, installed, Builder, "11.0.100", locator ?? CarriedLocator, Previews: false), []);

    /// <summary>The editor started through Passenger, its DOTNET_ROOT naming the home folder's .NET, which Passenger may replace.</summary>
    private static GodotEditorDotnet.Start Through(DotnetInstallation own, DotnetInstallation installed) =>
        new(new GodotEditorDotnet.Machine(own, installed, Builder, "11.0.100", CarriedLocator, Previews: false), [RuntimeVariables.Root]);

    private void A_new_game_targets_the_newest_dotnet_its_editor_builds_and_runs_started_on_its_own_and_through_passenger()
    {
        GodotEditorDotnet.GameTargets targets = new([Alone(Installed), Through(Own, Installed)], Editor);
        Assert.Equal(new GodotEditorDotnet.GameDotnet(10, GodotEditorDotnet.InstallerRoot), targets.Newest);
        Assert.Null(targets.WhyNot("net10.0"));
        Assert.Null(targets.WhyNot("net9.0"));
        Assert.Equal($"{Editor} runs no C# of the game here: it runs on .NET 10 of {GodotEditorDotnet.InstallerRoot}, older than the .NET 11 the game targets, and builds "
            + $"with {Builder}: install the .NET 11 SDK into {GodotEditorDotnet.InstallerRoot} (Microsoft's installer puts it there), or target the game at net10.0, "
            + "which it then runs on from there", targets.WhyNot("net11.0"));
    }

    private void Through_passenger_a_game_targets_the_newer_dotnet_of_the_installation_the_editor_builds_with_which_passenger_hands_it()
    {
        DotnetInstallation older = new(Home, ["10.0.404"], ["10.0.15"]);
        DotnetInstallation released = Installed with { Sdks = ["11.0.100", .. Installed.Sdks], Runtimes = ["11.0.0", .. Installed.Runtimes] };
        Assert.Equal(new GodotEditorDotnet.GameDotnet(11, GodotEditorDotnet.InstallerRoot), GodotEditorDotnet.NewestOf(Through(older, released)));
        Assert.Equal(new GodotEditorDotnet.GameDotnet(10, Home), GodotEditorDotnet.NewestOf(Through(older, released) with { Roots = [] }));
    }

    private void Its_dotnet_is_the_newest_released_major_from_8_or_a_preview_where_it_rolls_to_one()
    {
        Assert.Equal(10, GodotEditorDotnet.Major(Installed, previews: false));
        Assert.Equal(11, GodotEditorDotnet.Major(Installed, previews: true));
        Assert.Null(GodotEditorDotnet.Major(new DotnetInstallation("/old", [], ["7.0.20", "6.0.36"]), previews: false));
    }

    private void It_runs_on_the_processors_DOTNET_ROOT_then_DOTNET_ROOT_then_the_registered_dotnet_then_the_installers()
    {
        HashSet<string> folders = ["/arm", "/root", "/registered", "/legacy", .. ((string[])["/arm", "/root", "/registered", "/legacy", GodotEditorDotnet.InstallerRoot]).Select(Fxr)];
        Dictionary<string, string?> variables = new() { [RuntimeVariables.RootFor("arm64")] = "/arm", [RuntimeVariables.Root] = "/root" };
        Dictionary<string, string?> lines = new() { ["/etc/dotnet/install_location_arm64"] = "/registered", ["/etc/dotnet/install_location"] = "/legacy" };
        string? Found() => GodotEditorDotnet.RunsOn("arm64", n => variables.GetValueOrDefault(n), folders.Contains, f => lines.GetValueOrDefault(f), _ => false);

        Assert.Equal("/arm", Found());
        variables[RuntimeVariables.RootFor("arm64")] = "/gone";
        Assert.Equal("/root", Found());
        variables.Clear();
        Assert.Equal("/registered", Found());
        lines["/etc/dotnet/install_location_arm64"] = string.Empty;
        Assert.Equal(GodotEditorDotnet.InstallerRoot, Found());
        lines.Remove("/etc/dotnet/install_location_arm64");
        Assert.Equal("/legacy", Found());
        folders.Remove(Fxr("/legacy"));
        Assert.Null(Found());

        static string Fxr(string root) => Path.Combine(root, "host", "fxr");
    }

}
