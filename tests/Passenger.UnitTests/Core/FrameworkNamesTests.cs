using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A target framework's short name, its .NET version and the Windows desktop's shared frameworks are read one
/// way, by the XAML core and previews, the Solution view, Hot Reload, the container images and a package's dependencies
/// alike.</summary>
public sealed class FrameworkNamesTests
{
    [Theory]
    [InlineData(".NETCoreApp,Version=v10.0", "net10.0")]
    [InlineData(".NETCoreApp10.0", "net10.0")]
    [InlineData(".netcoreapp,version=v8.0", "net8.0")]
    [InlineData("net9.0", "net9.0")]
    [InlineData(".NETStandard,Version=v2.0", ".NETStandard,Version=v2.0")]
    public void A_target_framework_reads_as_a_project_names_it(string named, string shortName)
    {
        Assert.Equal(shortName, FrameworkNames.Short(named));
    }

    [Fact]
    public void Wpf_and_windows_forms_are_found_in_their_profiles_and_the_windows_desktop_whole_in_any_letter_case()
    {
        Assert.True(FrameworkNames.UsesWpf(["Microsoft.NETCore.App", "microsoft.windowsdesktop.app.wpf"]), "WPF's profile named in lower case was not read as WPF");
        Assert.True(FrameworkNames.UsesWpf([FrameworkNames.WindowsDesktop]), "The Windows desktop whole was not read as taking in WPF");
        Assert.False(FrameworkNames.UsesWpf([FrameworkNames.WindowsForms]), "Windows Forms' profile alone was read as WPF");
        Assert.True(FrameworkNames.UsesWindowsForms([FrameworkNames.WindowsDesktop]), "The Windows desktop whole was not read as taking in Windows Forms");
        Assert.True(FrameworkNames.IsWindowsDesktop(FrameworkNames.Wpf), "WPF's profile was not read as the Windows desktop's");
        Assert.False(FrameworkNames.IsWindowsDesktop("Microsoft.AspNetCore.App"), "ASP.NET Core's shared framework was read as the Windows desktop's");
    }

    [Theory]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("NET462", true)]
    [InlineData(".NETFramework,Version=v4.8", true)]
    [InlineData("net8.0", false)]
    [InlineData("net10.0-windows", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("net", false)]
    public void A_net_framework_target_is_told_from_the_ones_coreclr_runs(string framework, bool netFramework)
    {
        Assert.Equal(netFramework, FrameworkNames.IsNetFramework(framework));
    }

    [Theory]
    [InlineData("net10.0", "10.0")]
    [InlineData("NET8.0-windows", "8.0")]
    [InlineData("net8.0-windows10.0.19041.0", "8.0")]
    [InlineData("Net9.0-android", "9.0")]
    [InlineData("netcoreapp3.1", "3.1")]
    [InlineData(".NETCoreApp,Version=v10.0", "10.0")]
    [InlineData(".netcoreapp,version=v2.1", "2.1")]
    [InlineData("net48", null)]
    [InlineData("net4.8", null)]
    [InlineData("netstandard2.0", null)]
    [InlineData(".NETFramework,Version=v4.8", null)]
    [InlineData("net", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_target_frameworks_dotnet_version_reads_in_any_letter_case_its_platform_left_off(string? framework, string? version)
    {
        Assert.True(FrameworkNames.VersionOf(framework)?.ToString() == version,
            $"{framework ?? "null"} reads as .NET {FrameworkNames.VersionOf(framework)?.ToString() ?? "none"}, not {version ?? "none"}");
        Assert.True(FrameworkNames.Major(framework) == (version is null ? (int?)null : int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture)),
            $"{framework ?? "null"}'s major reads as {FrameworkNames.Major(framework)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
    }
}
