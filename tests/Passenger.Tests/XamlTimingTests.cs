using System.Diagnostics;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Passenger.App;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What XAML costs the window, held to times, so run alone: completion in a long file answers soon after the
/// key, and reading a project's types (its assemblies' metadata and its own C#) happens off the UI thread, which keeps
/// taking keys meanwhile.</summary>
[Collection(Alone.Name)]
public sealed class XamlTimingTests : IClassFixture<XamlTimingTests.LongView>
{
    private readonly LongView app;

    public XamlTimingTests(LongView app) => this.app = app;

    [AvaloniaFact]
    public void Completion_in_a_five_thousand_line_view_lists_the_controls_soon_after_the_key()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        window.OpenFile(app.PathOf("LongView.axaml"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.True(editor.Document.Snapshot.LineCount > 5000, $"The view has only {editor.Document.Snapshot.LineCount} lines");
        string text = editor.Document.Snapshot.ToString();
        int middle = text.IndexOf("<!-- middle -->", StringComparison.Ordinal) + "<!-- middle -->".Length;
        List<double> times = [];
        for (int i = 0; i < 7; i++)
        {
            editor.Selections = SelectionSet.Single(middle);
            long began = Stopwatch.GetTimestamp();
            window.KeyTextInput("<");
            Ui.PumpUntil(() => window.Assist.CompletionLabels.Contains("Button"), "the controls listed after '<' in the long view");
            times.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, "BACK_SPACE");
            Ui.Settle();
        }
        times.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine($"Completion in {editor.Document.Snapshot.LineCount:N0} lines: median {times[3]:F0} ms, slowest {times[^1]:F0} ms");
        Assert.True(times[3] < 250, $"Completion in the long view took a median {times[3]:F0} ms from the key to the list ({string.Join(", ", times.Select(t => $"{t:F0}"))} ms)");
        window.Close();
    }

    [AvaloniaFact]
    public void Reading_passengers_own_types_for_its_xaml_leaves_the_window_taking_keys()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        Ui.Settle();
        Dispatcher dispatcher = Dispatcher.UIThread;
        List<double> waits = [];
        // A key's wait, as the system would deliver one: a job at input's priority, posted from another thread now and then.
        using Timer keys = new(_ =>
        {
            long posted = Stopwatch.GetTimestamp();
            dispatcher.Post(() =>
            {
                lock (waits)
                {
                    waits.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                }
            }, DispatcherPriority.Input);
        }, null, 0, 10);
        double Worst()
        {
            lock (waits)
            {
                double worst = waits.Count == 0 ? 0 : waits.Max();
                waits.Clear();
                return worst;
            }
        }
        Ui.Hold(TimeSpan.FromSeconds(1), "a second of the window at rest, against which the keys' waits while loading are held");
        double atRest = Worst();

        window.OpenFile(Path.Combine(XamlTests.Repository, "src", "Passenger.App", "Theme", "Shell.axaml"));
        XamlDocument xaml = window.XamlService.For(window.EditorArea.Active!) ?? throw new InvalidOperationException("Shell.axaml was given no XAML");
        long loading = Stopwatch.GetTimestamp();
        Ui.PumpUntil(() => xaml.Loaded.IsCompleted, "Passenger.App's types read for its XAML", TimeSpan.FromMinutes(2));
        double load = Stopwatch.GetElapsedTime(loading).TotalMilliseconds;
        double whileLoading = Worst();
        Assert.True(xaml.Loaded.IsCompletedSuccessfully, $"Reading Passenger.App's types failed: {xaml.Loaded.Exception}");
        TestContext.Current.TestOutputHelper?.WriteLine($"Passenger.App's XAML read in {load:F0} ms; a key waited up to {whileLoading:F0} ms meanwhile, {atRest:F0} ms at rest");
        Assert.True(whileLoading < Math.Max(250, 10 * atRest),
            $"A key waited up to {whileLoading:F0} ms while Passenger.App's types were read ({load:F0} ms), against {atRest:F0} ms at rest");

        CodeEditor editor = window.EditorArea.CodeEditor;
        int selector = editor.Document.Snapshot.ToString().IndexOf("<Style Selector=\"", StringComparison.Ordinal) + "<Style Selector=\"".Length;
        editor.Selections = SelectionSet.Single(selector);
        long asked = Stopwatch.GetTimestamp();
        Ui.Run(window, AppActions.Complete);
        Ui.PumpUntil(() => window.Assist.CompletionLabels.Contains("Button"), "the controls a style can select");
        double first = Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
        TestContext.Current.TestOutputHelper?.WriteLine($"The first completion once read: {first:F0} ms");
        Assert.True(first < 1000, $"The first completion after the types were read took {first:F0} ms");
        window.Close();
    }

    /// <summary>The template's app with a view of more than five thousand lines: a thousand bordered rows of text and
    /// buttons, a marker in the middle where completion is asked for.</summary>
    public sealed class LongView : IAsyncLifetime
    {
        public LongView()
        {
            Folder = TestFolders.Create("xaml-long-");
        }

        public ValueTask InitializeAsync() => new(TestFolders.PrepareAsync(async () =>
        {
            await AvaloniaApp.CreateAsync(Folder, "Long");
            StringBuilder view = new();
            view.Append("<UserControl xmlns=\"https://github.com/avaloniaui\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n             x:Class=\"Long.LongView\">\n    <StackPanel>\n");
            for (int i = 0; i < 1000; i++)
            {
                view.Append($"        <Border Name=\"Row{i}\" Padding=\"4\" Background=\"#20{i % 256:X2}40FF\" CornerRadius=\"2\">\n")
                    .Append("            <StackPanel Orientation=\"Horizontal\" Spacing=\"6\">\n")
                    .Append($"                <TextBlock Text=\"Row {i}\" FontWeight=\"Bold\" VerticalAlignment=\"Center\" />\n")
                    .Append($"                <Button Content=\"Open {i}\" HorizontalAlignment=\"Right\" />\n")
                    .Append("            </StackPanel>\n")
                    .Append("        </Border>\n");
                if (i == 500)
                {
                    view.Append("        <!-- middle -->\n");
                }
            }
            view.Append("    </StackPanel>\n</UserControl>\n");
            File.WriteAllText(PathOf("LongView.axaml"), view.ToString());
            File.WriteAllText(PathOf("LongView.axaml.cs"), "using Avalonia.Controls;\n\nnamespace Long;\n\npublic partial class LongView : UserControl\n{\n    public LongView()\n    {\n        InitializeComponent();\n    }\n}\n");
            await Restore.RunAsync(Folder);
        }, Folder));

        public string Folder { get; }

        public string PathOf(string name) => Path.Combine(Folder, name);

        public ValueTask DisposeAsync()
        {
            TestFolders.Delete(Folder);
            return ValueTask.CompletedTask;
        }
    }
}
