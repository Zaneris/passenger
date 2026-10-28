using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What XAML costs the window, counted in bytes rather than timed: completion in a long file reads the file once a
/// key, and reading a project's types (its assemblies' metadata and its own C#) happens off the UI thread, once. Counted
/// over every thread of the process, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class XamlTimingTests : IClassFixture<XamlTimingTests.LongView>
{
    private readonly LongView app;

    public XamlTimingTests(LongView app) => this.app = app;

    /// <summary>Completion after '&lt;' in a view of five thousand lines allocates, over every thread, under one and a half
    /// times what reading the view's text once (its copy and its tree, about 19 bytes a character) costs more than in a
    /// short view: each key parses the text once.</summary>
    [AvaloniaFact]
    public void Completion_in_a_five_thousand_line_view_reads_the_view_once_a_key()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        (long small, int smallLength) = Completions(window, "ShortView.axaml");
        (long large, int largeLength) = Completions(window, "LongView.axaml");
        long perCharacter = (large - small) / (largeLength - smallLength);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Completion allocated {small:N0} bytes in a view of {smallLength:N0} characters, {large:N0} in one of {largeLength:N0}: {perCharacter} bytes a character more");
        Assert.True(perCharacter <= ReadOnce,
            $"Completion in the long view allocated {large:N0} bytes against {small:N0} in a short one, {perCharacter} bytes for each character more: the view was read more than once a key");
        window.Close();
    }

    /// <summary>Reading Passenger.App's types for its XAML runs off the UI thread, which allocates under a quarter of what
    /// the process does meanwhile (about 1 %, its frames); and the first completion once they are read reads none of them
    /// again, allocating under a tenth of what the read did.</summary>
    [AvaloniaFact]
    public void Reading_passengers_own_types_for_its_xaml_leaves_the_window_taking_keys()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        Ui.Settle();
        long uiBefore = GC.GetAllocatedBytesForCurrentThread();
        long allBefore = GC.GetTotalAllocatedBytes(precise: true);
        window.OpenFile(Path.Combine(XamlTests.Repository, "src", "Passenger.App", "Theme", "Shell.axaml"));
        XamlDocument xaml = window.XamlService.For(window.EditorArea.Active!) ?? throw new InvalidOperationException("Shell.axaml was given no XAML");
        Ui.PumpUntil(() => xaml.Loaded.IsCompleted, "Passenger.App's types read for its XAML", TimeSpan.FromMinutes(2));
        long ui = GC.GetAllocatedBytesForCurrentThread() - uiBefore;
        long load = GC.GetTotalAllocatedBytes(precise: true) - allBefore;
        Assert.True(xaml.Loaded.IsCompletedSuccessfully, $"Reading Passenger.App's types failed: {xaml.Loaded.Exception}");
        TestContext.Current.TestOutputHelper?.WriteLine($"Reading Passenger.App's types for its XAML allocated {load / 1024:N0} KB, {ui / 1024:N0} KB of it on the UI thread");
        Assert.True(ui < load / 4, $"Reading Passenger.App's types allocated {ui / 1024:N0} KB on the UI thread of the {load / 1024:N0} KB it took: it read them there");

        CodeEditor editor = window.EditorArea.CodeEditor;
        int selector = editor.Document.Snapshot.ToString().IndexOf("<Style Selector=\"", StringComparison.Ordinal) + "<Style Selector=\"".Length;
        editor.Selections = SelectionSet.Single(selector);
        Ui.Settle();
        long asked = GC.GetTotalAllocatedBytes(precise: true);
        Ui.Run(window, AppActions.Complete);
        Ui.PumpUntil(() => window.Assist.CompletionLabels.Contains("Button"), "the controls a style can select");
        long first = GC.GetTotalAllocatedBytes(precise: true) - asked;
        TestContext.Current.TestOutputHelper?.WriteLine($"The first completion once they were read allocated {first / 1024:N0} KB");
        Assert.True(first < load / 10, $"The first completion after the types were read allocated {first / 1024:N0} KB, against {load / 1024:N0} KB for reading them: it read them again");
        window.Close();
    }

    /// <summary>The most a key's completion may allocate for each character of a view: one read of it and a half.</summary>
    private const long ReadOnce = 28;

    /// <summary>The least completion after '&lt;' at a view's middle allocates over every thread, from the key to the list
    /// shown, of seven; and the view's length.</summary>
    private (long Bytes, int Length) Completions(MainWindow window, string view)
    {
        window.OpenFile(app.PathOf(view));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        string text = editor.Document.Snapshot.ToString();
        int middle = text.IndexOf("<!-- middle -->", StringComparison.Ordinal) + "<!-- middle -->".Length;
        long least = long.MaxValue;
        for (int i = 0; i < 7; i++)
        {
            editor.Selections = SelectionSet.Single(middle);
            Ui.Settle();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            window.KeyTextInput("<");
            Ui.PumpUntil(() => window.Assist.CompletionLabels.Contains("Button"), $"the controls listed after '<' in {view}");
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, "BACK_SPACE");
            Ui.Settle();
        }
        Assert.True(editor.Document.Snapshot.ToString() == text, $"The keys left {view} changed");
        return (least, text.Length);
    }

    /// <summary>The template's app with a view of more than five thousand lines, a thousand bordered rows of text and
    /// buttons, and one of ten such rows, each with a marker in the middle where completion is asked for.</summary>
    public sealed class LongView : IAsyncLifetime
    {
        public LongView()
        {
            Folder = TestFolders.Create("xaml-long-");
        }

        public ValueTask InitializeAsync() => new(TestFolders.PrepareAsync(async () =>
        {
            await AvaloniaApp.CreateAsync(Folder, "Long");
            WriteView("LongView", 1000);
            WriteView("ShortView", 10);
            await Restore.RunAsync(Folder);
        }, Folder));

        public string Folder { get; }

        public string PathOf(string name) => Path.Combine(Folder, name);

        public ValueTask DisposeAsync()
        {
            TestFolders.Delete(Folder);
            return ValueTask.CompletedTask;
        }

        /// <summary>A view of so many rows and its code-behind, the marker after the middle row.</summary>
        private void WriteView(string name, int rows)
        {
            StringBuilder view = new();
            view.Append($"<UserControl xmlns=\"https://github.com/avaloniaui\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n             x:Class=\"Long.{name}\">\n    <StackPanel>\n");
            for (int i = 0; i < rows; i++)
            {
                view.Append($"        <Border Name=\"Row{i}\" Padding=\"4\" Background=\"#20{i % 256:X2}40FF\" CornerRadius=\"2\">\n")
                    .Append("            <StackPanel Orientation=\"Horizontal\" Spacing=\"6\">\n")
                    .Append($"                <TextBlock Text=\"Row {i}\" FontWeight=\"Bold\" VerticalAlignment=\"Center\" />\n")
                    .Append($"                <Button Content=\"Open {i}\" HorizontalAlignment=\"Right\" />\n")
                    .Append("            </StackPanel>\n")
                    .Append("        </Border>\n");
                if (i == rows / 2)
                {
                    view.Append("        <!-- middle -->\n");
                }
            }
            view.Append("    </StackPanel>\n</UserControl>\n");
            File.WriteAllText(PathOf($"{name}.axaml"), view.ToString());
            File.WriteAllText(PathOf($"{name}.axaml.cs"), $"using Avalonia.Controls;\n\nnamespace Long;\n\npublic partial class {name} : UserControl\n{{\n    public {name}()\n    {{\n        InitializeComponent();\n    }}\n}}\n");
        }
    }
}
