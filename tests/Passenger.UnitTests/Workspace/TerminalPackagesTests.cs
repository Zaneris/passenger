using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Which packages draw a program's output for a terminal: each named one and its family by id, in any letter
/// case, never a package whose id only begins with the same letters; a project's own first such package is what it draws
/// with.</summary>
public class TerminalPackagesTests
{
    [Theory]
    [InlineData("Spectre.Console")]
    [InlineData("spectre.console")]
    [InlineData("Spectre.Console.Cli")]
    [InlineData("Terminal.Gui")]
    [InlineData("XenoAtom.Terminal")]
    [InlineData("XenoAtom.Terminal.UI")]
    [InlineData("Sharprompt")]
    [InlineData("Consolonia.Core")]
    public void A_package_drawing_for_a_terminal_or_of_its_family_is_one(string package) =>
        Assert.True(ProjectModel.DrawsOnTerminal(package), $"{package} draws for a terminal, so its program would show plain text in the Run window");

    [Theory]
    [InlineData("Spectre.ConsoleX")]
    [InlineData("Spectre")]
    [InlineData("Terminal.GuiTools")]
    [InlineData("XenoAtom.Ansi")]
    [InlineData("Microsoft.Extensions.Logging.Console")]
    public void A_package_only_beginning_with_the_same_letters_is_none(string package) =>
        Assert.False(ProjectModel.DrawsOnTerminal(package), $"{package} draws nothing for a terminal, so its program would leave the Run window");

    [Fact]
    public void A_project_draws_with_its_first_terminal_package_and_with_none_where_it_has_none()
    {
        ProjectModel drawing = new("/work/Shop/Shop.csproj", "Shop", "Exe", ["net10.0"], [("Serilog", "4.0.0"), ("Spectre.Console.Cli", "0.57.2"), ("Terminal.Gui", "2.0.0")], [], true);
        ProjectModel plain = drawing with { Packages = [("Serilog", "4.0.0")] };
        Assert.Equal("Spectre.Console.Cli", drawing.DrawsWith);
        Assert.Null(plain.DrawsWith);
    }
}
