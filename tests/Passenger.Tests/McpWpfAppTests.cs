using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Agents testing a WPF app Passenger runs: tests/Debuggees/WpfTreeApp (a counter's button, a name box that greets
/// on Enter, a misspelt binding, a drop-down and a list to scroll), written into a folder of the test's own and restored
/// from the run's feed. WPF runs on Windows alone, so elsewhere the test skips.</summary>
[Collection(Alone.Name)]
public sealed class McpWpfAppTests : McpRig
{
    [Fact]
    public async Task An_agent_runs_a_wpf_app_sees_it_clicks_its_button_types_into_it_and_reads_its_failed_bindings()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string wpf = PathNames.Canonical(TestFolders.Create("mcpwpf-"));
        DeleteAtEnd(wpf);
        TestFolders.Prepare(() =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("WpfTreeApp")))
            {
                File.Copy(file, Path.Combine(wpf, Path.GetFileName(file)));
            }
            Restore.Run(WpfApp.Project(wpf, "WpfTreeApp"));
        }, wpf);
        // The inspector traces to a file a failure quotes: the variable is the started Passenger's, never this process's.
        string trace = Path.Combine(wpf, "inspector.log");
        await using McpClient client = await Start(["--mcp", wpf], environment: new() { [Passenger.Inspector.Wire.TraceVariable] = trace }, languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 0 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The app ended as it started: {ran}");

        JsonElement windows = await Call(client, McpTools.AppWindows);
        Assert.StartsWith("WPF ", windows.GetProperty("framework").GetString(), StringComparison.Ordinal);
        Assert.True(windows.GetProperty("windows").GetArrayLength() == 1,
            $"The app's windows are {windows}; the inspector traced:\n{(File.Exists(trace) ? Ui.Shared(trace) : "(nothing)")}");
        JsonElement main = windows.GetProperty("windows")[0];
        Assert.Equal("WPF Tree App", main.GetProperty("title").GetString());

        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        Assert.True(picture.IsError != true && picture.Content.OfType<ImageContentBlock>().Count() == 1,
            $"The app drew no picture: {Text(picture)}; the inspector traced:\n{(File.Exists(trace) ? Ui.Shared(trace) : "(nothing)")}");
        ImageContentBlock image = picture.Content.OfType<ImageContentBlock>().Single();
        double scaling = main.GetProperty("scaling").GetDouble();
        (int width, int height) = PngSize(image.DecodedData.ToArray());
        Assert.True(Math.Abs(width - (main.GetProperty("width").GetDouble() * scaling)) <= 1 && Math.Abs(height - (main.GetProperty("height").GetDouble() * scaling)) <= 1,
            $"The picture is {width}x{height} pixels for a window of {main}");
        Assert.True(image.DecodedData.Length > 4000, $"The app's picture is {image.DecodedData.Length} bytes, as a blank one would be");

        Assert.Contains("Button#Count \"Count\"", Text(await Call(client, McpTools.AppTree, new() { ["filter"] = "Button" })), StringComparison.Ordinal);
        string clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Count" }));
        Assert.StartsWith("Clicked Button#Count @", clicked, StringComparison.Ordinal);
        Assert.Contains("\"Clicked once\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);

        JsonElement button = await Call(client, McpTools.AppDetails, new() { ["control"] = "#Count" });
        JsonElement margin = Assert.Single(button.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "Margin");
        Assert.True(margin.GetProperty("source").GetString() == "Style" && margin.GetProperty("place").GetString()!.StartsWith("App.xaml:", StringComparison.Ordinal),
            $"The button's margin did not come from App.xaml's style: {margin}");
        JsonElement[] styles = [.. button.GetProperty("styles").EnumerateArray()];
        Assert.Contains(styles, s => s.GetProperty("selector").GetString() == "Style TargetType=Button" && s.GetProperty("applies").GetBoolean());
        Assert.Contains(styles, s => s.GetProperty("selector").GetString() == "Trigger IsMouseOver=True" && !s.GetProperty("applies").GetBoolean()
            && s.GetProperty("whyNot").GetString() == "IsMouseOver is False, not True");

        Assert.Contains("it shows \"Ada\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#NameBox", ["text"] = "Ada" })), StringComparison.Ordinal);
        await Call(client, McpTools.AppKey, new() { ["keys"] = "Enter" });
        Assert.Contains("\"Hello, Ada\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Hello" })), StringComparison.Ordinal);
        Assert.Contains("it shows \"Grace\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#NameBox", ["text"] = "Grace", ["replace"] = true })), StringComparison.Ordinal);

        await Call(client, McpTools.AppClick, new() { ["control"] = "#Colour" });
        string picked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "ComboBoxItem \"Blue\"" }));
        string colour = Text(await Call(client, McpTools.AppTree, new() { ["filter"] = "#Colour" }));
        Assert.True(colour.Contains("ComboBox#Colour \"Blue\"", StringComparison.Ordinal), $"Blue, clicked in the drop-down ({picked}), is not chosen: {colour}");

        Assert.Contains("ScrollViewer", Text(await Call(client, McpTools.AppScroll, new() { ["control"] = "#List", ["dy"] = 3 })), StringComparison.Ordinal);

        JsonElement failed = Assert.Single((await Call(client, McpTools.AppBindingErrors)).EnumerateArray());
        Assert.Equal(("TextBlock#Caption", "Nmae", "Name"), (failed.GetProperty("control").GetString(), failed.GetProperty("path").GetString(),
            failed.GetProperty("nearest").EnumerateArray().Single().GetString()));
        Assert.StartsWith("MainWindow.xaml:", failed.GetProperty("place").GetString(), StringComparison.Ordinal);

        await Call(client, McpTools.Stop);
    }
}
