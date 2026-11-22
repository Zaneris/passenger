using Passenger.App.Run.Devices;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>How a Mac Catalyst app is opened: by the system's open through a shell that carries its output through FIFOs
/// in a folder of its start's own, a new instance waited for to its end, the app's own arguments after open's --args; and, where nobody sees the window that runs it,
/// hidden and never brought in front, so its windows never reach the person's screen. A sandboxed app's start carries
/// Passenger's relay and inspector into its container, where it listens on sockets, and names them in its variables.</summary>
public class MacCatalystAppsTests
{
    private static readonly MacCatalystApp App = new("/src/Shop/bin/Debug/net11.0-maccatalyst/maccatalyst-arm64/Shop.app",
        "/src/Shop/bin/Debug/net11.0-maccatalyst/maccatalyst-arm64/Shop.app/Contents/MacOS/Shop")
    {
        Pipes = Path.Combine("/var/folders/xy/T/passenger-0123456789", MacCatalystApp.PipesName(4321, 2)),
    };

    [Fact]
    public void A_Mac_Catalyst_app_opens_as_the_system_opens_one_its_start_carried_into_its_own_container_folder() => Cases.All(
        ("an app nobody sees opens hidden behind every window none it kept reopened with its own arguments after opens", An_app_nobody_sees_opens_hidden_behind_every_window_none_it_kept_reopened_with_its_own_arguments_after_opens),
        ("an app a person sees opens in front as it last ended with its own arguments after opens", An_app_a_person_sees_opens_in_front_as_it_last_ended_with_its_own_arguments_after_opens),
        ("a sandboxed app is one whose entitlements give the app sandbox true", A_sandboxed_app_is_one_whose_entitlements_give_the_app_sandbox_true),
        ("a sandboxed apps start carries into a folder of its own in the containers temporary folder", A_sandboxed_apps_start_carries_into_a_folder_of_its_own_in_the_containers_temporary_folder),
        ("each start hands the opener a folder of its own for its pipes named for its process first", Each_start_hands_the_opener_a_folder_of_its_own_for_its_pipes_named_for_its_process_first),
        ("the sockets in a containers folder fit a sockets address for a long home and the templates bundle identifier", The_sockets_in_a_containers_folder_fit_a_sockets_address_for_a_long_home_and_the_templates_bundle_identifier),
        ("a debugged apps relay is loaded from its container and listens at a socket there for the sessions token", A_debugged_apps_relay_is_loaded_from_its_container_and_listens_at_a_socket_there_for_the_sessions_token),
        ("a start of an app whose build trims dotnets assemblies says what stays out and how to keep them whole", A_start_of_an_app_whose_build_trims_dotnets_assemblies_says_what_stays_out_and_how_to_keep_them_whole),
        ("an app a person runs opens in front as the system opens an app", An_app_a_person_runs_opens_in_front_as_the_system_opens_an_app));

    private void An_app_nobody_sees_opens_hidden_behind_every_window_none_it_kept_reopened_with_its_own_arguments_after_opens()
    {
        IReadOnlyList<string> opening = MacCatalystApps.Opening(App, unseen: true, ["--verbose", "a b"]);

        Assert.Equal(["-c", MacCatalystApps.Opener, "sh", App.Pipes, "-n", "-W", "-g", "-j", "-a", App.Bundle, "--args", "-ApplePersistenceIgnoreState", "YES", "--verbose", "a b"], opening);
    }

    private void An_app_a_person_sees_opens_in_front_as_it_last_ended_with_its_own_arguments_after_opens()
    {
        IReadOnlyList<string> opening = MacCatalystApps.Opening(App, unseen: false, ["--verbose"]);

        Assert.Equal(["-c", MacCatalystApps.Opener, "sh", App.Pipes, "-n", "-W", "-a", App.Bundle, "--args", "--verbose"], opening);
    }

    private void A_sandboxed_app_is_one_whose_entitlements_give_the_app_sandbox_true()
    {
        Assert.True(MacCatalystApp.Sandboxed(Entitlements("<key>com.apple.security.app-sandbox</key>\n\t<true/>")));
        Assert.False(MacCatalystApp.Sandboxed(Entitlements("<key>com.apple.security.app-sandbox</key><false/>")));
        Assert.False(MacCatalystApp.Sandboxed(Entitlements("<key>com.apple.security.network.client</key><true/>")));
        Assert.False(MacCatalystApp.Sandboxed(string.Empty));
    }

