using System.Text.Json;
using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.Core;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Agents testing a WPF app Passenger runs, through the same tools as an Avalonia app's: tests/Debuggees/WpfTreeApp
/// (a counter's button, a name box that greets on Enter, a misspelt binding, a drop-down and a list to scroll), written into a
/// folder of the test's own and restored from the run's feed. WPF runs on Windows alone, so elsewhere the test skips.</summary>
public sealed partial class McpTests
{
    /// <summary>Folders a test made for itself, removed with the class once the Passengers that worked in them have gone.</summary>
    private readonly List<string> madeFolders = [];

    [Theory]
    [InlineData("None")]
    [InlineData("Slide")]
    [InlineData("Scroll")]
    public async Task An_agent_runs_a_wpf_app_sees_it_clicks_its_button_types_into_it_and_reads_its_failed_bindings(string animation)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string wpf = WpfInputFolder($$"""
                Loaded += (_, _) =>
                {
                    Colour.ApplyTemplate();
                    var popup = (System.Windows.Controls.Primitives.Popup)Colour.Template.FindName("PART_Popup", Colour);
                    popup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.{{animation}};
                };
                """);
        // What the inspector in the app does is traced to a file a failure quotes; the Passenger started inherits the variable.
        string trace = Path.Combine(wpf, "inspector.log");
        string? traceBefore = Environment.GetEnvironmentVariable("PASSENGER_INSPECTOR_TRACE");
        Environment.SetEnvironmentVariable("PASSENGER_INSPECTOR_TRACE", trace);
        McpClient started;
        try
        {
            started = await Start(["--mcp", wpf]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PASSENGER_INSPECTOR_TRACE", traceBefore);
        }
        await using McpClient client = started;
        JsonElement built = await Call(client, "build");
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        JsonElement ran = await Call(client, "run", new() { ["waitSeconds"] = 0 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The app ended as it started: {ran}");

        // Its one window, as WPF has it: its title, its client area in units, WPF named.
        JsonElement windows = await Call(client, "app_windows");
        Assert.StartsWith("WPF ", windows.GetProperty("framework").GetString(), StringComparison.Ordinal);
        Assert.True(windows.GetProperty("windows").GetArrayLength() == 1,
            $"The app's windows are {windows}; the inspector traced:\n{(File.Exists(trace) ? Ui.Shared(trace) : "(nothing)")}");
        JsonElement main = windows.GetProperty("windows")[0];
        Assert.Equal("WPF Tree App", main.GetProperty("title").GetString());

        // A picture the app drew of it, at its own scaling.
        CallToolResult picture = await Result(client, "app_screenshot");
        Assert.True(picture.IsError != true && picture.Content.OfType<ImageContentBlock>().Count() == 1,
            $"The app drew no picture: {Text(picture)}; the inspector traced:\n{(File.Exists(trace) ? Ui.Shared(trace) : "(nothing)")}");
        ImageContentBlock image = picture.Content.OfType<ImageContentBlock>().Single();
        double scaling = main.GetProperty("scaling").GetDouble();
        (int width, int height) = PngSize(image.DecodedData.ToArray());
        Assert.True(Math.Abs(width - (main.GetProperty("width").GetDouble() * scaling)) <= 1 && Math.Abs(height - (main.GetProperty("height").GetDouble() * scaling)) <= 1,
            $"The picture is {width}x{height} pixels for a window of {main}");
        Assert.True(image.DecodedData.Length > 4000, $"The app's picture is {image.DecodedData.Length} bytes, as a blank one would be");

        // The button found in the tree, clicked through UI Automation, and the text it changes.
        Assert.Contains("Button#Count \"Count\"", Text(await Call(client, "app_tree", new() { ["filter"] = "Button" })), StringComparison.Ordinal);
        string clicked = Text(await Call(client, "app_click", new() { ["control"] = "#Count" }));
        Assert.StartsWith("Clicked Button#Count @", clicked, StringComparison.Ordinal);
        Assert.Contains("\"Clicked once\"", Text(await Call(client, "app_tree", new() { ["control"] = "#Counter" })), StringComparison.Ordinal);

        // Typed into the name box and Enter pressed, its handler greets; replace types over what is there.
        Assert.Contains("it shows \"Ada\"", Text(await Call(client, "app_type", new() { ["control"] = "#NameBox", ["text"] = "Ada" })), StringComparison.Ordinal);
        await Call(client, "app_key", new() { ["keys"] = "Enter" });
        Assert.Contains("\"Hello, Ada\"", Text(await Call(client, "app_tree", new() { ["control"] = "#Hello" })), StringComparison.Ordinal);
        Assert.Contains("it shows \"Grace\"", Text(await Call(client, "app_type", new() { ["control"] = "#NameBox", ["text"] = "Grace", ["replace"] = true })), StringComparison.Ordinal);

        // A drop-down opens its list as a popup over the window, where the agent picks from it.
        await Call(client, "app_click", new() { ["control"] = "#Colour" });
        string picked = Text(await Call(client, "app_click", new() { ["control"] = "ComboBoxItem \"Blue\"" }));
        string colour = Text(await Call(client, "app_tree", new() { ["filter"] = "#Colour" }));
        Assert.True(colour.Contains("ComboBox#Colour \"Blue\"", StringComparison.Ordinal), $"Blue, clicked in the drop-down ({picked}), is not chosen: {colour}");
        CallToolResult chosen = await Result(client, "app_screenshot", new() { ["scale"] = 2 });
        Assert.True(chosen.IsError != true, Text(chosen));
        string captures = Path.Combine(AppContext.BaseDirectory, "wpf-popup");
        Directory.CreateDirectory(captures);
        File.WriteAllBytes(Path.Combine(captures, animation + "-selected.png"), chosen.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());

        // A genuinely clipped item still fails rather than bypassing the viewport guard after settling the popup.
        CallToolResult hidden = await Result(client, "app_click", new() { ["control"] = "ListBoxItem \"Item 6\"" });
        Assert.True(hidden.IsError == true, "A click on a clipped WPF item was sent to whatever is there");
        Assert.Contains("scrolled out of view inside ScrollViewer", Text(hidden), StringComparison.Ordinal);

        // The wheel over the list scrolls it.
        Assert.Contains("ScrollViewer", Text(await Call(client, "app_scroll", new() { ["control"] = "#List", ["dy"] = 3 })), StringComparison.Ordinal);

        // The misspelt binding, once, at its place in the XAML with the member nearest.
        JsonElement failed = Assert.Single((await Call(client, "app_binding_errors")).EnumerateArray());
        Assert.Equal(("TextBlock#Caption", "Nmae", "Name"), (failed.GetProperty("control").GetString(), failed.GetProperty("path").GetString(),
            failed.GetProperty("nearest").EnumerateArray().Single().GetString()));
        Assert.StartsWith("MainWindow.xaml:", failed.GetProperty("place").GetString(), StringComparison.Ordinal);

        await Call(client, "stop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_popup_that_stays_displaced_is_refused_without_queuing_a_later_click_or_blocking_other_commands(bool animated)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string move = animated
            ? "shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(-200, -200, new System.Windows.Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });"
            : "shift.Y = -200;";
        string wpf = WpfInputFolder($$"""
            Loaded += (_, _) =>
            {
                Colour.ApplyTemplate();
                var popup = (System.Windows.Controls.Primitives.Popup)Colour.Template.FindName("PART_Popup", Colour);
                popup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Slide;
                bool displaced = true;
                popup.Opened += (_, _) =>
                {
                    if (!displaced) return;
                    var shift = new System.Windows.Media.TranslateTransform();
                    popup.Child.RenderTransform = shift;
                    {{move}}
                };
                Delete.Click += (_, _) =>
                {
                    displaced = false;
                    if (popup.Child.RenderTransform is System.Windows.Media.TranslateTransform shift)
                    {
                        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                        shift.Y = 0;
                    }
                };
            };
            """);
        await using McpClient client = await Start(["--mcp", wpf]);
        Assert.True((await Call(client, "build")).GetProperty("succeeded").GetBoolean());
        Assert.True((await Call(client, "run", new() { ["waitSeconds"] = 0 })).GetProperty("stillRunning").GetBoolean());
        await Call(client, "app_click", new() { ["control"] = "#Colour" });
        Stopwatch took = Stopwatch.StartNew();
        CallToolResult refused = await Result(client, "app_click", new() { ["control"] = "ComboBoxItem \"Blue\"" });
        Assert.True(refused.IsError == true, Text(refused));
        Assert.Contains(animated ? "still moving" : "out of view", Text(refused), StringComparison.Ordinal);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(4), $"A permanently displaced popup held a command for {took.Elapsed}");
        Assert.Contains("ComboBox#Colour \"Red\"", Text(await Call(client, "app_tree", new() { ["filter"] = "#Colour" })), StringComparison.Ordinal);
        await Call(client, "app_click", new() { ["control"] = "#Delete" });
        Assert.Contains("ComboBox#Colour \"Red\"", Text(await Call(client, "app_tree", new() { ["filter"] = "#Colour" })), StringComparison.Ordinal);
        // The refused request cannot wake up and click later when the application's button restores the popup.
        await Call(client, "app_key", new() { ["keys"] = "Escape" });
        await Call(client, "app_click", new() { ["control"] = "#Colour" });
        await Call(client, "app_click", new() { ["control"] = "ComboBoxItem \"Blue\"" });
        Assert.Contains("ComboBox#Colour \"Blue\"", Text(await Call(client, "app_tree", new() { ["filter"] = "#Colour" })), StringComparison.Ordinal);
        await Call(client, "stop");
    }

    private string WpfInputFolder(string startup)
    {
        string wpf = PathNames.Canonical(TestFolders.Create("mcpwpf-"));
        madeFolders.Add(wpf);
        TestFolders.Prepare(() =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("WpfTreeApp")))
            {
                File.Copy(file, Path.Combine(wpf, Path.GetFileName(file)));
            }
            string code = Path.Combine(wpf, "MainWindow.xaml.cs");
            File.WriteAllText(code, File.ReadAllText(code).Replace("InitializeComponent();", "InitializeComponent();\n" + startup, StringComparison.Ordinal));
            string xaml = Path.Combine(wpf, "MainWindow.xaml");
            File.WriteAllText(xaml, File.ReadAllText(xaml).Replace("<ListBox x:Name=\"List\"", "<ListBox VirtualizingPanel.IsVirtualizing=\"False\" x:Name=\"List\"", StringComparison.Ordinal));
            Restore.Run(WpfApp.Project(wpf, "WpfTreeApp"));
        }, wpf);
        return wpf;
    }
}
