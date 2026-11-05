using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What a Passenger nobody sees makes of a plan by what the plan starts, not only what its project built: a desktop
/// app is refused, or kept off the screen, however it is named, and an Avalonia one takes the inspector that keeps it off.
/// Windows starts each on a desktop of its own instead.</summary>
[Collection(Beside.Name)]
public sealed class UnseenAppsTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("unseen-"));

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    public void A_gtk_program_a_configuration_names_never_reaches_the_screen()
    {
        KeptOff(Plan(Carrying("GtkSharp.dll", "Viewer")), "a GTK app");
    }

    [Fact]
    public void A_gtk_program_a_project_s_run_command_names_never_reaches_the_screen_whatever_the_project_built()
    {
        string built = Carrying("Console.dll", "Plain.dll");
        KeptOff(Plan(Carrying("GtkSharp.dll", "Viewer")) with { Built = built, Target = built }, "a GTK app");
    }

    [Fact]
    public void A_person_s_window_starts_a_named_program_as_it_is()
    {
        LaunchPlan seen = Plan(Carrying("GtkSharp.dll", "Viewer")) with { Unseen = false };
        Assert.True(seen.WhyNot("run") is null && !seen.OnOwnDesktop && UnseenApps.Inspected(seen) is null,
            $"A person's window would refuse or hide a program they asked to see: {seen.WhyNot("run")}");
    }

    [Fact]
    public void An_avalonia_program_a_configuration_names_takes_the_inspector_that_keeps_it_off_the_screen()
    {
        string assembly = Carrying("Avalonia.Base.dll", "App.dll");
        LaunchPlan named = Plan(Dotnet.Host()) with { Arguments = [assembly] };
        Assert.Equal(assembly, UnseenApps.Inspected(named));
        Assert.Null(named.WhyNot("run"));
        Assert.Null(UnseenApps.Inspected(Plan(Carrying("Plain.dll", "Tool"))));
    }

    [Fact]
    public void A_named_dotnet_run_of_an_avalonia_project_is_kept_off_the_screen_as_no_inspector_goes_through_it()
    {
        string target = Carrying("Avalonia.Base.dll", "App.dll");
        LaunchPlan named = Plan(Dotnet.Host()) with { Arguments = ["run"], Target = target };
        KeptOff(named, DesktopPrograms.Avalonia);
        Assert.Null(UnseenApps.Inspected(named));
    }

    /// <summary>A console project's plan says nothing of what a <c>dotnet run --project OTHER</c> it names starts.</summary>
    [Fact]
    public void A_named_dotnet_run_of_another_project_is_kept_off_the_screen_by_what_that_project_is()
    {
        string other = Path.Combine(folder, "Gui", "Gui.csproj");
        LaunchPlan named = Plan(Dotnet.Host()) with
        {
            Arguments = ["run", "--project", other], Target = Carrying("Console.dll", "Tool.dll"), DesktopProject = "a GTK app", NamedProject = other,
        };
        KeptOff(named, "a GTK app");
        Assert.Null(UnseenApps.Inspected(named));
    }

    [Fact]
    public void An_avalonia_app_dotnet_watch_would_start_is_kept_off_the_screen()
    {
        string built = Carrying("Avalonia.Base.dll", "App.dll");
        LaunchPlan watched = Plan(Dotnet.Host()) with { Arguments = [built], Built = built, Target = built };
        Assert.Null(watched.WhyNot("run"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("dotnet watch", watched.WhyNot("run", built: false), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_desktop_project_never_built_is_known_by_what_it_says_before_dotnet_watch_builds_it()
    {
        string built = Path.Combine(folder, "bin", "App.dll");
        LaunchPlan never = Plan(Dotnet.Host()) with { Arguments = [built], Built = built, Target = built, DesktopProject = "a GTK app" };
        if (OperatingSystem.IsWindows())
        {
            Assert.True(never.OnOwnDesktop, "A desktop app dotnet watch builds and starts went to the person's desktop");
            return;
        }
        Assert.Contains("a GTK app", never.WhyNot("run", built: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_named_for_a_game_of_gdscript_alone_is_read_as_any_other()
    {
        string project = Path.Combine(folder, Passenger.Workspace.GodotProject.FileName);
        File.WriteAllText(project, "config_version=5\n");
        RunConfiguration named = new() { Name = "Viewer", Project = project, Program = Carrying("GtkSharp.dll", "Viewer") };
        LaunchPlan plan = await LaunchPlan.ResolveAsync(named, [], null, folder, TestContext.Current.CancellationToken, unseen: true);
        KeptOff(plan, "a GTK app");
    }

    /// <summary>A launch profile may name a display, as one forwarded over SSH does: where nobody is to see the program, that
    /// names nothing, or it would reach the screen the process's own environment no longer leads to.</summary>
    [Fact]
    public async Task On_Linux_a_display_a_configuration_names_stays_out_of_what_a_passenger_nobody_sees_starts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux finds its display in the environment");
        string project = Path.Combine(folder, Passenger.Workspace.GodotProject.FileName);
        File.WriteAllText(project, "config_version=5\n");
        RunConfiguration named = new()
        {
            Name = "Tool", Project = project, Program = Carrying("Plain.dll", "Tool"),
            Environment = new Dictionary<string, string> { [NoDisplay.X11] = ":1", [NoDisplay.Wayland] = "wayland-1", ["KEPT"] = "yes" },
        };

        LaunchPlan plan = await LaunchPlan.ResolveAsync(named, [], null, folder, TestContext.Current.CancellationToken, unseen: true);
        LaunchPlan seen = await LaunchPlan.ResolveAsync(named, [], null, folder, TestContext.Current.CancellationToken, unseen: false);

        Assert.False(plan.Environment.ContainsKey(NoDisplay.X11), "A configuration's DISPLAY reaches what a Passenger nobody sees starts");
        Assert.Equal((NoDisplay.Nowhere, "yes"), (plan.Environment[NoDisplay.Wayland], plan.Environment["KEPT"]));
        Assert.Equal((":1", "wayland-1"), (seen.Environment[NoDisplay.X11], seen.Environment[NoDisplay.Wayland]));
    }

    /// <summary>A plan nobody is to see, of the program given, in the test's folder.</summary>
    private LaunchPlan Plan(string program) => new() { Name = "App", Program = program, WorkingDirectory = folder, Unseen = true };

    /// <summary>A file in a folder of its own, beside the file that names its framework.</summary>
    private string Carrying(string framework, string file)
    {
        string own = Directory.CreateDirectory(Path.Combine(folder, Path.GetFileNameWithoutExtension(framework))).FullName;
        File.WriteAllText(Path.Combine(own, framework), string.Empty);
        string path = Path.Combine(own, file);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    /// <summary>A plan refused saying what it is, or on Windows started on a desktop of its own.</summary>
    private static void KeptOff(LaunchPlan plan, string called)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(plan.OnOwnDesktop && plan.WhyNot("run") is null, $"{called} a Passenger nobody sees runs went to the person's desktop: {plan.WhyNot("run")}");
            return;
        }
        Assert.True(plan.WhyNot("run") is { } why && why.Contains(called, StringComparison.Ordinal),
            $"{called} a Passenger nobody sees runs was let through to the person's screen: {plan.WhyNot("run")}");
    }
}