    private void A_sandboxed_apps_start_carries_into_a_folder_of_its_own_in_the_containers_temporary_folder()
    {
        string home = Path.Combine("/Users", "someone");

        Assert.Equal(Path.Combine(home, "Library", "Containers", "com.shop", "Data", "tmp"), MacCatalystApp.ContainerTemporary(home, "com.shop"));
        Assert.Equal("passenger-4321-2", MacCatalystApp.CarriedName(4321, 2));
    }

    private void Each_start_hands_the_opener_a_folder_of_its_own_for_its_pipes_named_for_its_process_first()
    {
        Assert.Equal("4321-2.pipes", MacCatalystApp.PipesName(4321, 2));
        Assert.NotEqual(MacCatalystApp.PipesName(4321, 2), MacCatalystApp.PipesName(4321, 3));
    }

    private void The_sockets_in_a_containers_folder_fit_a_sockets_address_for_a_long_home_and_the_templates_bundle_identifier()
    {
        MacCatalystApp app = App with
        {
            Carried = Path.Combine(MacCatalystApp.ContainerTemporary("/Users/firstname.lastname", "com.companyname.mauiapp1"), MacCatalystApp.CarriedName(99999, 99)),
        };

        Assert.All([app.RelaySocket, app.InspectorSocket, app.HandlersSocket], socket => Assert.True(SocketFolders.Fits(socket), SocketFolders.TooLong(socket)));
    }

    private void A_debugged_apps_relay_is_loaded_from_its_container_and_listens_at_a_socket_there_for_the_sessions_token()
    {
        MacCatalystApp app = App with { Carried = Path.Combine(MacCatalystApp.ContainerTemporary(Path.Combine("/Users", "a"), "com.shop"), MacCatalystApp.CarriedName(1, 1)) };

        IReadOnlyDictionary<string, string> given = MacCatalystApp.RelayVariables(Path.Combine(app.Carried, MacCatalystApp.RelayFile), app.RelaySocket, "r3l");

        Assert.Equal("1", given[RuntimeVariables.EnableProfiling]);
        Assert.Equal(AndroidApp.RelayProfiler, given[RuntimeVariables.Profiler]);
        Assert.Equal(Path.Combine(app.Carried, MacCatalystApp.RelayFile), given[RuntimeVariables.ProfilerPath]);
        Assert.Equal(Path.Combine(app.Carried, "r"), given[AndroidApp.RelaySocketVariable]);
        Assert.Equal("r3l", given[AndroidApp.RelayTokenVariable]);
        Assert.Equal(Path.Combine(App.Bundle, "Contents", "MonoBundle"), app.Assemblies);
    }

    private void A_start_of_an_app_whose_build_trims_dotnets_assemblies_says_what_stays_out_and_how_to_keep_them_whole()
    {
        string said = MacCatalystApp.Trimmed("Shop");

        Assert.StartsWith("Shop's build trims .NET's own assemblies", said, StringComparison.Ordinal);
        Assert.EndsWith("<MtouchLink>None</MtouchLink>", said, StringComparison.Ordinal);
        Assert.True(new MacCatalystApp("/a.app", "/a.app/Contents/MacOS/a").Whole, "An app read with no link mode is taken for one whose build trims nothing");
    }

    /// <summary>Entitlements as codesign writes them, holding <paramref name="entries"/>.</summary>
    private static string Entitlements(string entries) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"https://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict>{entries}</dict></plist>";

    private void An_app_a_person_runs_opens_in_front_as_the_system_opens_an_app()
    {
        IReadOnlyList<string> opening = MacCatalystApps.Opening(App, unseen: false, []);

        Assert.Equal(["-c", MacCatalystApps.Opener, "sh", App.Pipes, "-n", "-W", "-a", App.Bundle, "--args"], opening);
        Assert.DoesNotContain("-g", opening);
        Assert.DoesNotContain("-j", opening);
    }
}
