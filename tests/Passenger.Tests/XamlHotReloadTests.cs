using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

    private void FutureViews(bool foreignOverride = false, bool customProvider = false)
    {
        string main = View.Replace("Height=\"460\"", "Height=\"1000\"", StringComparison.Ordinal)
            .Replace("<Button x:Name=\"Increment\"", "<Button x:Name=\"CreateView\" Content=\"Create view\" Click=\"Created\" /><Button x:Name=\"Increment\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), main);
        string setup = foreignOverride
            ? " typeof(RepeatedView).GetField(\"!XamlIlPopulateOverride\", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, (Action<object>)(_ => { }));"
            : string.Empty;
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml.cs"), Code
            .Replace("DataContext = new State();", "DataContext = new State();" + setup, StringComparison.Ordinal)
            .Replace("private void Clicked", "private void Created(object? sender, RoutedEventArgs e) { ((StackPanel)Content!).Children.Add(new RepeatedView()); ((StackPanel)Content!).Children.Add(new UnopenedView()); }\n    private void Clicked", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "RepeatedView.axaml"), """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="using:LiveTreeApp" x:Class="LiveTreeApp.RepeatedView" x:DataType="local:State">
                <StackPanel>
                    <TextBlock x:Name="NewLiteral" Text="Created before" Width="200" />
                    <TextBlock x:Name="ConstructorValue" Text="XAML before" />
                    <TextBox x:Name="NewEntry" Text="{Binding Name, Mode=TwoWay}" />
                    <TextBlock x:Name="NewBound" Text="{Binding Name}" />
                    <Button x:Name="NewIncrement" Content="New count" Click="Clicked" />
                    <local:ThrowingControl Fragile="Safe" />
                </StackPanel>
            </UserControl>
            """);
        string populate = customProvider ? "Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Services(), this);" : "InitializeComponent();";
        File.WriteAllText(Path.Combine(folder, "RepeatedView.axaml.cs"), $$"""
            using Avalonia.Controls;
            using Avalonia.Interactivity;
            namespace LiveTreeApp;
            public partial class RepeatedView : UserControl
            {
                public RepeatedView()
                {
                    {{populate}}
                    Console.WriteLine($"Constructor saw {this.FindControl<TextBlock>("NewLiteral")!.Text}; {this.FindControl<TextBlock>("ConstructorValue")!.Text}");
                    this.FindControl<TextBlock>("ConstructorValue")!.Text = "Constructor wins";
                    DataContext = new State();
                }
                private void Clicked(object? sender, RoutedEventArgs e)
                {
                    State state = (State)DataContext!;
                    state.Count++;
                    Console.WriteLine($"New view Name={state.Name}; Count={state.Count}");
                }
            }
            public sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }
            """);
    }

    [AvaloniaTheory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", true)]
    public void Newly_created_views_use_composed_xaml_edits_before_constructor_assignments_and_keep_compiled_bindings_and_handlers(string theme, bool watched)
    {
        FutureViews();
        string appXaml = Path.Combine(folder, "App.axaml");
        File.WriteAllText(appXaml, File.ReadAllText(appXaml).Replace("RequestedThemeVariant=\"Light\"", $"RequestedThemeVariant=\"{theme}\"", StringComparison.Ordinal));
        MainWindow window = FutureStarted(watched);
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            Element originalEntry = before.First(e => e.Name == "NewEntry");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = originalEntry.Id, Text = "Existing user state", Replace = true }), "the existing view's state to change");
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.axaml")));
            Typing.Replace(window, "Created before", "Created first");
            Assert.Equal(HotReloadResult.Applied, FutureApplied(window, watched).Result);
            Typing.Replace(window, "Created first", "Created latest");
            Typing.Replace(window, "XAML before", "XAML latest");
            Typing.Replace(window, "Width=\"200\"", "Width=\"260\" MaxHeight=\"55\"");
            Assert.Equal(HotReloadResult.Applied, FutureApplied(window, watched).Result);
            Capture(app, "future-" + (watched ? "run-" : "debug-") + theme + "-before");
            HashSet<long> old = [.. before.Select(e => e.Id)];
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a new compiled view to be constructed");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Created latest; XAML latest", StringComparison.Ordinal), "the constructor to observe composed edits before its own assignments");
            Assert.Equal(process, app.ProcessId);
            List<Element> after = Read(app);
            Element made = after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id));
            List<Element> children = [.. Flatten(made)];
            Assert.Equal("Created latest", children.Single(e => e.Name == "NewLiteral").Text);
            ElementDetails details = Ui.Await(app.DetailsAsync(children.Single(e => e.Name == "NewLiteral").Id), "the new view's composed width to be read");
            Assert.Equal("260", details.Properties.Single(p => p.Name == "Width").Value);
            Assert.Equal("55", details.Properties.Single(p => p.Name == "MaxHeight").Value);
            Assert.Equal(ValueSources.Local, details.Properties.Single(p => p.Name == "MaxHeight").Source);
            Assert.Equal("Constructor wins", children.Single(e => e.Name == "ConstructorValue").Text);
            Assert.Equal("Existing user state", after.Single(e => e.Id == originalEntry.Id).Text);
            Assert.Contains(after, e => e.Type == "TextBlock" && e.Text == "Repeated before");
            Element entry = children.Single(e => e.Name == "NewEntry");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry.Id, Text = "New user state", Replace = true }), "the new compiled binding to accept user text");
            Assert.Equal("New user state", Read(app).Single(e => e.Id == children.Single(c => c.Name == "NewBound").Id).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = children.Single(e => e.Name == "NewIncrement").Id }), "the new compiled event handler to run");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("New view Name=New user state; Count=1", StringComparison.Ordinal), "the new namescope, viewmodel and event connection to work");
            Capture(app, "future-" + (watched ? "run-" : "debug-") + theme + "-after");
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_population_failure_reports_its_source_without_rewriting_the_completed_apply_or_losing_pending_edits(bool watched)
    {
        FutureViews();
        MainWindow window = FutureStarted(watched);
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            string path = Path.Combine(folder, "RepeatedView.axaml");
            Assert.Null(window.OpenFile(path));
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Accepted\"");
            HotReloadOutcome applied = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Applied, applied.Result);
            Typing.Replace(window, "Created before", "Pending text");
            XamlUpdateResult? observed = null;
            app.XamlFailed += (_, result) => observed = result;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "Increment").Id }), "the app to make subsequent property getters refuse");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the app to construct another view");
            Ui.WaitFor(() => observed is not null, "the later population failure to arrive as an event");
            Assert.Same(applied, watched ? window.Session.RunXaml.LastApplied : window.Debugger.LastApplied);
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.Contains(observed!.Problems, p => p.File == path && p.Line == 9 && p.RestartRequired && p.Message.Contains("Fragile", StringComparison.Ordinal));
            Assert.Contains("newly created view", FutureOutput(window, watched), StringComparison.Ordinal);
            Assert.Contains(window.BottomPanel.Problems.HotReloadProblems!, p => p.Path == path && p.Message.Contains("newly created", StringComparison.Ordinal));
            Assert.Contains("newly created view", window.Status!, StringComparison.Ordinal);
            window.SetRenderScaling(2);
            Ui.Settle();
            UiAudit.Capture(window, "future-failure-" + (watched ? "run" : "debug"));
            if (!watched)
            {
                HotReloadOutcome next = Ui.Await(window.Debugger.ApplyChangesAsync(), "the inconsistent debug source to require restart");
                Assert.Equal(HotReloadResult.RestartNeeded, next.Result);
                Assert.True(window.Debugger.HotReload!.Changed);
            }
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Literal_property_elements_update_their_parent_and_future_views_without_replacing_state(bool watched)
    {
        FutureViews();
        string main = Path.Combine(folder, "MainWindow.axaml");
        File.WriteAllText(main, File.ReadAllText(main).Replace("Text=\"Attached\" Grid.Row=\"0\" />", "Text=\"Attached\"><Grid.Row>0</Grid.Row></TextBlock>", StringComparison.Ordinal));
        string child = Path.Combine(folder, "RepeatedView.axaml");
        File.WriteAllText(child, File.ReadAllText(child).Replace("Text=\"Created before\" Width=\"200\" />", "Width=\"200\"><TextBlock.Text>Created before</TextBlock.Text></TextBlock>", StringComparison.Ordinal));
        MainWindow window = FutureStarted(watched);
        try
        {
            window.ApplySettings(window.Settings with { RestartOnRudeEdit = false });
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            HashSet<long> old = [.. before.Select(e => e.Id)];
            long oldEntry = before.First(e => e.Name == "NewEntry").Id;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = oldEntry, Text = "Kept property-element state", Replace = true }), "existing Avalonia state");
            Assert.Null(window.OpenFile(main));
            int rowLine = Array.FindIndex(File.ReadAllLines(main), line => line.Contains("<Grid.Row>", StringComparison.Ordinal)) + 1;
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Property batch applied\"");
            Typing.Replace(window, "<Grid.Row>0</Grid.Row>", "<Grid.Row>invalid</Grid.Row>");
            HotReloadOutcome invalid = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, invalid.Result);
            Assert.Contains(invalid.Problems, p => p.Path == main && p.Position.Line == rowLine - 1 && p.Message.Contains("Grid.Row", StringComparison.Ordinal));
            Assert.Contains(Read(app), e => e.Text == "Before");
            Assert.DoesNotContain(Read(app), e => e.Text == "Property batch applied");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Typing.Replace(window, "<Grid.Row>invalid</Grid.Row>", "<Grid.Row>1</Grid.Row>");
            HotReloadOutcome attached = FutureApplied(window, watched);
            Assert.True(attached.Result == HotReloadResult.Applied, attached.Said);
            ElementDetails attachedDetails = Ui.Await(app.DetailsAsync(Read(app).Single(e => e.Text == "Attached").Id), "the Avalonia attached property-element value");
            Assert.Equal("1", attachedDetails.Properties.Single(p => p.Name == "Grid.Row").Value);
            Assert.Null(window.OpenFile(child));
            Typing.Replace(window, "<TextBlock.Text>Created before</TextBlock.Text>", "<TextBlock.Text>Property first</TextBlock.Text>");
            HotReloadOutcome first = FutureApplied(window, watched);
            Assert.True(first.Result == HotReloadResult.Applied, first.Said);
            Typing.Replace(window, "<TextBlock.Text>Property first</TextBlock.Text>", "<TextBlock.Text>Property latest</TextBlock.Text>");
            HotReloadOutcome latest = FutureApplied(window, watched);
            Assert.True(latest.Result == HotReloadResult.Applied, latest.Said);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a future Avalonia view with property-element literals");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Property latest; XAML before", StringComparison.Ordinal), "the constructor to see the property-element update");
            List<Element> after = Read(app);
            List<Element> made = [.. Flatten(after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id)))];
            Assert.Equal("Property latest", made.Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("Constructor wins", made.Single(e => e.Name == "ConstructorValue").Text);
            Assert.Equal("Kept property-element state", after.Single(e => e.Id == oldEntry).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = made.Single(e => e.Name == "NewEntry").Id, Text = "New property-element state", Replace = true }), "the future Avalonia binding");
            Assert.Equal("New property-element state", Read(app).Single(e => e.Id == made.Single(e => e.Name == "NewBound").Id).Text);
            Assert.Equal(process, app.ProcessId);
            Capture(app, "property-elements-" + (watched ? "run" : "debug"));
            Typing.Replace(window, "<TextBlock.Text>Property latest</TextBlock.Text>", "<TextBlock.Text>{Binding Name}</TextBlock.Text>");
            HotReloadOutcome markup = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.RestartNeeded, markup.Result);
            Assert.Contains(markup.Problems, p => p.Path == child && p.Message.Contains("markup", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, Read(app).Count(e => e.Name == "NewLiteral" && e.Text == "Property latest"));
            Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the unsupported Avalonia property body to offer a restart");
            ChoiceDialog asked = window.OwnedWindows.OfType<ChoiceDialog>().Single();
            Ui.Settle();
            Avalonia.Controls.Button never = asked.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => (string?)b.Content == "Never");
            Avalonia.Point at = never.TranslatePoint(new Avalonia.Point(never.Bounds.Width / 2, never.Bounds.Height / 2), asked)!.Value;
            asked.MouseMove(at);
            asked.MouseDown(at, Avalonia.Input.MouseButton.Left);
            asked.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.Settle();
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Typing.Replace(window, "<TextBlock.Text>{Binding Name}</TextBlock.Text>", "<TextBlock.Text>Property latest</TextBlock.Text>");
            Typing.Replace(window, "x:Name=\"NewLiteral\"", "x:Name=\"NewLiteral\" Text=\"Duplicate\"");
            HotReloadOutcome duplicate = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, duplicate.Result);
            Assert.Contains(duplicate.Problems, p => p.Path == child && p.Message.Contains("both an attribute and a property element", StringComparison.Ordinal));
            Assert.DoesNotContain(Read(app), e => e.Text == "Duplicate");
            Typing.Replace(window, "x:Name=\"NewLiteral\" Text=\"Duplicate\"", "x:Name=\"NewLiteral\"");
            Typing.Replace(window, "<TextBlock.Text>Property latest</TextBlock.Text>", "<TextBlock.Text> Property latest </TextBlock.Text>");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            HotReloadOutcome whitespace = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.RestartNeeded, whitespace.Result);
            Assert.Contains(whitespace.Problems, p => p.Path == child && p.Message.Contains("whitespace", StringComparison.Ordinal));
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            // Debug declines stop asking until the next edit; Run keeps the saved batch pending.
            Assert.Equal(watched, watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Typing.Replace(window, "<TextBlock.Text> Property latest </TextBlock.Text>", "<TextBlock.>Property latest</TextBlock.>");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            HotReloadOutcome incomplete = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.RestartNeeded, incomplete.Result);
            Assert.Contains(incomplete.Problems, p => p.Path == child && p.Message.Contains("structure", StringComparison.Ordinal));
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Assert.Equal(process, app.ProcessId);
            Assert.Equal(3, Read(app).Count(e => e.Name == "NewLiteral" && e.Text == "Property latest"));
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    private MainWindow FutureStarted(bool watched)
    {
        if (!watched)
        {
            return Debugged();
        }
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { HotReload = true });
        Assert.Null(Ui.Await(window.RunAsync(), "the app to start under dotnet watch", TimeSpan.FromMinutes(3)));
        Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected, "the watched app's inspector", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal), "watch's file watchers to be established");
        return window;
    }

    private void AttributeView(bool throwing = false)
    {
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), View.Replace("<StackPanel Margin", "<Window.Styles><Style Selector=\"TextBlock.caption\"><Setter Property=\"Width\" Value=\"180\" /></Style></Window.Styles><StackPanel Margin", StringComparison.Ordinal)
            .Replace("Text=\"Before\"", "Text=\"Before\" Classes=\"caption\"", StringComparison.Ordinal));
        if (throwing)
        {
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml.cs"), Code.Replace("set => SetAndRaise(FragileProperty, ref fragile, value);",
                "set { SetAndRaise(FragileProperty, ref fragile, value); if (value == \"Refuse\") { throw new InvalidOperationException(\"Setter refused\"); } }", StringComparison.Ordinal));
        }
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_literal_attribute_can_be_added_and_edited_again_as_a_local_value_without_replacing_user_state(string theme)
    {
        AttributeView();
        string appXaml = Path.Combine(folder, "App.axaml");
        File.WriteAllText(appXaml, File.ReadAllText(appXaml).Replace("RequestedThemeVariant=\"Light\"", $"RequestedThemeVariant=\"{theme}\"", StringComparison.Ordinal));
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            Element caption = Read(app).Single(e => e.Text == "Before");
            Assert.Equal(ValueSources.Style, Ui.Await(app.DetailsAsync(caption.Id), "the original style value").Properties.Single(p => p.Name == "Width").Source);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = Read(app).Single(e => e.Name == "Entry").Id, Text = "Kept input", Replace = true }), "the existing input");
            Capture(app, "attribute-" + theme + "-before");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Text=\"Before\" Classes", "Text=\"Before\" Width=\"260\" Classes");
            Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
            Typing.Replace(window, "Width=\"260\"", "Width=\"300\"");
            Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
            PropertyValue width = Ui.Await(app.DetailsAsync(caption.Id), "the added attribute's current source").Properties.Single(p => p.Name == "Width");
            Assert.Equal("300", width.Value);
            Assert.Equal(ValueSources.Local, width.Source);
            Assert.Equal("Kept input", Read(app).Single(e => e.Name == "Bound").Text);
            Capture(app, "attribute-" + theme + "-after");
        }
        finally
        {
            End(window);
        }
    }

    [AvaloniaFact]
    public void An_added_attribute_in_a_failed_batch_restores_the_style_source_instead_of_leaving_a_local_value()
    {
        AttributeView(throwing: true);
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Text=\"Before\" Classes", "Text=\"Before\" Width=\"260\" Classes");
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Refuse\"");
            HotReloadOutcome refused = Ui.Await(window.Debugger.ApplyChangesAsync(), "the app setter to refuse the batch");
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            PropertyValue width = Ui.Await(app.DetailsAsync(caption), "the rolled back attribute's value source").Properties.Single(p => p.Name == "Width");
            Assert.Equal("180", width.Value);
            Assert.Equal(ValueSources.Style, width.Source);
            Assert.True(window.Debugger.HotReload!.Changed);
        }
        finally
        {
            End(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adding_an_attribute_over_a_constructor_binding_requires_restart_without_changing_the_binding_or_the_batch(bool observable)
    {
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), View.Replace("x:Name=\"Bound\" Text=\"{Binding Name}\"", "x:Name=\"Bound\"", StringComparison.Ordinal));
        string binding = observable ? "new StateNames((State)DataContext!)" : "new Avalonia.Data.Binding(\"Name\")";
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml.cs"), Code.Replace("DataContext = new State();", $"DataContext = new State(); this.FindControl<TextBlock>(\"Bound\")!.Bind(TextBlock.TextProperty, {binding});", StringComparison.Ordinal) + """

            public sealed class StateNames(State state) : IObservable<string?>
            {
                public IDisposable Subscribe(IObserver<string?> observer)
                {
                    PropertyChangedEventHandler changed = (_, _) => observer.OnNext(state.Name);
                    state.PropertyChanged += changed;
                    observer.OnNext(state.Name);
                    return new Unsubscribe(() => state.PropertyChanged -= changed);
                }
                private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
            }
            """);
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "x:Name=\"Bound\"", "x:Name=\"Bound\" Text=\"Must not replace\"");
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Must not apply\"");
            HotReloadOutcome result = Ui.Await(window.Debugger.ApplyChangesAsync(), "the runtime constructor binding to be preserved");
            Assert.Equal(HotReloadResult.RestartNeeded, result.Result);
            Assert.Contains(result.Problems, p => p.Message.Contains("binding", StringComparison.Ordinal));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = Read(app).Single(e => e.Name == "Entry").Id, Text = "Binding survived", Replace = true }), "the retained binding's source to change");
            Assert.Equal("Binding survived", Read(app).Single(e => e.Name == "Bound").Text);
            Assert.Contains(Read(app), e => e.Text == "Before");
        }
        finally
        {
            End(window);
        }
    }

    [AvaloniaFact]
    public void A_setter_created_view_which_refuses_population_makes_the_active_batch_require_restart()
    {
        FutureViews();
        string view = Path.Combine(folder, "MainWindow.axaml");
        File.WriteAllText(view, File.ReadAllText(view).Replace("<Button x:Name=\"CreateView\"", "<local:SpawningControl Trigger=\"1\" /><Button x:Name=\"CreateView\"", StringComparison.Ordinal));
        string code = Path.Combine(folder, "MainWindow.axaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("DataContext = new State();", "DataContext = new State(); SpawningControl.Spawn = () => ((StackPanel)Content!).Children.Add(new RepeatedView());", StringComparison.Ordinal) + """

            public class SpawningControl : Control
            {
                public static Action? Spawn;
                private int trigger;
                public static readonly DirectProperty<SpawningControl, int> TriggerProperty = AvaloniaProperty.RegisterDirect<SpawningControl, int>(nameof(Trigger), item => item.Trigger, (item, value) => item.Trigger = value);
                public int Trigger
                {
                    get => trigger;
                    set
                    {
                        SetAndRaise(TriggerProperty, ref trigger, value);
                        if (value == 2) { ThrowingControl.ReadThrows = true; Spawn?.Invoke(); }
                    }
                }
            }
            """);
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.axaml")));
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Accepted\"");
            Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
            Typing.Replace(window, "Fragile=\"Accepted\"", "Fragile=\"Batch\"");
            Assert.Null(window.OpenFile(view));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Must roll back\"");
            Typing.Replace(window, "Trigger=\"1\"", "Trigger=\"2\"");
            int events = 0;
            app.XamlFailed += (_, _) => events++;
            HotReloadOutcome result = Ui.Await(window.Debugger.ApplyChangesAsync(), "the setter-created view's refusal to be part of the active batch");
            Assert.Equal(HotReloadResult.RestartNeeded, result.Result);
            Assert.Contains(result.Problems, p => p.Message.Contains("newly created", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.Equal(0, events);
            Assert.Equal(3, Read(app).Count(e => e.Type == "RepeatedView"));
            Assert.Contains(Read(app), e => e.Text == "Before");
            Assert.DoesNotContain(Read(app), e => e.Text == "Must roll back");
        }
        finally
        {
            End(window);
        }
    }

    private static HotReloadOutcome FutureApplied(MainWindow window, bool watched)
    {
        if (!watched)
        {
            return Apply(window);
        }
        HotReloadOutcome? before = window.Session.RunXaml.LastApplied;
        Ui.Run(window, AppActions.Save);
        Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { } current && !ReferenceEquals(before, current), "the next saved XAML edit to apply");
        return window.Session.RunXaml.LastApplied!;
    }

    private static string FutureOutput(MainWindow window, bool watched) => watched ? RunOutput(window) : Output(window);

    private static void FutureEnded(MainWindow window, bool watched)
    {
        if (!watched)
        {
            End(window);
            return;
        }
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "the watched app to stop");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true, false, "already overridden")]
    [InlineData(false, true, "custom service provider")]
    public void A_foreign_population_override_or_custom_service_provider_is_refused_before_changing_existing_views(bool foreign, bool provider, string reason)
    {
        FutureViews(foreign, provider);
        MainWindow window = Debugged();
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.axaml")));
            Typing.Replace(window, "Created before", "Must not apply");
            HotReloadOutcome outcome = Ui.Await(window.Debugger.ApplyChangesAsync(), "an unsafe population override to be refused");
            Assert.Equal(HotReloadResult.RestartNeeded, outcome.Result);
            Assert.Contains(outcome.Problems, p => p.Message.Contains(reason, StringComparison.Ordinal));
            Assert.Contains(Read(window.LiveApp!), e => e.Text == "Created before");
        }
        finally
        {
            End(window);
        }
    }

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
        TestContext.Current.TestOutputHelper?.WriteLine($"Mixed update: {failed.Result}; {failed.Said}; files={string.Join(',', failed.Files)}\n{Output(window)}");
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
        TestContext.Current.TestOutputHelper?.WriteLine($"After stepping: {window.Debugger.LastApplied}\n{Output(window)}");
        Ui.WaitFor(() => Output(window).Contains("Updated Name=Original state; Count=1", StringComparison.Ordinal), "C# to have applied before the steps executed it");
        End(window);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Run_under_dotnet_watch_keeps_the_inspector_and_managed_hot_reload_then_reconnects_after_restart(string theme)
    {
        if (theme == "Dark")
        {
            string appXaml = Path.Combine(folder, "App.axaml");
            File.WriteAllText(appXaml, File.ReadAllText(appXaml).Replace("RequestedThemeVariant=\"Light\"", "RequestedThemeVariant=\"Dark\"", StringComparison.Ordinal));
        }
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
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Capture(app, "watch-" + theme + "-before");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Watched XAML\"");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied?.Result == HotReloadResult.Applied, "a saved XAML edit to update the watched UI without rebuilding");
            Assert.Contains(Read(app), e => e.Text == "Watched XAML");
            Assert.Equal(firstProcess, app.ProcessId);
            Assert.Equal(caption, Read(app).Single(e => e.Text == "Watched XAML").Id);
            Assert.Contains(Read(app), e => e.Name == "Entry" && e.Text == "Original state");
            HotReloadOutcome saved = window.Session.RunXaml.LastApplied!;
            Typing.Replace(window, "Text=\"Watched XAML\"", "Text=\"Applied XAML\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { Result: HotReloadResult.Applied } next && next != saved, "Apply Changes to apply XAML in the watched app");
            Assert.Equal("Applied XAML", Read(app).Single(e => e.Id == caption).Text);
            Assert.Equal(firstProcess, app.ProcessId);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button }), "the retained handler after watched XAML updates");
            Ui.WaitFor(() => RunOutput(window).Contains("Watched Name=Original state; Count=2", StringComparison.Ordinal), "the original state and handler after both XAML updates");
            Capture(app, "watch-" + theme + "-after");
            // A project-input change makes watch rebuild/restart, rather than a C# delta. The listener belongs to the run.
            string project = Path.Combine(folder, "LiveTreeApp.csproj");
            Assert.Null(window.OpenFile(project));
            Typing.Replace(window, "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><DefineConstants>WATCH_RESTART</DefineConstants>");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => app.State == LiveAppState.Connected && app.ProcessId != firstProcess, "the replacement target to reconnect to the same run inspector", TimeSpan.FromMinutes(2));
            Assert.Same(app, window.Session.RunApp);
            Assert.Contains(Read(app), e => e.Name == "Entry" && e.Text == "Original state");
            Assert.Contains(Read(app), e => e.Text == "Applied XAML");
            HotReloadOutcome beforeReconnectEdit = window.Session.RunXaml.LastApplied!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Applied XAML", "After reconnect");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { Result: HotReloadResult.Applied } applied && applied != beforeReconnectEdit,
                "XAML edits to use the replacement process's compiled source anchors");
            Assert.Contains(Read(app), e => e.Text == "After reconnect");
        }
        finally
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "watch and its inspector listener to end");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_watched_xaml_failure_stays_pending_for_retry_and_a_structural_edit_can_be_declined_then_restarted()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { HotReload = true, RestartOnRudeEdit = false });
        try
        {
            Assert.Null(Ui.Await(window.RunAsync(), "the watched XAML app to start", TimeSpan.FromMinutes(3)));
            Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected && RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal), "the watched app and its file watchers");
            LiveApp app = window.Session.RunApp!;
            int? first = app.ProcessId;
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"After retry\"");
            Typing.Replace(window, "Width=\"640\"", "Width=\"invalid\"");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied?.Result == HotReloadResult.Errors, "the invalid saved XAML to be diagnosed");
            Assert.Contains(window.Session.RunXaml.LastApplied!.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml") && p.Position.Line == 4 && p.Message.Contains("Width", StringComparison.Ordinal));
            Assert.True(window.Session.RunXaml.Changed);
            Assert.Equal("Before", Read(app).Single(e => e.Id == caption).Text);
            Assert.Equal(first, app.ProcessId);
            UiAudit.Capture(window, "watched-xaml-conversion-error");
            Typing.Replace(window, "Width=\"invalid\"", "Width=\"680\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied?.Result == HotReloadResult.Applied, "the corrected XAML batch to apply on retry");
            Assert.False(window.Session.RunXaml.Changed);
            Assert.Equal("After retry", Read(app).Single(e => e.Id == caption).Text);
            Assert.Equal(first, app.ProcessId);
            UiAudit.Capture(window, "watched-xaml-retry-applied");
            Typing.Replace(window, "<TextBlock Text=\"After retry\" />", "<TextBlock Text=\"After retry\" /><TextBlock Text=\"Added control\" />");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the structural XAML edit to offer a restart");
            Assert.Equal(HotReloadResult.RestartNeeded, window.Session.RunXaml.LastApplied!.Result);
            Assert.Contains(window.Session.RunXaml.LastApplied.Problems, p => p.Path == Path.Combine(folder, "MainWindow.axaml") && p.Message.Contains("elements", StringComparison.Ordinal));
            ChoiceDialog asked = window.OwnedWindows.OfType<ChoiceDialog>().Single();
            asked.SetRenderScaling(2);
            Ui.Settle();
            UiAudit.Capture(asked, "watched-xaml-restart-choice");
            Avalonia.Controls.Button no = asked.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => (string?)b.Content == "No");
            no.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Ui.Settle();
            Assert.True(window.Session.RunXaml.Changed);
            Assert.Equal(first, app.ProcessId);
            Assert.DoesNotContain(Read(app), e => e.Text == "Added control");
            window.ApplySettings(window.Settings with { RestartOnRudeEdit = true });
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected } replacement && replacement != app,
                "the restart to build and run the structural XAML edit", TimeSpan.FromMinutes(2));
            Assert.NotEqual(first, window.Session.RunApp!.ProcessId);
            Assert.Equal(LiveAppState.Ended, app.State);
            Assert.Contains(Read(window.Session.RunApp), e => e.Text == "Added control");
            Assert.False(window.Session.RunXaml.Changed);
            Assert.Contains(window.BottomPanel.Problems.HotReloadProblems!, p => p.Message.Contains("elements", StringComparison.Ordinal));
        }
        finally
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "the watched app to end");
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("later save")]
    [InlineData("saved revert")]
    [InlineData("unsaved revert")]
    public void A_save_during_a_real_xaml_update_applies_the_latest_saved_text_and_keeps_newer_unsaved_edits(string kind)
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Assert.Null(Ui.Await(window.RunAsync(), "the watched app to start", TimeSpan.FromMinutes(3)));
            Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected && RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal), "the watched app and its file watchers");
            LiveApp app = window.Session.RunApp!;
            int? process = app.ProcessId;
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"First saved\"");
            Typing.Replace(window, "Value=\"1\"", "Value=\"3\"");
            Assert.True(window.Perform(AppActions.Save));
            // Pump the real window without waiting for all background work: the deliberately slow converter holds the
            // inspector's UI-thread reply while the person edits and saves in Passenger's separate UI process.
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (!RunOutput(window).Contains("Slow conversion", StringComparison.Ordinal) && DateTime.UtcNow < until)
            {
                Screenshot.Pump(TimeSpan.FromMilliseconds(20));
            }
            Assert.Contains("Slow conversion", RunOutput(window), StringComparison.Ordinal);
            Assert.Null(window.Session.RunXaml.LastApplied);
            string expectedLive = kind == "saved revert" ? "Before" : kind == "unsaved revert" ? "First saved" : "Second saved";
            string expectedEditor = kind == "unsaved revert" ? "Before" : "Newer unsaved";
            ReplaceDuringApply("First saved", kind == "later save" ? "Second saved" : "Before");
            if (kind != "later save")
            {
                ReplaceDuringApply("Value=\"3\"", "Value=\"1\"");
            }
            if (kind != "unsaved revert")
            {
                Assert.True(window.Perform(AppActions.Save));
                ReplaceDuringApply(expectedLive, expectedEditor);
            }
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { Result: HotReloadResult.Applied }
                && Read(app).Any(e => e.Id == caption && e.Text == expectedLive), "the latest saved update to follow the held first reply");
            Assert.Equal(process, app.ProcessId);
            Assert.True(window.Session.RunXaml.Changed);
            Assert.Contains("Text=\"" + expectedEditor + "\"", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("Text=\"" + expectedLive + "\"", File.ReadAllText(Path.Combine(folder, "MainWindow.axaml")), StringComparison.Ordinal);
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => !window.Session.RunXaml.Changed && Read(app).Any(e => e.Id == caption && e.Text == expectedEditor), "the later manual Apply to save and apply the remaining edit");

            void ReplaceDuringApply(string before, string after)
            {
                Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
                int at = editor.Document.Snapshot.ToString().IndexOf(before, StringComparison.Ordinal);
                Assert.True(at >= 0);
                editor.Focus();
                editor.Select(new TextRange(at, before.Length));
                window.KeyTextInput(after);
            }
        }
        finally
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "the watched app to end");
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
