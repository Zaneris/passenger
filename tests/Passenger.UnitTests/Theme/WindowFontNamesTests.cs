using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>The window's typeface a setting names: the carried Inter where it names none or Inter, the system's own UI
/// font by system-ui (macOS's hidden San Francisco where it draws, else the platform's default family), an installed
/// family spelt as installed, and a name nothing has as missing; Settings lists the installed ones a person may choose.</summary>
public class WindowFontNamesTests
{
    private static readonly string[] Installed = ["Segoe UI", "Arial", "JetBrains Mono", ".AppleSystemUIFont", "Noto Sans"];

    [Fact]
    public void The_windows_font_is_the_family_named_installed_or_the_carried_Inter() => Cases.All(
        ("no name or Inter is the carried Inter", () => Cases.Each<string?>(
        [
            null,
            " inter ",
        ], No_name_or_Inter_is_the_carried_Inter)),
        ("system ui is the systems own family whatever it is here", () => Cases.Each<string>(
        [
            " System-UI ",
        ], System_ui_is_the_systems_own_family_whatever_it_is_here)),
        ("an installed family is taken by any letter case and spelt as installed", An_installed_family_is_taken_by_any_letter_case_and_spelt_as_installed),
        ("the system font is San Francisco where macOS draws it and the platforms default elsewhere", The_system_font_is_San_Francisco_where_macOS_draws_it_and_the_platforms_default_elsewhere),
        ("the system choice names its family unless the system hides it", The_system_choice_names_its_family_unless_the_system_hides_it));

    private void No_name_or_Inter_is_the_carried_Inter(string? name) =>
        Assert.Equal(new WindowFontChoice(WindowFontKind.Carried, WindowFontNames.Carried), WindowFontNames.Of(name, Installed, "Segoe UI"));

    private void System_ui_is_the_systems_own_family_whatever_it_is_here(string name) =>
        Assert.Equal(new WindowFontChoice(WindowFontKind.System, "Noto Sans"), WindowFontNames.Of(name, Installed, "Noto Sans"));

    private void An_installed_family_is_taken_by_any_letter_case_and_spelt_as_installed() =>
        Assert.Equal(new WindowFontChoice(WindowFontKind.Installed, "JetBrains Mono"), WindowFontNames.Of("jetbrains mono ", Installed, "Segoe UI"));

    private void The_system_font_is_San_Francisco_where_macOS_draws_it_and_the_platforms_default_elsewhere()
    {
        Assert.Equal(WindowFontNames.MacSystem, WindowFontNames.SystemFamily(macSystemDraws: true, "Helvetica"));
        Assert.Equal("Segoe UI", WindowFontNames.SystemFamily(macSystemDraws: false, "Segoe UI"));
    }

    private void The_system_choice_names_its_family_unless_the_system_hides_it()
    {
        Assert.Equal("System font (Segoe UI)", WindowFontNames.SystemShown("Segoe UI"));
        Assert.Equal("System font", WindowFontNames.SystemShown(WindowFontNames.MacSystem));
    }
}
