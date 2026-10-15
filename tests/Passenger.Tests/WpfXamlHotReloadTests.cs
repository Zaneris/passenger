using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Inspector;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>WPF's existing objects updated through the window's Apply Changes and the real WPF inspector, with the
/// bindings, namescope, user input, code-behind and repeated view instances intact. WPF runs on Windows alone.</summary>
[Collection(Alone.Name)]
public sealed class WpfXamlHotReloadTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder = string.Empty;

    public WpfXamlHotReloadTests(RestoredFolders folders)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        folder = PathNames.Canonical(folders.Take("wpfxamlreload-", made =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("WpfTreeApp")))
            {
                File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
            }
            File.WriteAllText(Path.Combine(made, "MainWindow.xaml"), View);
            File.WriteAllText(Path.Combine(made, "MainWindow.xaml.cs"), Code);
            foreach (string name in new[] { "RepeatedView", "UnopenedView" })
            {
                File.WriteAllText(Path.Combine(made, name + ".xaml"), $"<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"WpfTreeApp.{name}\">\n    <TextBlock Text=\"Repeated before\" />\n</UserControl>\n");
                File.WriteAllText(Path.Combine(made, name + ".xaml.cs"), $"using System.Windows.Controls;\nnamespace WpfTreeApp;\npublic partial class {name} : UserControl {{ public {name}() => InitializeComponent(); }}\n");
            }
            return WpfApp.Project(made, "WpfTreeApp");
        }));
    }

    public void Dispose()
    {
        if (folder.Length > 0)
        {
            TestFolders.Delete(folder);
        }
    }

    private const string View = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:WpfTreeApp"
                x:Class="WpfTreeApp.MainWindow"
                Title="WPF XAML Reload" Width="640" Height="460" Background="WhiteSmoke" Foreground="Black">
            <StackPanel Margin="12">
                <TextBlock Text="Before" />
                <TextBox x:Name="Entry" Text="{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
                <TextBlock x:Name="Bound" Text="{Binding Name}" />
                <Button x:Name="Increment" Content="Count" Click="Clicked" />
                <TextBlock x:Name="Count" Text="{Binding Count}" />
                <Grid>
                    <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="Auto" /></Grid.RowDefinitions>
                    <TextBlock Text="Attached" Grid.Row="0" />
                </Grid>
                <local:RepeatedView />
                <local:RepeatedView />
            </StackPanel>
        </Window>
        """;

    private const string Code = """
        using System.ComponentModel;
        using System.Windows;
        namespace WpfTreeApp;
        public partial class MainWindow : Window
        {
            public MainWindow() { InitializeComponent(); DataContext = new State(); }
            private void Clicked(object sender, RoutedEventArgs e)
            {
                State state = (State)DataContext;
                state.Count++;
                Console.WriteLine($"Name={state.Name}; Count={state.Count}; Named={FindName("Entry") == Entry}");
            }
        }
        public class State : INotifyPropertyChanged
        {
            private string name = "Original state";
            private int count;
            public event PropertyChangedEventHandler? PropertyChanged;
            public string Name { get => name; set { name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
            public int Count { get => count; set { count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
        }
        """;

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Literal_edits_update_the_existing_wpf_controls_and_both_view_instances_while_preserving_state(string theme)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        if (theme == "Dark")
        {
            File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), View.Replace("Background=\"WhiteSmoke\" Foreground=\"Black\"", "Background=\"#222222\" Foreground=\"White\"", StringComparison.Ordinal));
        }
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        List<Element> before = Read(app);
        Element entry = before.Single(e => e.Name == "Entry");
        Element button = before.Single(e => e.Name == "Increment");
        Element caption = before.Single(e => e.Type == "TextBlock" && e.Text == "Before");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry.Id, Text = "User state", Replace = true }), "the WPF text box to receive input");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "the WPF button's event handler");
        Ui.WaitFor(() => Output(window).Contains("Name=User state; Count=1; Named=True", StringComparison.Ordinal), "the original WPF viewmodel and namescope");
        Capture(app, theme + "-before");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"After\"");
        Typing.Replace(window, "Grid.Row=\"0\"", "Grid.Row=\"1\"");
        Typing.Replace(window, "Title=\"WPF XAML Reload\"", "Title=\"WPF XAML Reloaded\"");
        HotReloadOutcome applied = Apply(window);
        Assert.True(applied.Result == HotReloadResult.Applied, applied.Said);
        List<Element> after = Read(app);
        Assert.Equal("After", after.Single(e => e.Id == caption.Id).Text);
        Assert.Equal(entry.Id, after.Single(e => e.Name == "Entry").Id);
        Assert.Equal("User state", after.Single(e => e.Name == "Entry").Text);
        Assert.Equal("User state", after.Single(e => e.Name == "Bound").Text);
        Assert.Equal("1", after.Single(e => e.Text == "Attached").Values!["Grid.Row"]);
        Capture(app, theme + "-after");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry.Id, Text = "Later state", Replace = true }), "the original WPF binding to receive another edit");
        Assert.Equal("Later state", Read(app).Single(e => e.Name == "Bound").Text);
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "the original WPF handler to run again");
        Ui.WaitFor(() => Output(window).Contains("Name=Later state; Count=2; Named=True", StringComparison.Ordinal), "the WPF namescope, viewmodel and handler to survive");
        Typing.Replace(window, "<TextBlock Text=\"After\" />", "\n<TextBlock Text=\"After again\" />");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.Equal("After again", Read(app).Single(e => e.Id == caption.Id).Text);
        long[] repeated = [.. Read(app).Where(e => e.Type == "TextBlock" && e.Text == "Repeated before").Select(e => e.Id)];
        Assert.Equal(2, repeated.Length);
        Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
        Typing.Replace(window, "Repeated before", "Repeated after");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.All(repeated, id => Assert.Equal("Repeated after", Read(app).Single(e => e.Id == id).Text));
        End(window);
    }

    [AvaloniaFact]
    public void Wpf_conversion_failure_has_a_file_and_line_and_applies_no_properties_until_the_edit_is_fixed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"Must not apply\"");
        Typing.Replace(window, "Width=\"640\"", "Width=\"not a number\"");
        HotReloadOutcome failed = Apply(window);
        Assert.Equal(HotReloadResult.Errors, failed.Result);
        Assert.Contains(failed.Problems, p => p.Path == Path.Combine(folder, "MainWindow.xaml") && p.Position.Line == 4 && p.Message.Contains("Width", StringComparison.Ordinal));
        Assert.Contains(Read(app), e => e.Text == "Before");
        Assert.True(window.Debugger.HotReload!.Changed);
        Typing.Replace(window, "not a number", "700");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.Contains(Read(app), e => e.Text == "Must not apply");
        End(window);
    }

    [AvaloniaFact]
    public void Wpf_wrong_source_unused_view_and_unsupported_structure_refuse_without_mutating_any_live_instance()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        XamlUpdateResult wrong = Ui.Await(app.ApplyXamlAsync(new XamlUpdate([new XamlSourceUpdate(Path.Combine(folder, "Other", "MainWindow.xaml"), View,
            View.Replace("Text=\"Before\"", "Text=\"Wrong view\"", StringComparison.Ordinal))])), "the unrelated WPF source to be refused");
        Assert.False(wrong.Applied);
        Assert.All(wrong.Problems, p => Assert.True(p.RestartRequired));
        Assert.Contains(Read(app), e => e.Text == "Before");
        Assert.Null(window.OpenFile(Path.Combine(folder, "UnopenedView.xaml")));
        Typing.Replace(window, "Repeated before", "No instance");
        HotReloadOutcome absent = Ui.Await(window.Debugger.ApplyChangesAsync(), "the unused WPF source to be classified");
        Assert.Equal(HotReloadResult.RestartNeeded, absent.Result);
        Assert.Contains(absent.Problems, p => p.Path == Path.Combine(folder, "UnopenedView.xaml") && p.Message.Contains("live instance", StringComparison.Ordinal));
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
        Typing.Replace(window, "<TextBlock Text=\"Before\" />", "<TextBlock Text=\"Must not apply\" /><TextBlock Text=\"Added\" />");
        HotReloadOutcome structure = Ui.Await(window.Debugger.ApplyChangesAsync(), "the structural WPF edit to be classified");
        Assert.Equal(HotReloadResult.RestartNeeded, structure.Result);
        Assert.Contains(structure.Problems, p => p.Path == Path.Combine(folder, "MainWindow.xaml") && p.Message.Contains("elements", StringComparison.Ordinal));
        Assert.Contains(Read(app), e => e.Text == "Before");
        End(window);
    }

    private MainWindow Debugged()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        string? failed = Ui.Await(window.DebugAsync(), "the WPF XAML app to build and start", TimeSpan.FromMinutes(4));
        Assert.True(failed is null, $"{failed}\n{window.BottomPanel.Build.Document.Snapshot}");
        Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected && window.LiveTree.Rows.Any(r => r.Element.Name == "Entry"), "the WPF live controls", TimeSpan.FromMinutes(2));
        return window;
    }

    private static HotReloadOutcome Apply(MainWindow window)
    {
        HotReloadOutcome? before = window.Debugger.LastApplied;
        Ui.Run(window, AppActions.ApplyHotReloadChanges);
        Ui.WaitFor(() => window.Debugger.LastApplied is { } now && !ReferenceEquals(before, now), "the WPF edits to be applied", TimeSpan.FromMinutes(1));
        return window.Debugger.LastApplied!;
    }

    private static List<Element> Read(LiveApp app) => [.. Ui.Await(app.AgentTreeAsync(["Grid.Row"]), "the WPF app's tree").Roots.SelectMany(Flatten)];

    private static IEnumerable<Element> Flatten(Element element) => new[] { element }.Concat(element.Children.SelectMany(Flatten));

    private static string Output(MainWindow window) => window.BottomPanel.Debug.Console.Editor.Document.Snapshot.ToString();

    private static void Capture(LiveApp app, string name)
    {
        long root = Ui.Await(app.WindowsAsync(), "the WPF window to capture").Single(w => w.Title?.StartsWith("WPF XAML Reload", StringComparison.Ordinal) == true).Id;
        Picture picture = Ui.Await(app.CaptureAsync(root, scale: 2, area: null), "the WPF app's scale-2 frame");
        TestContext.Current.AddAttachment("WPF XAML Hot Reload " + name, picture.Png, "image/png");
        string frames = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "wpf-xaml-hot-reload")).FullName;
        File.WriteAllBytes(Path.Combine(frames, name + ".png"), picture.Png);
    }

    private static void End(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the WPF app to stop");
        window.Close();
    }
}
