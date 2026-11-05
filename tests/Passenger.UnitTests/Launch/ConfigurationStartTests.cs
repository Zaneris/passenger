using Passenger.App;

namespace Passenger.UnitTests.Launch;

/// <summary>The command line's <c>--run [NAME]</c> and <c>--debug [NAME]</c>: the word after the flag is the configuration's
/// name unless it is another flag, and a name the folder's configurations lack is refused listing those it has, never run
/// as the one chosen in its place.</summary>
public class ConfigurationStartTests
{
    [Fact]
    public void The_word_after_run_is_the_configurations_name_and_is_read_past()
    {
        string[] args = [".", "--run", "Hello: https", "--theme", "dark"];
        int at = 1;

        ConfigurationStart start = ConfigurationStart.Read(args, ref at);

        Assert.True(start == new ConfigurationStart("Hello: https", Debug: false) && at == 2,
            $"--run Hello: https would start {start} and read on from '{args[at + 1]}', taking the name for the folder or a flag");
    }

    [Theory]
    [InlineData(new[] { ".", "--debug" }, true)]
    [InlineData(new[] { ".", "--run", "--theme", "dark" }, false)]
    [InlineData(new[] { ".", "--run", "" }, false)]
    public void Run_or_debug_followed_by_a_flag_an_empty_word_or_nothing_starts_the_configuration_chosen(string[] args, bool debug)
    {
        int at = 1;

        ConfigurationStart start = ConfigurationStart.Read(args, ref at);

        Assert.True(start == new ConfigurationStart(null, debug) && at == 1,
            $"'{string.Join(' ', args)}' would start {start}, read up to '{args[Math.Min(at, args.Length - 1)]}', rather than the configuration chosen");
    }

    [Fact]
    public void A_start_with_no_folder_says_the_folder_comes_before_the_flag()
    {
        Assert.Equal("--run starts a run configuration of the folder Passenger opens, and none is given: passenger FOLDER --run [NAME], the folder first",
            new ConfigurationStart(".", Debug: false).NoFolder);
        Assert.Equal("--debug starts a run configuration of the folder Passenger opens, and none is given: passenger FOLDER --debug [NAME], the folder first",
            new ConfigurationStart(null, Debug: true).NoFolder);
    }

    [Fact]
    public void A_start_that_came_to_nothing_says_why_and_a_failed_builds_first_error()
    {
        Assert.Equal("--run: Greenhouse cannot run: Build failed: 1 error, 0 warnings; Problems lists them (Greenhouse.cs:8:5: Invalid token)",
            new ConfigurationStart("Greenhouse", Debug: false).Cannot("Greenhouse", "Build failed: 1 error, 0 warnings; Problems lists them", "Greenhouse.cs:8:5: Invalid token"));
        Assert.Equal("--debug: Hello cannot debug: it has ended", new ConfigurationStart(null, Debug: true).Cannot("Hello", "it has ended"));
    }

    [Fact]
    public void A_name_no_configuration_has_is_refused_listing_the_folders_configurations()
    {
        string? why = new ConfigurationStart("Nope", Debug: false).WhyNot("hello", ["Hello", "Hello: https", "Worker"]);

        Assert.Equal("--run 'Nope' names no run configuration of hello, whose configurations are 'Hello', 'Hello: https' and 'Worker'", why);
    }

    [Fact]
    public void A_name_is_matched_as_the_toolbar_matches_it_ignoring_letter_case()
    {
        string? why = new ConfigurationStart("hello: HTTPS", Debug: true).WhyNot("hello", ["Hello", "Hello: https"]);

        Assert.True(why is null, $"--debug 'hello: HTTPS' was refused though the folder has Hello: https: {why}");
    }

    [Fact]
    public void A_folder_with_no_configurations_refuses_a_start_with_a_name_or_without()
    {
        Assert.Equal("--debug: hello has no run configuration to debug", new ConfigurationStart(null, Debug: true).WhyNot("hello", []));
        Assert.Equal("--run 'Hello': hello has no run configuration to run", new ConfigurationStart("Hello", Debug: false).WhyNot("hello", []));
        Assert.Null(new ConfigurationStart(null, Debug: false).WhyNot("hello", ["Hello"]));
    }
}
