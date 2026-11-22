using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Debugging;
using Passenger.Dap;

namespace Passenger.UnitTests.Debugger;

/// <summary>What .NET's browser debug proxy says of a stop and of values, as the Debug window shows them: a stop's .NET frames
/// alone, the browser's own passed over; values in C#'s words and types; a breakpoint's file matched whole by its pattern.</summary>
public sealed class WebAssemblyValuesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void What_the_browser_debug_proxy_says_of_a_stop_and_its_values_shows_as_the_Debug_window_shows_a_programs() => Cases.All(
        ("a stop's frames are its dotnet frames with their file place and locals the browser's own passed over", () =>
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
        }),
        ("values show as C# writes them with C#'s type names and only objects open", () =>
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
        }),
        ("a breakpoint's pattern matches its file whole by either separator and in any case only where asked", () =>
        {
            Regex either = new(WebAssemblyValues.PatternOf("/app/Pages/Counter.razor", ignoreCase: true));
            Assert.True(either.IsMatch("/app/Pages/Counter.razor") && either.IsMatch(@"\APP\pages\counter.razor"), "The pattern missed its file named another way");
            Assert.False(either.IsMatch("/app/Pages/Counter.razor.cs") || either.IsMatch("/other/app/Pages/Counter.razor"), "The pattern matched another file");
            Assert.False(new Regex(WebAssemblyValues.PatternOf("/app/Pages/Counter.razor", ignoreCase: false)).IsMatch("/app/pages/Counter.razor"),
                "A file system that tells case apart had another file's breakpoint");
        }),
        ("a tracepoint's string is quoted unless nq and its whole number is as wide as its type under h", () =>
        {
            JsonElement name = Json("""{"type":"string","value":"Ada","description":"Ada","objectId":"dotnet:object:5"}""");
            Assert.Equal(("\"Ada\"", "Ada"), (DisplayFormat.Shown(WebAssemblyValues.Displayed(name), []), DisplayFormat.Shown(WebAssemblyValues.Displayed(name), ["nq"])));
            Assert.Equal(("14", "0x0000000e"), (Shown("""{"type":"number","value":14,"description":"14","className":"Int32"}""", []),
                Shown("""{"type":"number","value":14,"description":"14","className":"Int32"}""", ["h"])));
            Assert.Equal("0x000000000000000e", Shown("""{"type":"number","value":14,"description":"14","className":"System.Int64"}""", ["h"]));
            Assert.Equal("0xffffffff", Shown("""{"type":"number","value":-1,"description":"-1","className":"Object"}""", ["h"]));
            Assert.Equal("3", Shown("""{"type":"number","value":3,"description":"3","className":"Double"}""", ["h"]));
            Assert.Equal("2.5", Shown("""{"type":"number","value":2.5,"description":"2.5"}""", ["h"]));

            static string Shown(string value, string[] specifiers) => DisplayFormat.Shown(WebAssemblyValues.Displayed(Json(value)), specifiers);
        }));

}
