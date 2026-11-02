using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Debugging;

namespace Passenger.UnitTests.Debugger;

/// <summary>What .NET's browser debug proxy says of a stop and of values, as the Debug window shows them: a stop's .NET frames
/// alone, the browser's own passed over; values in C#'s words and types; a breakpoint's file matched whole by its pattern.</summary>
public sealed class WebAssemblyValuesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_stops_frames_are_its_dotnet_frames_with_their_file_place_and_locals_the_browsers_own_passed_over()
    {
        string file = OperatingSystem.IsWindows() ? @"C:\app\Pages\Counter.razor" : "/app/Pages/Counter.razor";
        string url = new Uri(file).AbsoluteUri;
        JsonElement paused = Json($$$"""
            {"callFrames":[
              {"functionName":"App.Pages.Counter.OnInitialized","callFrameId":"dotnet:scope:1","location":{"scriptId":"dotnet://197_0","lineNumber":17,"columnNumber":8},
               "url":"{{{url}}}","scopeChain":[{"type":"local","object":{"type":"object","objectId":"dotnet:scope:1"}}]},
              {"callFrameId":"-66.2.2","functionName":"$func1401","location":{"scriptId":"15","lineNumber":0,"columnNumber":478351},"url":"",
               "scopeChain":[{"type":"local","object":{"objectId":"-66.2.19"}}]}],
             "reason":"other","hitBreakpoints":["br:1:17:0"]}
            """);
        WebAssemblyFrame frame = Assert.Single(WebAssemblyValues.Frames(paused));
        Assert.Equal(new WebAssemblyFrame("dotnet:scope:1", "App.Pages.Counter.OnInitialized", file, 17, 8, "dotnet:scope:1"), frame);
    }

    [Fact]
    public void Values_show_as_CSharp_writes_them_with_CSharps_type_names_and_only_objects_open()
    {
        JsonElement number = Json("""{"type":"number","value":41,"description":"41","className":"Int32"}""");
        Assert.Equal(("41", "int"), (WebAssemblyValues.Shown(number), WebAssemblyValues.TypeOf(number)));
        JsonElement text = Json("""{"type":"string","value":"say \"hi\"\n","description":"say \"hi\"\n","objectId":"dotnet:object:5"}""");
        Assert.Equal(("\"say \\\"hi\\\"\\n\"", "string"), (WebAssemblyValues.Shown(text), WebAssemblyValues.TypeOf(text)));
        Assert.Null(WebAssemblyValues.Inside(text));
        JsonElement component = Json("""{"type":"object","value":null,"description":"App.Pages.Counter","className":"App.Pages.Counter","objectId":"dotnet:object:2"}""");
        Assert.Equal(("App.Pages.Counter", "App.Pages.Counter", "dotnet:object:2"),
            (WebAssemblyValues.Shown(component), WebAssemblyValues.TypeOf(component), WebAssemblyValues.Inside(component)));
        JsonElement none = Json("""{"type":"object","subtype":"null","value":null,"className":"App.Pages.Counter","description":"App.Pages.Counter"}""");
        Assert.Equal("null", WebAssemblyValues.Shown(none));
        Assert.Null(WebAssemblyValues.Inside(none));
        Assert.Null(WebAssemblyValues.TypeOf(Json("""{"type":"number","value":1.0,"description":"1","className":"Object"}""")));
        Assert.Equal("bool", WebAssemblyValues.TypeOf(Json("""{"type":"boolean","value":true,"description":"true","className":"System.Boolean"}""")));
    }

    [Fact]
    public void A_breakpoints_pattern_matches_its_file_whole_by_either_separator_and_in_any_case_only_where_asked()
    {
        Regex either = new(WebAssemblyValues.PatternOf("/app/Pages/Counter.razor", ignoreCase: true));
        Assert.True(either.IsMatch("/app/Pages/Counter.razor") && either.IsMatch(@"\APP\pages\counter.razor"), "The pattern missed its file named another way");
        Assert.False(either.IsMatch("/app/Pages/Counter.razor.cs") || either.IsMatch("/other/app/Pages/Counter.razor"), "The pattern matched another file");
        Assert.False(new Regex(WebAssemblyValues.PatternOf("/app/Pages/Counter.razor", ignoreCase: false)).IsMatch("/app/pages/Counter.razor"),
            "A file system that tells case apart had another file's breakpoint");
        Assert.Matches(WebAssemblyValues.PatternOf("/app (1)/Pages/Counter+.razor", ignoreCase: false), "/app (1)/Pages/Counter+.razor");
    }

    [Fact]
    public void A_refused_evaluation_says_the_proxys_words()
    {
        JsonElement details = Json("""{"exception":{"type":"object","subtype":"error","description":"The name nosuch does not exist in the current context","className":"ReferenceError"}}""");
        Assert.Equal("The name nosuch does not exist in the current context", WebAssemblyValues.Failure(details));
    }
}
