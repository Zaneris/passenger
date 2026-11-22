using Passenger.App;

namespace Passenger.UnitTests.Launch;

/// <summary>The command line's <c>--run [NAME]</c> and <c>--debug [NAME]</c>: the word after the flag is the configuration's
/// name unless it is another flag, and a name the folder's configurations lack is refused listing those it has, never run
/// as the one chosen in its place.</summary>
public class ConfigurationStartTests
{
    [Fact]
    public void Run_or_debug_from_the_command_line_starts_a_configuration_by_name_or_says_why_not() => Cases.All(
        ("the word after run is the configurations name and is read past", The_word_after_run_is_the_configurations_name_and_is_read_past),
        ("run or debug followed by a flag an empty word or nothing starts the configuration chosen", () => Cases.Each<(string[], bool)>(
        [
            (new[] { ".", "--debug" }, true),
            (new[] { ".", "--run", "" }, false),
        ], row => Run_or_debug_followed_by_a_flag_an_empty_word_or_nothing_starts_the_configuration_chosen(row.Item1, row.Item2))),
        ("a start that came to nothing says why and a failed builds first error", A_start_that_came_to_nothing_says_why_and_a_failed_builds_first_error),
        ("a name no configuration has is refused listing the folders configurations", A_name_no_configuration_has_is_refused_listing_the_folders_configurations),
        ("a folder with no configurations refuses a start with a name or without", A_folder_with_no_configurations_refuses_a_start_with_a_name_or_without));

    private void The_word_after_run_is_the_configurations_name_and_is_read_past()
    {
        string[] args = [".", "--run", "Hello: https", "--theme", "dark"];
        int at = 1;

        ConfigurationStart start = ConfigurationStart.Read(args, ref at);

        Assert.True(start == new ConfigurationStart("Hello: https", Debug: false) && at == 2,
            $"--run Hello: https would start {start} and read on from '{args[at + 1]}', taking the name for the folder or a flag");
    }

    private void Run_or_debug_followed_by_a_flag_an_empty_word_or_nothing_starts_the_configuration_chosen(string[] args, bool debug)
    {
        int at = 1;

        ConfigurationStart start = ConfigurationStart.Read(args, ref at);

        Assert.True(start == new ConfigurationStart(null, debug) && at == 1,
            $"'{string.Join(' ', args)}' would start {start}, read up to '{args[Math.Min(at, args.Length - 1)]}', rather than the configuration chosen");
    }

    private void A_start_that_came_to_nothing_says_why_and_a_failed_builds_first_error()
    {
        Assert.Equal("--run: Greenhouse cannot run: Build failed: 1 error, 0 warnings; Problems lists them (Greenhouse.cs:8:5: Invalid token)",
            new ConfigurationStart("Greenhouse", Debug: false).Cannot("Greenhouse", "Build failed: 1 error, 0 warnings; Problems lists them", "Greenhouse.cs:8:5: Invalid token"));
        Assert.Equal("--debug: Hello cannot debug: it has ended", new ConfigurationStart(null, Debug: true).Cannot("Hello", "it has ended"));
    }

    private void A_name_no_configuration_has_is_refused_listing_the_folders_configurations()
    {
        string? why = new ConfigurationStart("Nope", Debug: false).WhyNot("hello", ["Hello", "Hello: https", "Worker"]);

        Assert.Equal("--run 'Nope' names no run configuration of hello, whose configurations are 'Hello', 'Hello: https' and 'Worker'", why);
    }

    private void A_folder_with_no_configurations_refuses_a_start_with_a_name_or_without()
    {
        Assert.Equal("--debug: hello has no run configuration to debug", new ConfigurationStart(null, Debug: true).WhyNot("hello", []));
        Assert.Equal("--run 'Hello': hello has no run configuration to run", new ConfigurationStart("Hello", Debug: false).WhyNot("hello", []));
        Assert.Null(new ConfigurationStart(null, Debug: false).WhyNot("hello", ["Hello"]));
    }
}
