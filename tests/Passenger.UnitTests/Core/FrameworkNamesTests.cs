using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A target framework's short name, its .NET version and the Windows desktop's shared frameworks are read one
/// way, by the XAML core and previews, the Solution view, Hot Reload, the container images and a package's dependencies
/// alike.</summary>
public sealed class FrameworkNamesTests
{
    [Fact]
    public void A_target_framework_reads_as_dotnet_names_it_its_version_and_platform_apart() => Cases.All(
        ("a target framework reads as a project names it", () => Cases.Each<(string, string)>(
        [
            (".NETCoreApp,Version=v10.0", "net10.0"),
            (".netcoreapp,version=v8.0", "net8.0"),
            (".NETStandard,Version=v2.0", ".NETStandard,Version=v2.0"),
        ], row => A_target_framework_reads_as_a_project_names_it(row.Item1, row.Item2))),
        ("wpf and windows forms are found in their profiles and the windows desktop whole in any letter case", Wpf_and_windows_forms_are_found_in_their_profiles_and_the_windows_desktop_whole_in_any_letter_case),
        ("a net framework target is told from the ones coreclr runs", () => Cases.Each<(string, bool)>(
        [
            ("net48", true),
            (".NETFramework,Version=v4.8", true),
            ("net10.0-windows", false),
            ("netstandard2.0", false),
        ], row => A_net_framework_target_is_told_from_the_ones_coreclr_runs(row.Item1, row.Item2))),
        ("a target frameworks dotnet version reads in any letter case its platform left off", () => Cases.Each<(string?, string?)>(
        [
            ("NET8.0-windows", "8.0"),
            ("net8.0-windows10.0.19041.0", "8.0"),
            (".netcoreapp,version=v2.1", "2.1"),
            ("net48", null),
            (null, null),
        ], row => A_target_frameworks_dotnet_version_reads_in_any_letter_case_its_platform_left_off(row.Item1, row.Item2))),
        ("a target frameworks platform reads in lower case whatever its version", () => Cases.Each<(string?, string?)>(
        [
            ("net11.0-android36.0", FrameworkNames.Android),
            ("NET11.0-iOS", FrameworkNames.Ios),
            ("net11.0-windows10.0.19041.0", FrameworkNames.Windows),
            ("net11.0-browser", "browser"),
            ("net11.0-", null),
        ], row => A_target_frameworks_platform_reads_in_lower_case_whatever_its_version(row.Item1, row.Item2))),
        ("a platforms version is told from a framework naming the platform alone", A_platforms_version_is_told_from_a_framework_naming_the_platform_alone));

    private void A_target_framework_reads_as_a_project_names_it(string named, string shortName)
    {
        Assert.Equal(shortName, FrameworkNames.Short(named));
    }

    private void Wpf_and_windows_forms_are_found_in_their_profiles_and_the_windows_desktop_whole_in_any_letter_case()
    {
        Assert.True(FrameworkNames.UsesWpf(["Microsoft.NETCore.App", "microsoft.windowsdesktop.app.wpf"]), "WPF's profile named in lower case was not read as WPF");
        Assert.True(FrameworkNames.UsesWpf([FrameworkNames.WindowsDesktop]), "The Windows desktop whole was not read as taking in WPF");
        Assert.False(FrameworkNames.UsesWpf([FrameworkNames.WindowsForms]), "Windows Forms' profile alone was read as WPF");
        Assert.True(FrameworkNames.UsesWindowsForms([FrameworkNames.WindowsDesktop]), "The Windows desktop whole was not read as taking in Windows Forms");
        Assert.True(FrameworkNames.IsWindowsDesktop(FrameworkNames.Wpf), "WPF's profile was not read as the Windows desktop's");
        Assert.False(FrameworkNames.IsWindowsDesktop("Microsoft.AspNetCore.App"), "ASP.NET Core's shared framework was read as the Windows desktop's");
    }

    private void A_net_framework_target_is_told_from_the_ones_coreclr_runs(string framework, bool netFramework)
    {
        Assert.Equal(netFramework, FrameworkNames.IsNetFramework(framework));
    }

    private void A_target_frameworks_dotnet_version_reads_in_any_letter_case_its_platform_left_off(string? framework, string? version)
    {
        Assert.True(FrameworkNames.VersionOf(framework)?.ToString() == version,
            $"{framework ?? "null"} reads as .NET {FrameworkNames.VersionOf(framework)?.ToString() ?? "none"}, not {version ?? "none"}");
        Assert.True(FrameworkNames.Major(framework) == (version is null ? (int?)null : int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture)),
            $"{framework ?? "null"}'s major reads as {FrameworkNames.Major(framework)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
    }

    private void A_target_frameworks_platform_reads_in_lower_case_whatever_its_version(string? framework, string? platform)
    {
        Assert.True(FrameworkNames.PlatformOf(framework) == platform, $"{framework ?? "null"}'s platform reads as {FrameworkNames.PlatformOf(framework) ?? "none"}");
    }

    private void A_platforms_version_is_told_from_a_framework_naming_the_platform_alone()
    {
        Assert.True(FrameworkNames.NamesPlatformVersion("net11.0-windows10.0.19041.0"), "A Windows app's head names its platform's version");
        Assert.True(FrameworkNames.NamesPlatformVersion("net11.0-android36.0"), "An Android framework of API 36 names its platform's version");
        Assert.False(FrameworkNames.NamesPlatformVersion("net11.0-windows"), "A desktop Windows app's framework was read as naming a version");
        Assert.False(FrameworkNames.NamesPlatformVersion("net11.0"), "A framework of no platform was read as naming a platform's version");
    }
}
