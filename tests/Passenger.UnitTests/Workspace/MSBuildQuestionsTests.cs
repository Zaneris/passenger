using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.UnitTests.Workspace;

/// <summary>An MSBuild question of a project is asked one way (its globals, its own properties over them, its framework)
/// and its answer read one way, past whatever else came on MSBuild's output: the warnings a target writes come on its
/// errors, and a task's own words on its output, before or after the answer.</summary>
public sealed class MSBuildQuestionsTests
{
    /// <summary>A target that warns, asked two properties, as dotnet msbuild 11.0.100 answered (its path shortened): the
    /// answer alone on its output, the warning on its errors.</summary>
    private const string Answered = """
        {
          "Properties": {
            "Answer": "42",
            "Computed": "made by the target"
          }
        }

        """;

    private const string Warned = """
        /src/Warns/Warns.csproj(12,5): warning PS0001: A warning before the answer, with braces { and } in it

        """;

    /// <summary>What an in-process task wrote on MSBuild's console, which came on its output before the answer.</summary>
    private const string TaskSaid = "said on the console by a task, { before\n";

    [Fact]
    public void An_MSBuild_answer_is_read_as_MSBuild_writes_it_whatever_lines_come_beside_it() => Cases.All(
        ("an answer is read as MSBuild writes it its warnings on its errors", An_answer_is_read_as_MSBuild_writes_it_its_warnings_on_its_errors),
        ("an answer is read past a task line before it and a warning line after it", An_answer_is_read_past_a_task_line_before_it_and_a_warning_line_after_it),
        ("a property asked alone is read bare after any line before it and one answered empty is none", A_property_asked_alone_is_read_bare_after_any_line_before_it_and_one_answered_empty_is_none),
        ("a run that failed still says what it answered and one that wrote none answered nothing", A_run_that_failed_still_says_what_it_answered_and_one_that_wrote_none_answered_nothing),
        ("a question gives the globals but their framework its own properties over them and its framework", A_question_gives_the_globals_but_their_framework_its_own_properties_over_them_and_its_framework));

    private void An_answer_is_read_as_MSBuild_writes_it_its_warnings_on_its_errors()
    {
        MSBuildAnswer answer = new(new ChildResult(0, Answered, Warned));

        Assert.True(answer.Answered, "MSBuild's answer was not read");
        Assert.Equal("made by the target", answer.Property("Computed"));
        Assert.Contains("warning PS0001", answer.Said.Error, StringComparison.Ordinal);
    }

    private void An_answer_is_read_past_a_task_line_before_it_and_a_warning_line_after_it()
    {
        MSBuildAnswer answer = new(new ChildResult(0, TaskSaid + Answered + Warned, string.Empty));

        Assert.Equal("42", answer.Property("Answer"));
        Assert.Equal(new Dictionary<string, string> { ["Answer"] = "42", ["Computed"] = "made by the target" }, answer.Properties);
        Assert.True(answer.Property("answer") is null, "A property was read by another letter case than MSBuild wrote");
    }

    private void A_property_asked_alone_is_read_bare_after_any_line_before_it_and_one_answered_empty_is_none()
    {
        MSBuildQuestion asked = new([]) { Properties = ["TargetPath"] };
        MSBuildAnswer answer = new(new ChildResult(0, TaskSaid + "/src/App/bin/Debug/net11.0/App.dll\n", Warned), asked.Alone);

        Assert.Equal("TargetPath", asked.Alone);
        Assert.Equal(["-getProperty:TargetPath"], asked.Asking);
        Assert.Equal("/src/App/bin/Debug/net11.0/App.dll", answer.Property("targetpath"));
        Assert.Empty(new MSBuildAnswer(new ChildResult(0, "\n", string.Empty), "TargetPath").Properties);
        Assert.Null(new MSBuildQuestion([]) { Properties = ["TargetPath", "TargetDir"] }.Alone);
    }

    private void A_run_that_failed_still_says_what_it_answered_and_one_that_wrote_none_answered_nothing()
    {
        // The Fail target's run: its error ended it with exit code 1, its answer written all the same.
        MSBuildAnswer failed = new(new ChildResult(1, "{\n  \"Properties\": {\n    \"Answer\": \"42\",\n    \"Computed\": \"\"\n  }\n}\n",
            "/src/Warns/Warns.csproj(19,5): error PS0003: An error here\n"));
        MSBuildAnswer none = new(new ChildResult(1, "MSBUILD : error MSB1009: Project file does not exist.\n", string.Empty));

        Assert.False(failed.Succeeded, "A run whose target failed read as succeeding");
        Assert.Equal(new Dictionary<string, string> { ["Answer"] = "42" }, failed.Properties);
        Assert.False(none.Answered, "A switch's error read as an answer");
        Assert.Empty(none.Items(MSBuildNames.Devices));
        Assert.Null(none.TargetResult(MSBuildNames.GetRequiredWorkloads));
    }

    private void A_question_gives_the_globals_but_their_framework_its_own_properties_over_them_and_its_framework()
    {
        Dictionary<string, string> globals = new(StringComparer.OrdinalIgnoreCase)
        {
            [MSBuildNames.Configuration] = "Release",
            ["targetframework"] = "net11.0",
            ["device"] = "old",
        };
        MSBuildQuestion question = new([MSBuildNames.DeployToDevice, MSBuildNames.ComputeRunArguments])
        {
            Properties = [MSBuildNames.AndroidPackage],
            Items = [MSBuildNames.Devices],
            TargetResults = [MSBuildNames.GetRequiredWorkloads],
            Given = new Dictionary<string, string> { [MSBuildNames.Device] = "emulator-5554" },
        };

        Assert.Equal(["msbuild", "/src/App/App.csproj", "-nologo", "-target:DeployToDevice;ComputeRunArguments", "-getProperty:_AndroidPackage", "-getItem:Devices",
            "-getTargetResult:_GetRequiredWorkloads", "-p:Configuration=Release", "-p:device=emulator-5554", "-p:TargetFramework=net11.0-android"],
            MSBuildQuestions.Arguments("/src/App/App.csproj", "net11.0-android", globals, question));
        Assert.Equal(["msbuild", "/src/App/App.csproj", "-nologo", "-target:Restore", "-p:Configuration=Release", "-p:device=old"],
            MSBuildQuestions.Arguments("/src/App/App.csproj", null, globals, new MSBuildQuestion([MSBuildNames.Restore])));
    }
}
