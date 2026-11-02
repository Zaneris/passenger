using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A target framework's short name and the Windows desktop's shared frameworks are read one way, by the XAML
/// core, the Solution view and a package's dependencies alike.</summary>
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
}
