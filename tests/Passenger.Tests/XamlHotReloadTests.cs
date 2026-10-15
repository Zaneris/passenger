using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>XAML edits applied through the window to an existing debugged Avalonia app: the controls, user input,
/// viewmodel, event handlers and duplicate user-control instances survive, and an unsupported batch applies nothing.</summary>
[Collection(Alone.Name)]
public sealed class XamlHotReloadTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public XamlHotReloadTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("xamlreload-", made =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("LiveTreeApp")))
            {
                File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
            }
            File.WriteAllText(Path.Combine(made, "MainWindow.axaml"), View);
            File.WriteAllText(Path.Combine(made, "MainWindow.axaml.cs"), Code);
            foreach (string name in new[] { "RepeatedView", "UnopenedView" })
            {
                File.WriteAllText(Path.Combine(made, name + ".axaml"), $"<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"LiveTreeApp.{name}\">\n    <TextBlock Text=\"Repeated before\" />\n</UserControl>\n");
                File.WriteAllText(Path.Combine(made, name + ".axaml.cs"), $"using Avalonia.Controls;\nnamespace LiveTreeApp;\npublic partial class {name} : UserControl {{ public {name}() => InitializeComponent(); }}\n");
            }
            string project = Path.Combine(made, "LiveTreeApp.csproj");
            File.WriteAllText(project, AvaloniaApp.HeadlessProject());
            return project;
        }));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private const string View = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:LiveTreeApp"
                x:Class="LiveTreeApp.MainWindow" x:DataType="local:State"
                Title="XAML Reload" Width="640" Height="460">
            <StackPanel Margin="12" Spacing="8">
                <TextBlock Text="Before" />
                <TextBox x:Name="Entry" Text="{Binding Name, Mode=TwoWay}" />
                <TextBlock x:Name="Bound" Text="{Binding Name}" />
                <Button x:Name="Increment" Content="Count" Click="Clicked" />
                <TextBlock x:Name="Count" Text="{Binding Count}" />
                <Grid RowDefinitions="Auto,Auto">
                    <TextBlock Text="Attached" Grid.Row="0" />
                </Grid>
                <local:RepeatedView />
                <local:RepeatedView />
                <local:RetryControl Value="1" />
                <local:ThrowingControl Fragile="Safe" />
            </StackPanel>
        </Window>
        """;

    private const string Code = """
        using System.ComponentModel;
        using System.Globalization;
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Interactivity;
        namespace LiveTreeApp;
        public partial class MainWindow : Window
        {
            public MainWindow() { InitializeComponent(); DataContext = new State(); }
            private void Clicked(object? sender, RoutedEventArgs e)
            {
                State state = (State)DataContext!;
                state.Count++;
                ThrowingControl.ReadThrows = true;
                Console.WriteLine($"Name={state.Name}; Count={state.Count}");
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
        public class RetryControl : Control
        {
            public static readonly StyledProperty<RetryValue?> ValueProperty = AvaloniaProperty.Register<RetryControl, RetryValue?>(nameof(Value));
            public RetryValue? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        }
        public class ThrowingControl : Control
        {
            public static bool ReadThrows;
            private string fragile = "Safe";
            public static readonly DirectProperty<ThrowingControl, string> FragileProperty = AvaloniaProperty.RegisterDirect<ThrowingControl, string>(nameof(Fragile), item => item.Fragile, (item, value) => item.Fragile = value);
            public string Fragile
            {
                get => ReadThrows ? throw new InvalidOperationException("Fragile getter refused this read") : fragile;
                set => SetAndRaise(FragileProperty, ref fragile, value);
            }
        }
        [TypeConverter(typeof(RetryValueConverter))]
        public class RetryValue { public int Value { get; set; } }
        public class RetryValueConverter : TypeConverter
        {
            private static int tried;
            public override bool CanConvertFrom(ITypeDescriptorContext? context, Type source) => source == typeof(string);
            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                string text = (string)value;
                if (text == "2" && ++tried == 2) { throw new FormatException("The app refused the second conversion"); }
                if (text == "3") { Console.WriteLine("Slow conversion"); Thread.Sleep(1000); }
                return new RetryValue { Value = int.Parse(text, CultureInfo.InvariantCulture) };
            }
        }
        """;

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Literal_xaml_edits_change_existing_unnamed_controls_and_keep_user_input_bindings_and_code_behind_alive(string theme)
    {
        string appXaml = Path.Combine(folder, "App.axaml");
        File.WriteAllText(appXaml, File.ReadAllText(appXaml).Replace("RequestedThemeVariant=\"Light\"", $"RequestedThemeVariant=\"{theme}\"", StringComparison.Ordinal));
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        List<Element> before = Read(app);
        Element entry = before.Single(e => e.Name == "Entry");
        Element button = before.Single(e => e.Name == "Increment");
        Element caption = before.Single(e => e.Type == "TextBlock" && e.Text == "Before");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry.Id, Text = "User state", Replace = true }), "the app's input to be typed");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "the app's button to be clicked");
        Ui.WaitFor(() => Output(window).Contains("Name=User state; Count=1", StringComparison.Ordinal), "the original viewmodel to count the first click");
        Capture(app, theme + "-before");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"After\"");
        Typing.Replace(window, "Grid.Row=\"0\"", "Grid.Row=\"1\"");
        Typing.Replace(window, "Title=\"XAML Reload\"", "Title=\"XAML Reloaded\"");
        HotReloadOutcome applied = Apply(window);
        Assert.True(applied.Result == HotReloadResult.Applied, applied.Said);
        List<Element> after = Read(app);
        Assert.Equal("After", after.Single(e => e.Id == caption.Id).Text);
        Assert.Equal(entry.Id, after.Single(e => e.Name == "Entry").Id);
        Assert.Equal("User state", after.Single(e => e.Name == "Entry").Text);
        Assert.Equal("User state", after.Single(e => e.Name == "Bound").Text);
        Assert.Equal("1", after.Single(e => e.Text == "Attached").Values!["Grid.Row"]);
        Capture(app, theme + "-after");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "the original event handler to run again");
        Ui.WaitFor(() => Output(window).Contains("Name=User state; Count=2", StringComparison.Ordinal), "the original code-behind and viewmodel to keep counting");

        // A second edit after inserting a line still names the element by its original compiled position.
        Typing.Replace(window, "<TextBlock Text=\"After\" />", "\n<TextBlock Text=\"After again\" />");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.Equal("After again", Read(app).Single(e => e.Id == caption.Id).Text);

        long[] repeated = [.. Read(app).Where(e => e.Type == "TextBlock" && e.Text == "Repeated before").Select(e => e.Id)];
        Assert.Equal(2, repeated.Length);
        Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.axaml")));
        Typing.Replace(window, "Repeated before", "Repeated after");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.All(repeated, id => Assert.Equal("Repeated after", Read(app).Single(e => e.Id == id).Text));
        End(window);
    }

    [AvaloniaFact]
    public void An_invalid_conversion_reports_its_file_and_line_and_changes_none_of_the_other_properties()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"Must not apply\"");
        Typing.Replace(window, "Width=\"640\"", "Width=\"not a number\"");
        HotReloadOutcome failed = Apply(window);
        Assert.Equal(HotReloadResult.Errors, failed.Result);
        Assert.True(window.Debugger.HotReload!.Changed);
        Assert.Contains(failed.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml") && p.Position.Line == 4 && p.Message.Contains("Width", StringComparison.Ordinal));
        Assert.Contains(Read(app), e => e.Type == "TextBlock" && e.Text == "Before");
        Typing.Replace(window, "not a number", "700");
        Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
        Assert.Contains(Read(app), e => e.Type == "TextBlock" && e.Text == "Must not apply");
        End(window);
    }

    [AvaloniaFact]
    public void Unsupported_structure_and_a_file_with_no_live_instances_require_restart_and_apply_nothing()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "<TextBlock Text=\"Before\" />", "<TextBlock Text=\"Must not apply\" /><TextBlock Text=\"Added\" />");
        HotReloadOutcome structure = Ui.Await(window.Debugger.ApplyChangesAsync(), "the structural edit to be classified");
        Assert.Equal(HotReloadResult.RestartNeeded, structure.Result);
        Assert.Contains(structure.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml"));
        Assert.Contains(Read(app), e => e.Type == "TextBlock" && e.Text == "Before");
        End(window);

        window = Debugged();
        Assert.Null(window.OpenFile(Path.Combine(folder, "UnopenedView.axaml")));
        Typing.Replace(window, "Repeated before", "No instance");
        HotReloadOutcome absent = Ui.Await(window.Debugger.ApplyChangesAsync(), "the unused view's edit to be classified");
        Assert.Equal(HotReloadResult.RestartNeeded, absent.Result);
        Assert.Contains(absent.Problems, p => p.Path == Path.Combine(folder, "UnopenedView.axaml") && p.Message.Contains("live instance", StringComparison.Ordinal));
        End(window);
    }

    [AvaloniaFact]
    public void A_failed_xaml_apply_after_managed_code_applied_keeps_the_pending_edit_available_for_retry()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml.cs")));
        Typing.Replace(window, "Name={state.Name};", "Updated Name={state.Name};");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "Value=\"1\"", "Value=\"2\"");
        HotReloadOutcome failed = Apply(window);
        Assert.True(failed.Result == HotReloadResult.Errors, failed.Said);
        Assert.True(window.Debugger.HotReload!.Changed);
        Assert.Contains(Path.Combine(folder, "MainWindow.axaml.cs"), failed.Files);
        Assert.Contains(failed.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml") && p.Message.Contains("Value", StringComparison.Ordinal));
        // The real app accepted C# between preflight and the failed conversion. The same action must still run.
        HotReloadOutcome retried = Apply(window);
        Assert.True(retried.Result == HotReloadResult.Applied, retried.Said);
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "Increment").Id }), "the edited code-behind to run");
        Ui.WaitFor(() => Output(window).Contains("Updated Name=Original state; Count=1", StringComparison.Ordinal), "the already applied C# edit to survive the XAML retry");
        End(window);
    }

    [AvaloniaFact]
    public void A_same_named_file_elsewhere_does_not_change_the_only_loaded_view_with_that_filename()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        string other = Path.Combine(folder, "OtherProject", "MainWindow.axaml");
        XamlUpdateResult result = Ui.Await(app.ApplyXamlAsync(new XamlUpdate([new XamlSourceUpdate(other, View, View.Replace("Text=\"Before\"", "Text=\"Wrong view\"", StringComparison.Ordinal))])), "the unrelated source update to be refused");
        Assert.False(result.Applied);
        Assert.Contains(result.Problems, p => p.File == other && p.RestartRequired);
        Assert.Contains(Read(app), e => e.Type == "TextBlock" && e.Text == "Before");
        Assert.DoesNotContain(Read(app), e => e.Text == "Wrong view");
        End(window);
    }

    [AvaloniaFact]
    public void Xaml_edited_while_paused_is_kept_pending_then_applied_when_resume_releases_the_ui_thread()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        long caption = Read(app).Single(e => e.Text == "Before").Id;
        Ui.Run(window, AppActions.Pause);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the app's paused stack to be shown");
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"After resume\"");
        HotReloadOutcome paused = Apply(window);
        Assert.Equal(HotReloadResult.Failed, paused.Result);
        Assert.True(window.Debugger.HotReload!.Changed);
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.LastApplied is { Result: HotReloadResult.Applied }, "the pending XAML edit to apply after resume", TimeSpan.FromMinutes(1));
        Assert.Equal("After resume", Read(app).Single(e => e.Id == caption).Text);
        Assert.False(window.Debugger.HotReload.Changed);
        End(window);
    }

    [AvaloniaFact]
    public void An_update_that_expires_during_application_defined_conversion_never_mutates_the_live_controls()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        string after = View.Replace("Value=\"1\"", "Value=\"3\"", StringComparison.Ordinal).Replace("Text=\"Before\"", "Text=\"Late update\"", StringComparison.Ordinal);
        XamlUpdateResult expired = Ui.Await(app.ApplyXamlAsync(new XamlUpdate([new XamlSourceUpdate(Path.Combine(folder, "MainWindow.axaml"), View, after)])
            { ExpiresUtc = DateTimeOffset.UtcNow.AddMilliseconds(500).ToUnixTimeMilliseconds() }), "the slow update to expire before mutation");
        Assert.False(expired.Applied);
        Assert.Contains(expired.Problems, p => p.Message.Contains("expired", StringComparison.Ordinal));
        Ui.WaitFor(() => Output(window).Contains("Slow conversion", StringComparison.Ordinal), "the application converter to prove preflight actually ran");
        Assert.Contains(Read(app), e => e.Text == "Before");
        Assert.DoesNotContain(Read(app), e => e.Text == "Late update");
        End(window);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_throwing_direct_property_getter_reports_its_own_source_before_any_property_is_mutated(bool earlierChange)
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "Increment").Id }), "the app to enable its throwing getter");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        if (earlierChange)
        {
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Must not apply\"");
        }
        Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Changed\"");
        HotReloadOutcome refused = Apply(window);
        Assert.True(refused.Result == HotReloadResult.Errors, refused.Said);
        Assert.Contains(refused.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml") && p.Message.Contains("Fragile", StringComparison.Ordinal)
            && p.Message.Contains("previous value", StringComparison.Ordinal) && p.Position.Line == Array.FindIndex(View.Split('\n'), line => line.Contains("ThrowingControl", StringComparison.Ordinal)));
        Assert.Contains(Read(app), e => e.Text == "Before");
        Assert.DoesNotContain(Read(app), e => e.Text == "Must not apply");
        End(window);
    }

    [AvaloniaFact]
    public void Immediate_steps_keep_xaml_pending_without_queuing_a_mutation_behind_the_next_stop()
    {
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        long caption = Read(app).Single(e => e.Text == "Before").Id;
        long button = Read(app).Single(e => e.Name == "Increment").Id;
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml.cs")));
        int line = Array.FindIndex(Code.Split('\n'), text => text.Contains("State state =", StringComparison.Ordinal));
        window.EditorArea.CodeEditor.GoTo(new TextPosition(line, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Settle();
        Task<InputDone> clicked = app.InputAsync(new InputAction(InputKinds.Click) { Element = button });
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the UI event handler's breakpoint");
        Typing.Replace(window, "Name={state.Name};", "Updated Name={state.Name};");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Typing.Replace(window, "Text=\"Before\"", "Text=\"After stepping\"");
        for (int i = 0; i < 2; i++)
        {
            object? stop = window.Debugger.Stop;
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is { } next && !ReferenceEquals(stop, next),
                "the next step without waiting for a paused inspector", TimeSpan.FromSeconds(5));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.DoesNotContain(window.Debugger.LastApplied?.Said ?? "", "did not answer", StringComparison.Ordinal);
        }
        Ui.Run(window, AppActions.Resume);
        Ui.Await(clicked, "the original input event to complete after continue");
        Ui.WaitFor(() => window.Debugger.LastApplied is { Result: HotReloadResult.Applied } applied && applied.Files.Any(p => p.EndsWith("MainWindow.axaml", StringComparison.Ordinal)),
            "the deferred XAML to apply on continue");
        Assert.Equal("After stepping", Read(app).Single(e => e.Id == caption).Text);
        Ui.WaitFor(() => Output(window).Contains("Updated Name=Original state; Count=1", StringComparison.Ordinal), "C# to have applied before the steps executed it");
        End(window);
    }

    [AvaloniaFact]
    public void Run_under_dotnet_watch_keeps_the_inspector_and_managed_hot_reload_then_reconnects_after_restart()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Assert.Null(Ui.Await(window.RunAsync(), "the app to start under dotnet watch", TimeSpan.FromMinutes(3)));
            Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected, "the watched target's inspector to connect", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal), "watch to finish establishing its file watchers");
            LiveApp app = window.Session.RunApp!;
            int? firstProcess = app.ProcessId;
            long button = Read(app).Single(e => e.Name == "Increment").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml.cs")));
            Typing.Replace(window, "Name={state.Name};", "Watched Name={state.Name};");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => RunOutput(window).Contains("changes applied", StringComparison.OrdinalIgnoreCase), "watch's own managed Hot Reload agent to accept the edit", TimeSpan.FromMinutes(2));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button }), "the watched app to run its new C#");
            Ui.WaitFor(() => RunOutput(window).Contains("Watched Name=Original state; Count=1", StringComparison.Ordinal), "the watched app's updated event handler");
            Assert.Equal(firstProcess, app.ProcessId);
            XamlUpdateResult xaml = Ui.Await(app.ApplyXamlAsync(new XamlUpdate([new XamlSourceUpdate(Path.Combine(folder, "MainWindow.axaml"), View,
                View.Replace("Text=\"Before\"", "Text=\"Watched XAML\"", StringComparison.Ordinal))])), "the watched app's XAML property to update");
            Assert.True(xaml.Applied, string.Join("; ", xaml.Problems.Select(p => p.Message)));
            Assert.Contains(Read(app), e => e.Text == "Watched XAML");
            // A project-input change makes watch rebuild/restart, rather than a C# delta. The listener belongs to the run.
            string project = Path.Combine(folder, "LiveTreeApp.csproj");
            Assert.Null(window.OpenFile(project));
            Typing.Replace(window, "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><DefineConstants>WATCH_RESTART</DefineConstants>");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => app.State == LiveAppState.Connected && app.ProcessId != firstProcess, "the replacement target to reconnect to the same run inspector", TimeSpan.FromMinutes(2));
            Assert.Same(app, window.Session.RunApp);
            Assert.Contains(Read(app), e => e.Name == "Entry" && e.Text == "Original state");
            Assert.Contains(Read(app), e => e.Text == "Before");
        }
        finally
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "watch and its inspector listener to end");
            window.Close();
        }
    }

    private MainWindow Debugged()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        string? failed = Ui.Await(window.DebugAsync(), "the XAML app to build and start", TimeSpan.FromMinutes(4));
        Assert.True(failed is null, $"{failed}\n{window.BottomPanel.Build.Document.Snapshot}");
        Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected && window.LiveTree.Rows.Any(r => r.Element.Name == "Entry"), "the app's live controls", TimeSpan.FromMinutes(2));
        return window;
    }

    private static HotReloadOutcome Apply(MainWindow window)
    {
        HotReloadOutcome? before = window.Debugger.LastApplied;
        Ui.Run(window, AppActions.ApplyHotReloadChanges);
        Ui.WaitFor(() => window.Debugger.LastApplied is { } now && !ReferenceEquals(before, now), "the XAML edits to be applied", TimeSpan.FromMinutes(1));
        return window.Debugger.LastApplied!;
    }

    private static List<Element> Read(LiveApp app)
    {
        AppTree tree = Ui.Await(app.AgentTreeAsync(["Grid.Row"]), "the live app's tree to be read");
        return [.. tree.Roots.SelectMany(Flatten)];
    }

    private static IEnumerable<Element> Flatten(Element element) => new[] { element }.Concat(element.Children.SelectMany(Flatten));

    private static string Output(MainWindow window) => window.BottomPanel.Debug.Console.Editor.Document.Snapshot.ToString();

    private static string RunOutput(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static void Capture(LiveApp app, string name)
    {
        long root = Ui.Await(app.WindowsAsync(), "the live window to capture").Single(w => w.Title?.StartsWith("XAML Reload", StringComparison.Ordinal) == true).Id;
        Picture picture = Ui.Await(app.CaptureAsync(root, scale: 2, area: null), "the app's scale-2 frame");
        TestContext.Current.AddAttachment("XAML Hot Reload " + name, picture.Png, "image/png");
        string frames = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "xaml-hot-reload")).FullName;
        File.WriteAllBytes(Path.Combine(frames, name + ".png"), picture.Png);
    }

    private static void End(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the app to stop");
        window.Close();
    }
}
