using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
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

    private void FutureViews(string theme)
    {
        string view = View.Replace("Height=\"460\"", "Height=\"1000\"", StringComparison.Ordinal)
            .Replace("<local:RepeatedView />\n        <local:RepeatedView />", "<local:RepeatedView /><Button x:Name=\"CreateView\" Content=\"Create view\" Click=\"CreateViewClicked\" /><StackPanel x:Name=\"NewViews\" />", StringComparison.Ordinal);
        if (theme == "Dark")
        {
            view = view.Replace("Background=\"WhiteSmoke\" Foreground=\"Black\"", "Background=\"#222222\" Foreground=\"White\"", StringComparison.Ordinal);
        }
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), view);
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.cs"), Code.Replace("private void Clicked", "private void CreateViewClicked(object sender, RoutedEventArgs e) { NewViews.Children.Add(new RepeatedView()); Console.WriteLine(\"Imports=\" + ImportState.Before + \"/\" + ImportState.After); }\n    private void Clicked", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "RepeatedView.xaml"), """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="WpfTreeApp.RepeatedView">
                <StackPanel>
                    <TextBlock x:Name="NewLiteral" Text="Created before" Width="200" />
                    <TextBlock x:Name="ConstructorValue" Text="XAML before" />
                    <TextBox x:Name="NewEntry" Text="{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
                    <TextBlock x:Name="NewBound" Text="{Binding Name}" />
                    <Button x:Name="NewIncrement" Content="Child count" Click="Clicked" />
                </StackPanel>
            </UserControl>
            """);
        File.WriteAllText(Path.Combine(folder, "RepeatedView.xaml.cs"), """
            using System.Windows;
            using System.Windows.Controls;
            namespace WpfTreeApp;
            public partial class RepeatedView : UserControl
            {
                public RepeatedView()
                {
                    InitializeComponent();
                    Console.WriteLine($"Constructor saw {NewLiteral.Text}; {ConstructorValue.Text}");
                    ConstructorValue.Text = "Constructor wins";
                    DataContext = new State();
                }
                private void Clicked(object sender, RoutedEventArgs e)
                {
                    State state = (State)DataContext;
                    state.Count++;
                    Console.WriteLine($"New view Name={state.Name}; Count={state.Count}; Named={FindName("NewIncrement") == NewIncrement}");
                }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "ImportState.cs"), """
            namespace WpfTreeApp;
            public static class ImportState
            {
            #if BEFORE_IMPORT_KEPT
                public const string Before = "before kept";
            #else
                public const string Before = "BEFORE LOST";
            #endif
            #if AFTER_IMPORT_KEPT
                public const string After = "after kept";
            #else
                public const string After = "AFTER LOST";
            #endif
            }
            """);
        File.WriteAllText(Path.Combine(folder, "UserBefore.targets"), """
            <Project>
              <PropertyGroup><DefineConstants>$(DefineConstants);BEFORE_IMPORT_KEPT</DefineConstants></PropertyGroup>
              <Target Name="UserBeforeEffect" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(IntermediateOutputPath)user-before.txt" Lines="Before kept $(TargetFramework)" Overwrite="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "UserAfter.targets"), """
            <Project>
              <PropertyGroup><DefineConstants>$(DefineConstants);AFTER_IMPORT_KEPT</DefineConstants></PropertyGroup>
              <Target Name="UserAfterEffect" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(IntermediateOutputPath)user-after.txt" Lines="After kept $(TargetFramework)" Overwrite="true" />
              </Target>
            </Project>
            """);
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        string text = File.ReadAllText(project).Replace("<TargetFramework>" + AvaloniaApp.Metadata("TargetFramework") + "-windows</TargetFramework>",
            "<TargetFrameworks>net10.0-windows;" + AvaloniaApp.Metadata("TargetFramework") + "-windows</TargetFrameworks>", StringComparison.Ordinal)
            .Replace("<UseWPF>true</UseWPF>", "<UseWPF>true</UseWPF><CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)UserBefore.targets</CustomBeforeMicrosoftCommonTargets><CustomAfterMicrosoftCommonTargets>$(MSBuildThisFileDirectory)UserAfter.targets</CustomAfterMicrosoftCommonTargets>", StringComparison.Ordinal);
        File.WriteAllText(project, text);
        Restore.Run(project);
    }

    [AvaloniaTheory]
    [InlineData("Light", false, "net10.0-windows")]
    [InlineData("Dark", false, "net11.0-windows")]
    [InlineData("Light", true, "net10.0-windows")]
    [InlineData("Dark", true, "net11.0-windows")]
    public void Wpf_future_views_use_latest_literals_before_constructor_writes_and_keep_managed_updates_and_user_imports(string theme, bool watched, string framework)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews(theme);
        MainWindow window = FutureStarted(watched, framework);
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            Assert.Single(window.Session.Projects);
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            Element entry = before.Single(e => e.Name == "Entry");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry.Id, Text = "Kept main state", Replace = true }), "the main WPF state to change");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml.cs")));
            Typing.Replace(window, "Name={state.Name};", "Managed Name={state.Name};");
            if (watched)
            {
                Ui.Run(window, AppActions.Save);
                Ui.WaitFor(() => RunOutput(window).Contains("changes applied", StringComparison.OrdinalIgnoreCase), "the SDK's managed WPF edit", TimeSpan.FromMinutes(2));
            }
            else
            {
                HotReloadOutcome managed = Ui.Await(window.Debugger.ApplyChangesAsync(), "Passenger's managed code-behind delta against the hooked normal build");
                Assert.True(managed.Result == HotReloadResult.Applied, managed.Said);
                Assert.Contains(Path.Combine(folder, "MainWindow.xaml.cs"), managed.Files);
                Assert.Contains("WpfTreeApp", string.Join(' ', managed.Modules), StringComparison.Ordinal);
            }
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "Increment").Id }), "the edited code-behind to run");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Managed Name=Kept main state; Count=1; Named=True", StringComparison.Ordinal), "actual managed code execution without a restart");
            Element oldEntry = before.First(e => e.Name == "NewEntry");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = oldEntry.Id, Text = "Kept view state", Replace = true }), "existing child state to change");
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Created first");
            HotReloadOutcome first = FutureApplied(window, watched);
            Assert.True(first.Result == HotReloadResult.Applied, first.Said);
            Typing.Replace(window, "Created first", "Created latest");
            Typing.Replace(window, "XAML before", "XAML latest");
            Typing.Replace(window, "Width=\"200\"", "Width=\"260\" MaxHeight=\"55\"");
            HotReloadOutcome latest = FutureApplied(window, watched);
            Assert.True(latest.Result == HotReloadResult.Applied, latest.Said);
            Capture(app, "future-" + (watched ? "run-" : "debug-") + theme + "-before");
            HashSet<long> old = [.. before.Select(e => e.Id)];
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a genuinely new WPF view to be constructed");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Created latest; XAML latest", StringComparison.Ordinal), "the constructor to observe accepted XAML before its own assignments");
            Assert.Equal(process, app.ProcessId);
            List<Element> after = Read(app);
            Element made = after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id));
            List<Element> children = [.. Flatten(made)];
            Assert.Equal("Created latest", children.Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("Constructor wins", children.Single(e => e.Name == "ConstructorValue").Text);
            ElementDetails details = Ui.Await(app.DetailsAsync(children.Single(e => e.Name == "NewLiteral").Id), "the new WPF view's literal additions");
            Assert.Equal("260", details.Properties.Single(p => p.Name == "Width").Value);
            Assert.Equal("55", details.Properties.Single(p => p.Name == "MaxHeight").Value);
            Assert.Equal(ValueSources.Local, details.Properties.Single(p => p.Name == "MaxHeight").Source);
            Assert.Equal("Kept view state", after.Single(e => e.Id == oldEntry.Id).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = children.Single(e => e.Name == "NewEntry").Id, Text = "New user state", Replace = true }), "the new WPF binding to receive user input");
            Assert.Equal("New user state", Read(app).Single(e => e.Id == children.Single(e => e.Name == "NewBound").Id).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = children.Single(e => e.Name == "NewIncrement").Id }), "the compiled new-view event handler");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("New view Name=New user state; Count=1; Named=True", StringComparison.Ordinal), "the new namescope, state and handler to remain compiled");
            Assert.Contains("Imports=before kept/after kept", FutureOutput(window, watched), StringComparison.Ordinal);
            string normal = Path.Combine(folder, "obj", "Debug", framework);
            Assert.Contains("Before kept " + framework, File.ReadAllText(Path.Combine(normal, "user-before.txt")), StringComparison.Ordinal);
            Assert.Contains("After kept " + framework, File.ReadAllText(Path.Combine(normal, "user-after.txt")), StringComparison.Ordinal);
            Capture(app, "future-" + (watched ? "run-" : "debug-") + theme + "-after");
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    private string UncreatedView(string theme)
    {
        FutureViews(theme);
        string main = Path.Combine(folder, "MainWindow.xaml");
        File.WriteAllText(main, File.ReadAllText(main).Replace("<local:RepeatedView />", "", StringComparison.Ordinal));
        string marker = Path.Combine(Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "wpf-xaml-hot-reload")).FullName,
            "view-initialized-" + Guid.NewGuid().ToString("N") + ".txt");
        string code = Path.Combine(folder, "RepeatedView.xaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("public RepeatedView()", $"static RepeatedView() => System.IO.File.WriteAllText({System.Text.Json.JsonSerializer.Serialize(marker)}, \"Initialized\");\n    public RepeatedView()", StringComparison.Ordinal));
        return marker;
    }

    [AvaloniaTheory]
    [InlineData(false, "Light")]
    [InlineData(true, "Dark")]
    public void An_unconstructed_wpf_view_registers_verified_edits_without_initializing_it_and_uses_latest_values_on_its_first_construction(bool watched, string theme)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string marker = UncreatedView(theme);
        string file = Path.Combine(folder, "RepeatedView.xaml");
        MainWindow window = FutureStarted(watched, "net11.0-windows");
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            Assert.DoesNotContain(before, e => e.Type == "RepeatedView");
            Assert.False(File.Exists(marker));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = before.Single(e => e.Name == "Entry").Id, Text = "Kept unconstructed-view state", Replace = true }), "the main state before any child exists");
            before = Read(app);
            Assert.Null(window.OpenFile(file));
            Typing.Replace(window, "Width=\"200\"", "Width=\"invalid\"");
            Typing.Replace(window, "Created before", "Registered first");
            HotReloadOutcome invalid = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, invalid.Result);
            Assert.Contains(invalid.Problems, p => p.Path == file && p.Message.Contains("Width", StringComparison.Ordinal));
            Assert.False(File.Exists(marker));
            Typing.Replace(window, "Width=\"invalid\"", "Width=\"260\" MaxHeight=\"55\"");
            HotReloadOutcome first = FutureApplied(window, watched);
            Assert.True(first.Result == HotReloadResult.RegisteredForFutureViews, first.Said);
            Assert.Equal(1, first.FutureViewRegistrations);
            Assert.Equal(0, first.LiveXamlProperties);
            Assert.Contains("registered", first.Said, StringComparison.Ordinal);
            Assert.DoesNotContain("live", first.Said, StringComparison.Ordinal);
            Assert.Equal([file], first.Files);
            Assert.False(File.Exists(marker));
            Assert.Equal(before.Select(e => e.Id), Read(app).Select(e => e.Id));
            Typing.Replace(window, "Registered first", "Registered latest");
            Typing.Replace(window, "XAML before", "Latest before constructor");
            Typing.Replace(window, "Width=\"260\"", "Width=\"280\"");
            HotReloadOutcome latest = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.RegisteredForFutureViews, latest.Result);
            Assert.Equal(1, latest.FutureViewRegistrations);
            Assert.Equal(0, latest.LiveXamlProperties);
            Assert.False(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.False(File.Exists(marker));
            Assert.DoesNotContain(Read(app), e => e.Type == "RepeatedView");
            window.ApplySettings(window.Settings with { Theme = theme == "Light" ? ThemeChoice.Light : ThemeChoice.Dark });
            window.SetRenderScaling(2);
            Ui.Settle();
            UiAudit.Capture(window, "wpf-unconstructed-registered-" + (watched ? "run-" : "debug-") + theme);
            Capture(app, "unconstructed-" + (watched ? "run-" : "debug-") + theme + "-before");
            Typing.Replace(window, "Width=\"280\"", "Width=\"invalid again\"");
            Typing.Replace(window, "Registered latest", "Refused future text");
            HotReloadOutcome refused = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, refused.Result);
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.False(File.Exists(marker));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "the first WPF view to be created through its compiled loader");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Registered latest; Latest before constructor", StringComparison.Ordinal), "the first constructor to observe the registered edit before its own assignments");
            Assert.True(File.Exists(marker));
            List<Element> after = Read(app);
            List<Element> children = [.. Flatten(after.Single(e => e.Type == "RepeatedView"))];
            Assert.Equal("Registered latest", children.Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("Constructor wins", children.Single(e => e.Name == "ConstructorValue").Text);
            ElementDetails details = Ui.Await(app.DetailsAsync(children.Single(e => e.Name == "NewLiteral").Id), "the first view's registered property values");
            Assert.Equal("280", details.Properties.Single(p => p.Name == "Width").Value);
            Assert.Equal("55", details.Properties.Single(p => p.Name == "MaxHeight").Value);
            Assert.Equal(ValueSources.Local, details.Properties.Single(p => p.Name == "MaxHeight").Source);
            Assert.Equal("Kept unconstructed-view state", after.Single(e => e.Name == "Entry").Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = children.Single(e => e.Name == "NewEntry").Id, Text = "First view state", Replace = true }), "the first compiled view's binding");
            Assert.Equal("First view state", Read(app).Single(e => e.Id == children.Single(e => e.Name == "NewBound").Id).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = children.Single(e => e.Name == "NewIncrement").Id }), "the first view's compiled namescope and event handler");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("New view Name=First view state; Count=1; Named=True", StringComparison.Ordinal), "the first view's code-behind and state");
            Assert.Same(refused, watched ? window.Session.RunXaml.LastApplied : window.Debugger.LastApplied);
            Typing.Replace(window, "Width=\"invalid again\"", "Width=\"280\"");
            Typing.Replace(window, "Refused future text", "Now live");
            HotReloadOutcome live = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Applied, live.Result);
            Assert.Equal(0, live.FutureViewRegistrations);
            Assert.True(live.LiveXamlProperties > 0);
            Assert.Equal("Now live", Read(app).Single(e => e.Name == "NewLiteral").Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a second view to receive the same latest accepted source");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Now live; Latest before constructor", StringComparison.Ordinal), "later views after future-only registration becomes live");
            Assert.Equal(2, Read(app).Count(e => e.Type == "RepeatedView"));
            Assert.Equal(process, app.ProcessId);
            Capture(app, "unconstructed-" + (watched ? "run-" : "debug-") + theme + "-after");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Unconstructed WPF boundary failed: {error.Message}\n{FutureOutput(window, watched)}", error);
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaTheory]
    [InlineData("stale")]
    [InlineData("symbols")]
    [InlineData("custom")]
    [InlineData("module")]
    [InlineData("ambiguous")]
    public void Unconstructed_wpf_sources_with_unverified_baselines_or_custom_properties_require_restart_without_running_user_initializers(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string marker = UncreatedView("Light");
        string file = Path.Combine(folder, "RepeatedView.xaml");
        if (kind == "symbols")
        {
            string project = Path.Combine(folder, "WpfTreeApp.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<UseWPF>true</UseWPF>", "<UseWPF>true</UseWPF><DebugType>none</DebugType>", StringComparison.Ordinal));
        }
        else if (kind == "custom")
        {
            File.WriteAllText(file, File.ReadAllText(file).Replace("x:Class=\"WpfTreeApp.RepeatedView\"", "xmlns:local=\"clr-namespace:WpfTreeApp\" x:Class=\"WpfTreeApp.RepeatedView\"", StringComparison.Ordinal)
                .Replace("<TextBlock x:Name=\"NewLiteral\"", "<local:CustomText x:Name=\"NewLiteral\"", StringComparison.Ordinal));
            File.AppendAllText(Path.Combine(folder, "RepeatedView.xaml.cs"), "\npublic class CustomText : TextBlock { }\n");
        }
        else if (kind == "ambiguous")
        {
            string duplicate = Directory.CreateDirectory(Path.Combine(folder, "Duplicate")).FullName;
            File.WriteAllText(Path.Combine(duplicate, "Duplicate.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0-windows;net11.0-windows</TargetFrameworks><UseWPF>true</UseWPF><CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)../UserBefore.targets</CustomBeforeMicrosoftCommonTargets><CustomAfterMicrosoftCommonTargets>$(MSBuildThisFileDirectory)../UserAfter.targets</CustomAfterMicrosoftCommonTargets></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(duplicate, "View.cs"), "namespace WpfTreeApp { public class RepeatedView : System.Windows.Controls.UserControl { } } namespace Duplicate { public static class Anchor { public static void Load() { } } }");
            string project = Path.Combine(folder, "WpfTreeApp.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "<PropertyGroup><NoWarn>$(NoWarn);CS0436</NoWarn></PropertyGroup><ItemGroup><Compile Remove=\"Duplicate/**/*.cs\" /><ProjectReference Include=\"Duplicate/Duplicate.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));
            string main = Path.Combine(folder, "MainWindow.xaml.cs");
            File.WriteAllText(main, File.ReadAllText(main).Replace("InitializeComponent();", "InitializeComponent(); Duplicate.Anchor.Load();", StringComparison.Ordinal));
            Restore.Run(project);
        }
        MainWindow window = FutureStarted(false, "net11.0-windows");
        string? heldModule = null;
        string module = Path.Combine(folder, "bin", "Debug", "net11.0-windows", "WpfTreeApp.dll");
        try
        {
            LiveApp app = window.LiveApp!;
            List<Element> before = Read(app);
            if (kind == "stale")
            {
                File.WriteAllText(file, File.ReadAllText(file).Replace("Created before", "Not the compiled text", StringComparison.Ordinal));
            }
            else if (kind == "module")
            {
                byte[] bytes = File.ReadAllBytes(module);
                using (PEReader pe = new(new MemoryStream(bytes, writable: false)))
                {
                    MetadataReader metadata = pe.GetMetadataReader();
                    byte[] original = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToByteArray();
                    int at = bytes.AsSpan().IndexOf(original);
                    Assert.True(at >= 0);
                    Assert.Equal(-1, bytes.AsSpan(at + original.Length).IndexOf(original));
                    Guid.NewGuid().TryWriteBytes(bytes.AsSpan(at, original.Length));
                }
                heldModule = module + ".held";
                File.Move(module, heldModule);
                File.WriteAllBytes(module, bytes);
            }
            Assert.Null(window.OpenFile(file));
            Typing.Replace(window, kind == "stale" ? "Not the compiled text" : "Created before", "Must not register");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            string reason = kind switch
            {
                "stale" => "compiled checksum",
                "symbols" => "portable symbols",
                "module" => "assembly on disk",
                "ambiguous" => "single loaded WPF view type",
                _ => "built-in WPF",
            };
            Assert.Contains(refused.Problems, p => p.Path == file && p.Message.Contains(reason, StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.False(File.Exists(marker));
            Assert.Equal(before.Select(e => e.Id), Read(app).Select(e => e.Id));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "the refused view to keep its normal compiled construction");
            Ui.WaitFor(() => Output(window).Contains("Constructor saw Created before; XAML before", StringComparison.Ordinal), "the ordinary first view after refused registration");
            Assert.True(File.Exists(marker));
            Assert.Equal("Created before", Read(app).Single(e => e.Name == "NewLiteral").Text);
        }
        finally
        {
            if (heldModule is not null)
            {
                File.Delete(module);
                File.Move(heldModule, module);
            }
            FutureEnded(window, false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_first_wpf_population_failure_keeps_its_completed_registration_and_pending_text(bool watched)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string marker = UncreatedView("Dark");
        string file = Path.Combine(folder, "RepeatedView.xaml");
        File.WriteAllText(file, File.ReadAllText(file).Replace("x:Class=\"WpfTreeApp.RepeatedView\"", "x:Class=\"WpfTreeApp.RepeatedView\" Width=\"200\"", StringComparison.Ordinal));
        string code = Path.Combine(folder, "RepeatedView.xaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("static RepeatedView() =>", "static RepeatedView() { WidthProperty.OverrideMetadata(typeof(RepeatedView), new FrameworkPropertyMetadata(0d, (_, changed) => { if ((double)changed.NewValue == 260d) { throw new InvalidOperationException(\"First population refused\"); } }));", StringComparison.Ordinal)
            .Replace("\"Initialized\");", "\"Initialized\"); }", StringComparison.Ordinal));
        MainWindow window = FutureStarted(watched, "net11.0-windows");
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            Assert.Null(window.OpenFile(file));
            Typing.Replace(window, "Width=\"200\"", "Width=\"260\"");
            HotReloadOutcome registered = FutureApplied(window, watched);
            Assert.True(registered.Result == HotReloadResult.RegisteredForFutureViews, registered.Said);
            Assert.Equal(1, registered.FutureViewRegistrations);
            Assert.False(File.Exists(marker));
            Typing.Replace(window, "Created before", "Pending first-view text");
            XamlUpdateResult? observed = null;
            app.XamlFailed += (_, result) => observed = result;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the first population with runtime property metadata to refuse its registered value");
            Ui.WaitFor(() => observed is not null, "the first population's separate failure event");
            Assert.True(File.Exists(marker));
            Assert.Same(registered, watched ? window.Session.RunXaml.LastApplied : window.Debugger.LastApplied);
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.Contains(observed!.Problems, p => p.File == file && p.RestartRequired && p.Message.Contains("First population refused", StringComparison.Ordinal));
            Assert.Contains(window.BottomPanel.Problems.HotReloadProblems!, p => p.Path == file && p.Message.Contains("newly created", StringComparison.Ordinal));
            Assert.Contains("newly created view", window.Status!, StringComparison.Ordinal);
            Assert.Contains("Pending first-view text", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Equal("Created before", Read(app).Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("200", Ui.Await(app.DetailsAsync(Read(app).Single(e => e.Type == "RepeatedView").Id), "the refused first view's compiled width").Properties.Single(p => p.Name == "Width").Value);
            window.SetRenderScaling(2);
            Ui.Settle();
            UiAudit.Capture(window, "wpf-unconstructed-failure-" + (watched ? "run" : "debug"));
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Future_wpf_registrations_and_live_edits_refuse_together_then_report_their_separate_counts_on_retry(bool watched)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string marker = UncreatedView("Light");
        string file = Path.Combine(folder, "RepeatedView.xaml");
        string main = Path.Combine(folder, "MainWindow.xaml");
        File.WriteAllText(main, File.ReadAllText(main).Replace("<StackPanel x:Name=\"NewViews\" />", "<Button x:Name=\"RemoveViews\" Content=\"Remove views\" Click=\"RemoveViewsClicked\" /><StackPanel x:Name=\"NewViews\" />", StringComparison.Ordinal));
        string code = Path.Combine(folder, "MainWindow.xaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("private void Clicked", "private void RemoveViewsClicked(object sender, RoutedEventArgs e) => NewViews.Children.Clear();\n    private void Clicked", StringComparison.Ordinal));
        MainWindow window = FutureStarted(watched, "net11.0-windows");
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            Assert.Null(window.OpenFile(file));
            Typing.Replace(window, "Created before", "Registered with live edit");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Live batch\"");
            Typing.Replace(window, "Width=\"640\"", "Width=\"invalid\"");
            HotReloadOutcome invalid = FutureApplied(window, watched, all: true);
            Assert.Equal(HotReloadResult.Errors, invalid.Result);
            Assert.Empty(invalid.Files);
            Assert.Equal(0, invalid.FutureViewRegistrations);
            Assert.Equal(0, invalid.LiveXamlProperties);
            Assert.Contains(Read(app), e => e.Text == "Before");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.False(File.Exists(marker));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "a real view to prove the refused batch installed no future edit");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Created before; XAML before", StringComparison.Ordinal), "the refused batch to keep normal compiled population");
            Assert.Equal("Created before", Read(app).Single(e => e.Name == "NewLiteral").Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "RemoveViews").Id }), "the probe view to leave the live tree before retry");
            Assert.DoesNotContain(Read(app), e => e.Type == "RepeatedView");
            Typing.Replace(window, "Width=\"invalid\"", "Width=\"660\"");
            HotReloadOutcome applied = FutureApplied(window, watched, all: true);
            Assert.True(applied.Result == HotReloadResult.Applied, applied.Said);
            Assert.Equal(1, applied.FutureViewRegistrations);
            Assert.Equal(2, applied.LiveXamlProperties);
            Assert.Contains("2 live properties; registered for 1 future view type", applied.Said, StringComparison.Ordinal);
            Assert.Equal(2, applied.Files.Count);
            Assert.False(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.True(File.Exists(marker));
            Assert.Contains(Read(app), e => e.Text == "Live batch");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the future half of the accepted mixed batch to run");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Registered with live edit; XAML before", StringComparison.Ordinal), "the mixed batch's actual first view");
            Assert.Equal(process, app.ProcessId);
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaFact]
    public void A_previous_inspector_protocol_cannot_acknowledge_future_view_registrations()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        // This protocol-only peer has no window to claim the test's dispatcher.
        UiThread.Own();
        LaunchPlan plan = new LaunchPlan { Name = "Older WPF inspector", Program = "dotnet", WorkingDirectory = folder, Built = Path.Combine(folder, "WpfTreeApp.dll") };
        using LiveApp app = LiveApp.For(plan, expected: LiveAppKind.Wpf)!;
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Task connecting = socket.ConnectAsync(new UnixDomainSocketEndPoint(app.SocketPath), CancellationToken.None).AsTask();
        Ui.WaitFor(() => connecting.IsCompleted, "the older protocol peer to connect over the real inspector socket");
        connecting.GetAwaiter().GetResult();
        using NetworkStream stream = new(socket, ownsSocket: false);
        using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Reply hello = new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version - 1, Environment.ProcessId, null, null) { Framework = "WPF" } };
        Task writing = writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(hello, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        Ui.WaitFor(() => writing.IsCompleted, "the previous-version hello");
        writing.GetAwaiter().GetResult();
        Ui.WaitFor(() => app.State == LiveAppState.Refused, "the older inspector to be refused before any registration request");
        Assert.Contains($"version {Wire.Version - 1}", app.Problem!, StringComparison.Ordinal);
        Assert.Contains($"not {Wire.Version}", app.Problem!, StringComparison.Ordinal);
        LiveAppException refused = Assert.Throws<LiveAppException>(() => Ui.Await(app.ApplyXamlAsync(new XamlUpdate([])), "a refused older inspector's registration request"));
        Assert.Equal(app.Problem, refused.Message);
        Assert.Equal(0, socket.Available);
    }

    private MainWindow FutureStarted(bool watched, string framework, string? inspectorTrace = null)
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.Settle();
            RunConfiguration selected = window.Session.Configurations.All.Single(c => PathNames.Same(c.Project, Path.Combine(folder, "WpfTreeApp.csproj")))
                with { Name = "WPF " + framework, Framework = framework, Generated = false };
            if (inspectorTrace is not null)
            {
                selected = selected with { Environment = new Dictionary<string, string>(selected.Environment) { ["PASSENGER_INSPECTOR_TRACE"] = inspectorTrace } };
            }
            window.Session.Configurations.Set([selected], selected.Name, window.Session.Projects);
            if (watched)
            {
                window.ApplySettings(window.Settings with { HotReload = true, RestartOnRudeEdit = false });
                Assert.Null(Ui.Await(window.RunAsync(), "the hooked WPF watch run", TimeSpan.FromMinutes(3)));
                Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected && RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal)
                    || !window.IsRunning || window.Session.RunApp?.State is LiveAppState.Refused or LiveAppState.Ended,
                    "the hooked WPF target and file watchers", TimeSpan.FromMinutes(2));
                Assert.True(window.IsRunning && window.Session.RunApp?.State == LiveAppState.Connected, RunOutput(window));
            }
            else
            {
                string? problem = Ui.Await(window.DebugAsync(), "the hooked WPF debug run", TimeSpan.FromMinutes(3));
                Assert.True(problem is null, $"{problem}\n{window.BottomPanel.Build.Document.Snapshot}");
                Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected, "the hooked WPF inspector");
            }
            return window;
        }
        catch (Exception error)
        {
            string build = window.BottomPanel.Build.Document.Snapshot.ToString();
            string run = RunOutput(window);
            Ui.Run(window, AppActions.Stop);
            window.Close();
            throw new InvalidOperationException($"WPF launch failed: {error.Message}\nBuild:\n{build}\nRun:\n{run}", error);
        }
    }

    [AvaloniaTheory]
    [InlineData("Debug", "net10.0-windows", "Light")]
    [InlineData("Release", "net11.0-windows", "Dark")]
    public void Optimized_wpf_views_keep_compiled_population_and_constructor_order_with_latest_xaml(string configuration, string framework, string theme)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews(theme);
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        if (configuration == "Debug")
        {
            // Keep one optimized fixture on the checkout's compiler as well as the normally selected SDK. Their
            // null-conditional helper IL differs (stacked callback versus a local with a direct null return).
            File.Copy(Path.Combine(Debuggees.Root, "global.json"), Path.Combine(folder, "global.json"));
            Restore.Run(project);
        }
        File.WriteAllText(project, File.ReadAllText(project).Replace("<UseWPF>true</UseWPF>",
            $"<UseWPF>true</UseWPF><Configuration>{configuration}</Configuration><Optimize>true</Optimize><XamlDebuggingInformation>true</XamlDebuggingInformation>", StringComparison.Ordinal));
        string code = Path.Combine(folder, "RepeatedView.xaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("InitializeComponent();", """
            InitializeComponent();
            System.Diagnostics.DebuggableAttribute debug = (System.Diagnostics.DebuggableAttribute)typeof(RepeatedView).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)[0];
            Console.WriteLine("Optimized=" + !debug.IsJITOptimizerDisabled);
            #if DEBUG
            Console.WriteLine("Configuration=Debug");
            #else
            Console.WriteLine("Configuration=Release");
            #endif
            """, StringComparison.Ordinal));
        MainWindow window = FutureStarted(watched: true, framework);
        try
        {
            LiveApp app = window.Session.RunApp!;
            Assert.Contains(Path.Combine("bin", configuration, framework), app.Plan!.Built!, StringComparison.Ordinal);
            Assert.Contains("Configuration=" + configuration, RunOutput(window), StringComparison.Ordinal);
            Assert.Contains("Optimized=True", RunOutput(window), StringComparison.Ordinal);
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            HashSet<long> old = [.. before.Select(e => e.Id)];
            Element oldEntry = before.Single(e => e.Name == "NewEntry");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = oldEntry.Id, Text = "Kept optimized state", Replace = true }), "the existing optimized view's state");
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Optimized first");
            HotReloadOutcome first = FutureApplied(window, watched: true);
            Assert.True(first.Result == HotReloadResult.Applied, first.Said + "\n" + string.Join('\n', first.Problems.Select(p => p.Message)) + "\n" + RunOutput(window));
            Typing.Replace(window, "Optimized first", "Optimized latest");
            Typing.Replace(window, "XAML before", "Optimized constructor input");
            Typing.Replace(window, "Width=\"200\"", "Width=\"260\" MaxHeight=\"55\"");
            HotReloadOutcome latest = FutureApplied(window, watched: true);
            Assert.True(latest.Result == HotReloadResult.Applied, latest.Said);
            Capture(app, "optimized-" + configuration + "-" + theme + "-before");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a new optimized WPF view to load its compiled BAML");
            Ui.WaitFor(() => RunOutput(window).Contains("Constructor saw Optimized latest; Optimized constructor input", StringComparison.Ordinal), "the optimized constructor to see the accepted literals before its assignments");
            Assert.Equal(process, app.ProcessId);
            List<Element> after = Read(app);
            Element made = after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id));
            List<Element> children = [.. Flatten(made)];
            Assert.Equal("Optimized latest", children.Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("Constructor wins", children.Single(e => e.Name == "ConstructorValue").Text);
            Assert.Equal("Kept optimized state", after.Single(e => e.Id == oldEntry.Id).Text);
            ElementDetails details = Ui.Await(app.DetailsAsync(children.Single(e => e.Name == "NewLiteral").Id), "the optimized new view's literal addition");
            Assert.Equal("55", details.Properties.Single(p => p.Name == "MaxHeight").Value);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = children.Single(e => e.Name == "NewEntry").Id, Text = "New optimized state", Replace = true }), "the compiled optimized binding");
            Assert.Equal("New optimized state", Read(app).Single(e => e.Id == children.Single(e => e.Name == "NewBound").Id).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = children.Single(e => e.Name == "NewIncrement").Id }), "the optimized code-behind event");
            Ui.WaitFor(() => RunOutput(window).Contains("New view Name=New optimized state; Count=1; Named=True", StringComparison.Ordinal), "the optimized namescope and event handler");
            Capture(app, "optimized-" + configuration + "-" + theme + "-after");
        }
        finally
        {
            FutureEnded(window, watched: true);
        }
    }

    [AvaloniaFact]
    public void The_build_pane_stop_cancels_wpf_preparation_before_a_build_process_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        MainWindow window = new() { Width = 1200, Height = 800 };
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        string original = File.ReadAllText(project);
        string pipe = "passenger-build-stop-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(pipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<BuildOutcome>? building = null;
        try
        {
            window.Show();
            window.OpenFolder(folder);
            Ui.Settle();
            File.WriteAllText(project, original.Replace("</Project>", $"<PropertyGroup Condition=\"'$(DotNetWatchBuild)' == 'true'\"><HeldPopulationRead>$([System.IO.File]::ReadAllText('\\\\.\\pipe\\{pipe}'))</HeldPopulationRead></PropertyGroup></Project>", StringComparison.Ordinal));
            Task connected = server.WaitForConnectionAsync();
            building = window.BuildAsync();
            Ui.PumpUntil(() => connected.IsCompleted || building.IsCompleted, "WPF preparation to wait on its import", TimeSpan.FromSeconds(30));
            Assert.True(connected.IsCompletedSuccessfully && !building.IsCompleted, "Build did not wait for preparation");
            Assert.False(window.Session.Builder.IsRunning);
            Button stop = window.BottomPanel.Build.GetVisualDescendants().OfType<Button>().Single(b => (string?)ToolTip.GetTip(b) == "Stop");
            Assert.True(stop.IsEffectivelyVisible && stop.IsEnabled);
            Point at = stop.TranslatePoint(new Point(stop.Bounds.Width / 2, stop.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Assert.True(building.IsCompletedSuccessfully, "The Build pane's Stop ignored preparation before process startup");
            Assert.Equal(BuildStatus.Stopped, building.Result.Status);
            File.WriteAllText(project, original);
            server.Write(Encoding.UTF8.GetBytes("Released by the test"));
            server.Dispose();
            Ui.Settle();
            Assert.False(window.IsBuilding);
            Assert.False(window.Session.Builder.IsRunning);
            Assert.Equal(BuildStatus.Stopped, building.Result.Status);
            BuildOutcome fresh = Ui.Await(window.BuildAsync(), "a fresh build after stopping preparation", TimeSpan.FromMinutes(2));
            Assert.True(fresh.Succeeded, fresh.Message + "\n" + window.BottomPanel.Build.Document.Snapshot);
        }
        finally
        {
            File.WriteAllText(project, original);
            server.Dispose();
            window.Perform(AppActions.Stop);
            if (building is { IsCompleted: false })
            {
                Ui.PumpUntil(() => building.IsCompleted, "the held build to finish cleanup", TimeSpan.FromSeconds(30));
            }
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Stopping_or_closing_during_wpf_run_preparation_never_launches_afterward(bool watched, bool close)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        MainWindow window = FutureStarted(false, "net11.0-windows");
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        string original = File.ReadAllText(project);
        string pipe = "passenger-wpf-prepare-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(pipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        ConcurrentQueue<ChildStart> starts = new();
        void Started(object? sender, ChildStart child) => starts.Enqueue(child);
        Task<string?>? launching = null;
        ProjectSession session = window.Session;
        try
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the initial WPF debug run to end");
            RunConfiguration chosen = window.Session.Configurations.Chosen! with { BuildBeforeLaunch = false };
            window.Session.Configurations.Set([chosen], chosen.Name, window.Session.Projects);
            window.ApplySettings(window.Settings with { HotReload = watched });
            window.SetRenderScaling(2);
            // Only preparation's untouched watch-context evaluation reads this pipe. The project was already built;
            // its ordinary evaluation and launch-plan query do not set DotNetWatchBuild.
            File.WriteAllText(project, original.Replace("</Project>", $"<PropertyGroup Condition=\"'$(DotNetWatchBuild)' == 'true'\"><HeldPopulationRead>$([System.IO.File]::ReadAllText('\\\\.\\pipe\\{pipe}'))</HeldPopulationRead></PropertyGroup></Project>", StringComparison.Ordinal));
            Task connected = server.WaitForConnectionAsync();
            ChildProcesses.Started += Started;
            launching = window.RunAsync();
            Ui.PumpUntil(() => connected.IsCompleted || launching.IsCompleted, "the shared WPF preparation to read its held import", TimeSpan.FromSeconds(30));
            Assert.True(connected.IsCompletedSuccessfully && !launching.IsCompleted, $"Run did not wait for preparation: {launching.Status}");
            Ui.PumpUntil(() => starts.Any(child => child.Command.Contains("-getProperty:TargetPath", StringComparison.Ordinal) && Processes.Gone(child.ProcessId)),
                "Run's launch-plan query to finish before canceling its preparation", TimeSpan.FromSeconds(30));
            Screenshot.Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(launching.IsCompleted, "Run did not stay behind the held shared preparation");
            Assert.True(window.FindControl<Button>("StopButton")!.IsEffectivelyVisible, "The toolbar hides Stop while Run prepares its program");
            UiAudit.Capture(window, "wpf-run-preparing-" + (watched ? "watch" : "normal") + (close ? "-close" : "-stop"));
            bool stopped = close ? window.CloseSolution() is null : window.Perform(AppActions.Stop);
            Ui.PumpUntil(() => launching.IsCompleted, "Run cancellation without releasing shared preparation", TimeSpan.FromSeconds(5));
            // Release the actual Host evaluation even on the negative control, so the assertion observes any late
            // process launch instead of stranding a shared project host behind a failed test.
            File.WriteAllText(project, original);
            server.Write(Encoding.UTF8.GetBytes("Released by the test"));
            server.Dispose();
            Ui.PumpUntil(() => launching.IsCompleted, "the canceled WPF Run to answer", TimeSpan.FromSeconds(30));
            string? result = launching.GetAwaiter().GetResult();
            Assert.DoesNotContain(starts, child => child.Command.Contains("WpfTreeApp.dll", StringComparison.Ordinal)
                || child.Command.Contains("watch", StringComparison.Ordinal) && child.Command.Contains(project, StringComparison.OrdinalIgnoreCase));
            Assert.True(stopped, "Stop was unavailable while Run was preparing");
            Assert.NotNull(result);
            Assert.Contains(close ? "closed" : "cancel", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(window.IsRunning, "The canceled Run still owns a process");
            Assert.True(session.RunApp is null or { State: LiveAppState.Ended }, "The canceled Run left an inspector waiting for a target");
            ChildProcesses.Started -= Started;
            if (!close)
            {
                Assert.Null(Ui.Await(window.RunAsync(), "a fresh Run after cancellation", TimeSpan.FromMinutes(2)));
                Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected, "the fresh run's WPF inspector", TimeSpan.FromMinutes(2));
                Assert.True(window.IsRunning, "Stopping preparation permanently disabled later Run");
                Assert.True(window.Perform(AppActions.Stop));
                Ui.PumpUntil(() => window.RunEnd.IsCompleted, "the fresh WPF run to stop", TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            ChildProcesses.Started -= Started;
            File.WriteAllText(project, original);
            server.Dispose();
            if (launching is { IsCompleted: false })
            {
                Ui.PumpUntil(() => launching.IsCompleted, "held preparation cleanup", TimeSpan.FromSeconds(30));
            }
            if (session.Runner.IsRunning)
            {
                int? process = session.Runner.ProcessId;
                session.Runner.Stop();
                if (process is { } id)
                {
                    Ui.PumpUntil(() => Processes.Gone(id), "the lifecycle fixture's program to end", TimeSpan.FromSeconds(10));
                }
            }
            window.Perform(AppActions.Stop);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Literal_property_elements_update_their_parent_and_future_views_without_replacing_state(bool watched)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Dark");
        string main = Path.Combine(folder, "MainWindow.xaml");
        File.WriteAllText(main, File.ReadAllText(main).Replace("Text=\"Attached\" Grid.Row=\"0\" />", "Text=\"Attached\"><Grid.Row>0</Grid.Row></TextBlock>", StringComparison.Ordinal));
        string child = Path.Combine(folder, "RepeatedView.xaml");
        File.WriteAllText(child, File.ReadAllText(child).Replace("Text=\"Created before\" Width=\"200\" />", "Width=\"200\"><TextBlock.Text>Created before</TextBlock.Text></TextBlock>", StringComparison.Ordinal));
        string trace = Path.Combine(Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "wpf-input-traces")).FullName,
            $"property-elements-{watched}-{Guid.NewGuid():N}.log");
        MainWindow window = FutureStarted(watched, "net11.0-windows", trace);
        HotReloadOutcome ApplyEdit()
        {
            if (watched)
            {
                return FutureApplied(window, true);
            }
            HotReloadOutcome? before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is { } current && !ReferenceEquals(before, current), "the property-element edit to apply through the Debug action");
            return window.Debugger.LastApplied!;
        }
        try
        {
            window.ApplySettings(window.Settings with { RestartOnRudeEdit = false });
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            HashSet<long> old = [.. before.Select(e => e.Id)];
            long oldEntry = before.Single(e => e.Name == "NewEntry").Id;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = oldEntry, Text = "Kept property-element state", Replace = true }), "existing WPF state");
            Assert.Null(window.OpenFile(main));
            int rowLine = Array.FindIndex(File.ReadAllLines(main), line => line.Contains("<Grid.Row>", StringComparison.Ordinal)) + 1;
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Property batch applied\"");
            Typing.Replace(window, "<Grid.Row>0</Grid.Row>", "<Grid.Row>invalid</Grid.Row>");
            HotReloadOutcome invalid = ApplyEdit();
            Assert.Equal(HotReloadResult.Errors, invalid.Result);
            Assert.Contains(invalid.Problems, p => p.Path == main && p.Position.Line == rowLine - 1 && p.Message.Contains("Grid.Row", StringComparison.Ordinal));
            Assert.Contains(Read(app), e => e.Text == "Before");
            Assert.DoesNotContain(Read(app), e => e.Text == "Property batch applied");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Typing.Replace(window, "<Grid.Row>invalid</Grid.Row>", "<Grid.Row>1</Grid.Row>");
            HotReloadOutcome attached = ApplyEdit();
            Assert.True(attached.Result == HotReloadResult.Applied, attached.Said);
            ElementDetails attachedDetails = Ui.Await(app.DetailsAsync(Read(app).Single(e => e.Text == "Attached").Id), "the WPF attached property-element value");
            Assert.Equal("1", attachedDetails.Properties.Single(p => p.Name == "Grid.Row").Value);
            Assert.Null(window.OpenFile(child));
            Typing.Replace(window, "<TextBlock.Text>Created before</TextBlock.Text>", "<TextBlock.Text>Property first</TextBlock.Text>");
            HotReloadOutcome first = ApplyEdit();
            Assert.True(first.Result == HotReloadResult.Applied, first.Said);
            Typing.Replace(window, "<TextBlock.Text>Property first</TextBlock.Text>", "<TextBlock.Text>Property latest</TextBlock.Text>");
            HotReloadOutcome latest = ApplyEdit();
            Assert.True(latest.Result == HotReloadResult.Applied, latest.Said);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a future WPF view with property-element literals");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Property latest; XAML before", StringComparison.Ordinal), "the constructor to see the property-element update");
            List<Element> after = Read(app);
            List<Element> made = [.. Flatten(after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id)))];
            Assert.Equal("Property latest", made.Single(e => e.Name == "NewLiteral").Text);
            Assert.Equal("Constructor wins", made.Single(e => e.Name == "ConstructorValue").Text);
            Assert.Equal("Kept property-element state", after.Single(e => e.Id == oldEntry).Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = made.Single(e => e.Name == "NewEntry").Id, Text = "New property-element state", Replace = true }), "the future WPF binding");
            Assert.Equal("New property-element state", Read(app).Single(e => e.Id == made.Single(e => e.Name == "NewBound").Id).Text);
            Assert.Equal(process, app.ProcessId);
            Capture(app, "property-elements-" + (watched ? "run" : "debug"));
            Typing.Replace(window, "<TextBlock.Text>Property latest</TextBlock.Text>", "<TextBlock.Text>{Binding Name}</TextBlock.Text>");
            HotReloadOutcome markup = ApplyEdit();
            Assert.Equal(HotReloadResult.RestartNeeded, markup.Result);
            Assert.Contains(markup.Problems, p => p.Path == child && p.Message.Contains("markup", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, Read(app).Count(e => e.Name == "NewLiteral" && e.Text == "Property latest"));
            Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the unsupported WPF property body to offer a restart");
            ChoiceDialog asked = window.OwnedWindows.OfType<ChoiceDialog>().Single();
            Ui.Settle();
            Button never = asked.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Never");
            Avalonia.Point at = never.TranslatePoint(new Avalonia.Point(never.Bounds.Width / 2, never.Bounds.Height / 2), asked)!.Value;
            asked.MouseMove(at);
            asked.MouseDown(at, Avalonia.Input.MouseButton.Left);
            asked.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.Settle();
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Typing.Replace(window, "<TextBlock.Text>{Binding Name}</TextBlock.Text>", "<TextBlock.Text>Property latest</TextBlock.Text>");
            Typing.Replace(window, "x:Name=\"NewLiteral\"", "x:Name=\"NewLiteral\" Text=\"Duplicate\"");
            HotReloadOutcome duplicate = ApplyEdit();
            Assert.Equal(HotReloadResult.Errors, duplicate.Result);
            Assert.Contains(duplicate.Problems, p => p.Path == child && p.Message.Contains("both an attribute and a property element", StringComparison.Ordinal));
            Assert.DoesNotContain(Read(app), e => e.Text == "Duplicate");
            Typing.Replace(window, "x:Name=\"NewLiteral\" Text=\"Duplicate\"", "x:Name=\"NewLiteral\"");
            Typing.Replace(window, "<TextBlock.Text>Property latest</TextBlock.Text>", "<TextBlock.Text> Property latest </TextBlock.Text>");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            HotReloadOutcome whitespace = ApplyEdit();
            Assert.Equal(HotReloadResult.RestartNeeded, whitespace.Result);
            Assert.Contains(whitespace.Problems, p => p.Path == child && p.Message.Contains("whitespace", StringComparison.Ordinal));
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            // Debug declines stop asking until the next edit; Run keeps the saved batch pending.
            Assert.Equal(watched, watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Typing.Replace(window, "<TextBlock.Text> Property latest </TextBlock.Text>", "<TextBlock.>Property latest</TextBlock.>");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            HotReloadOutcome incomplete = ApplyEdit();
            Assert.Equal(HotReloadResult.RestartNeeded, incomplete.Result);
            Assert.Contains(incomplete.Problems, p => p.Path == child && p.Message.Contains("structure", StringComparison.Ordinal));
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Assert.Equal(process, app.ProcessId);
            Assert.Equal(2, Read(app).Count(e => e.Name == "NewLiteral" && e.Text == "Property latest"));
        }
        finally
        {
            string observed = Ui.Shared(trace);
            string output = FutureOutput(window, watched);
            TestContext.Current.TestOutputHelper?.WriteLine($"WPF property-element target trace {trace}:\n{observed}\nTarget output:\n{output}");
            TestContext.Current.AddAttachment("WPF property-element output", Encoding.UTF8.GetBytes(output), "text/plain");
            if (observed.Length > 0)
            {
                TestContext.Current.AddAttachment("WPF property-element input phases", Encoding.UTF8.GetBytes(observed), "text/plain");
            }
            FutureEnded(window, watched);
        }
    }

    [AvaloniaFact]
    public void Canceling_a_run_after_its_inspector_is_created_keeps_open_xaml_followed_on_the_next_watched_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        MainWindow window = FutureStarted(false, "net11.0-windows");
        ProjectSession session = window.Session;
        bool stopped = false;
        void Cancel(object? sender, EventArgs args)
        {
            if (session.RunApp is not null)
            {
                stopped = window.Perform(AppActions.Stop);
            }
        }
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the initial debug run to end");
            window.ApplySettings(window.Settings with { HotReload = true });
            session.InspectedChanged += Cancel;
            string? canceled = Ui.Await(window.RunAsync(), "Stop after Run creates its waiting inspector", TimeSpan.FromMinutes(2));
            session.InspectedChanged -= Cancel;
            Assert.True(stopped, "Stop was not available when the new inspector was created");
            Assert.Contains("cancel", canceled ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LiveAppState.Ended, session.RunApp!.State);
            Assert.Null(Ui.Await(window.RunAsync(), "the fresh watched run", TimeSpan.FromMinutes(2)));
            Ui.WaitFor(() => session.RunApp?.State == LiveAppState.Connected && RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal),
                "the fresh target and its file watchers", TimeSpan.FromMinutes(2));
            LiveApp app = session.RunApp!;
            int? process = app.ProcessId;
            Typing.Replace(window, "Created before", "Saved after canceled launch");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => session.RunXaml.LastApplied is not null, "the already-open XAML edit to apply on save", TimeSpan.FromSeconds(15));
            Assert.Equal(HotReloadResult.Applied, session.RunXaml.LastApplied!.Result);
            Assert.Equal(process, app.ProcessId);
            Assert.Contains(Read(app), e => e.Text == "Saved after canceled launch");
        }
        finally
        {
            session.InspectedChanged -= Cancel;
            FutureEnded(window, watched: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_wpf_view_failure_keeps_the_acknowledged_result_and_pending_editor_text(bool watched)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Dark");
        string path = Path.Combine(folder, "RepeatedView.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("xmlns:x=", "xmlns:local=\"clr-namespace:WpfTreeApp\" xmlns:x=", StringComparison.Ordinal)
            .Replace("    </StackPanel>", "        <local:ThrowingControl Fragile=\"Safe\" />\n    </StackPanel>", StringComparison.Ordinal));
        string main = Path.Combine(folder, "MainWindow.xaml.cs");
        File.WriteAllText(main, File.ReadAllText(main).Replace("state.Count++;", "state.Count++; ThrowingControl.Refuse = true;", StringComparison.Ordinal) + """

            public class ThrowingControl : System.Windows.Controls.Control
            {
                public static bool Refuse;
                public static readonly DependencyProperty FragileProperty = DependencyProperty.Register(nameof(Fragile), typeof(string), typeof(ThrowingControl),
                    new PropertyMetadata("Safe", (_, changed) => { if (Refuse && (string)changed.NewValue == "Accepted") { throw new InvalidOperationException("Future setter refused"); } }));
                public string Fragile { get => (string)GetValue(FragileProperty); set => SetValue(FragileProperty, value); }
            }
            """);
        MainWindow window = FutureStarted(watched, "net11.0-windows");
        try
        {
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            Assert.Null(window.OpenFile(path));
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Accepted\"");
            HotReloadOutcome accepted = FutureApplied(window, watched);
            Assert.True(accepted.Result == HotReloadResult.Applied, accepted.Said);
            Typing.Replace(window, "Created before", "Pending text");
            XamlUpdateResult? observed = null;
            app.XamlFailed += (_, result) => observed = result;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "Increment").Id }), "the app to arm its new-view setter refusal");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the app to construct a later WPF view");
            Ui.WaitFor(() => observed is not null, "the later WPF failure to arrive as its own event");
            Assert.Same(accepted, watched ? window.Session.RunXaml.LastApplied : window.Debugger.LastApplied);
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Assert.Contains(observed!.Problems, p => p.File == path && p.Line == 9 && p.RestartRequired && p.Message.Contains("Fragile", StringComparison.Ordinal));
            Assert.Contains("newly created view", FutureOutput(window, watched), StringComparison.Ordinal);
            Assert.Contains(window.BottomPanel.Problems.HotReloadProblems!, p => p.Path == path && p.Message.Contains("newly created", StringComparison.Ordinal));
            Assert.Contains("newly created view", window.Status!, StringComparison.Ordinal);
            Assert.Contains("Pending text", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
            window.SetRenderScaling(2);
            Ui.Settle();
            UiAudit.Capture(window, "wpf-future-failure-" + (watched ? "run" : "debug"));
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_import_that_observes_or_changes_its_hook_property_keeps_the_ordinary_wpf_build_and_refuses_live_updates(bool assigns)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        string before = Path.Combine(folder, "UserBefore.targets");
        File.WriteAllText(before, File.ReadAllText(before).Replace("<PropertyGroup>", assigns
            ? "<PropertyGroup><CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)UserBefore.targets</CustomBeforeMicrosoftCommonTargets>"
            : "<PropertyGroup><ObservedHook>$(CustomBeforeMicrosoftCommonTargets)</ObservedHook>", StringComparison.Ordinal));
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            LiveApp app = window.LiveApp!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Must not apply");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains(assigns ? "changes the CustomBefore" : "reads the CustomBefore", StringComparison.Ordinal));
            Assert.Contains(Read(app), e => e.Text == "Created before");
            Assert.True(window.Debugger.HotReload!.Changed);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the unmodified ordinary WPF app to create its compiled view");
            Ui.WaitFor(() => Output(window).Contains("Imports=before kept/after kept", StringComparison.Ordinal), "both original imports to retain their compiled effects");
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_target_rewriting_initialization_or_a_foreign_callback_is_refused_without_changing_the_app(bool foreign)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        if (foreign)
        {
            string main = Path.Combine(folder, "MainWindow.xaml.cs");
            File.WriteAllText(main, File.ReadAllText(main).Replace("DataContext = new State();", "DataContext = new State(); AppContext.SetData(\"Passenger.Wpf.Populated.1\", (Action<object, Uri>)((_, _) => Console.WriteLine(\"Foreign callback kept\")));", StringComparison.Ordinal));
        }
        else
        {
            string after = Path.Combine(folder, "UserAfter.targets");
            File.WriteAllText(after, File.ReadAllText(after).Replace("</Project>", """
                  <UsingTask TaskName="RemovePopulationCallback" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                    <ParameterGroup><Files ParameterType="Microsoft.Build.Framework.ITaskItem[]" Required="true" /></ParameterGroup>
                    <Task><Code Type="Fragment" Language="cs"><![CDATA[
                      foreach (Microsoft.Build.Framework.ITaskItem file in Files)
                      {
                          string path = file.GetMetadata("FullPath");
                          if (path.EndsWith(".g.cs", System.StringComparison.Ordinal) && System.IO.File.Exists(path))
                          {
                              string text = System.IO.File.ReadAllText(path);
                              const string callback = "global::Passenger.Generated.WpfPopulation.Loaded(this, resourceLocater);";
                              if (text.Contains(callback)) { System.IO.File.WriteAllText(path, text.Replace(callback, string.Empty)); }
                          }
                      }
                    ]]></Code></Task>
                  </UsingTask>
                  <Target Name="UserRewritesInitialization" AfterTargets="PassengerPrepareWpfPopulationBeforeCompile" Condition="'$(UseWPF)' == 'true'">
                    <RemovePopulationCallback Files="@(Compile)" />
                  </Target>
                </Project>
                """, StringComparison.Ordinal));
        }
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            LiveApp app = window.LiveApp!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Must not apply");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains(foreign ? "app owns or changed" : "compiled WPF population no longer matches", StringComparison.Ordinal));
            Assert.Contains(Read(app), e => e.Text == "Created before");
            Assert.DoesNotContain(Read(app), e => e.Text == "Must not apply");
            Assert.True(window.Debugger.HotReload!.Changed);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the unmodified compiled view to remain constructible");
            Ui.WaitFor(() => Output(window).Contains("Imports=before kept/after kept", StringComparison.Ordinal), "the original before and after imports to remain active");
            if (foreign)
            {
                Assert.Contains("Foreign callback kept", Output(window), StringComparison.Ordinal);
            }
            Assert.Equal(2, Read(app).Count(e => e.Name == "NewLiteral" && e.Text == "Created before"));
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaFact]
    public void Creating_a_missing_custom_import_invalidates_its_refusal_and_a_real_rebuild_enables_future_views()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        string before = Path.Combine(folder, "UserBefore.targets");
        string contents = File.ReadAllText(before);
        File.Delete(before);
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Built after restart");
            HotReloadOutcome missing = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, missing.Result);
            Assert.Contains(missing.Problems, p => p.Message.Contains("does not exist yet", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.Contains(Read(window.LiveApp!), e => e.Text == "Created before");
            File.WriteAllText(before, contents);
            HotReloadOutcome stillRunning = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, stillRunning.Result);
            Ui.Run(window, AppActions.Save);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the unhooked WPF app to stop before rebuilding");
            string? problem = Ui.Await(window.DebugAsync(), "the same session to invalidate missing-import preparation and rebuild", TimeSpan.FromMinutes(3));
            Assert.True(problem is null, $"{problem}\n{window.BottomPanel.Build.Document.Snapshot}");
            Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected, "the rebuilt WPF app to connect");
            LiveApp app = window.LiveApp!;
            Typing.Replace(window, "Built after restart", "Latest after restart");
            HotReloadOutcome accepted = FutureApplied(window, false);
            Assert.True(accepted.Result == HotReloadResult.Applied, accepted.Said);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "a newly hooked view to be constructed");
            Ui.WaitFor(() => Output(window).Contains("Constructor saw Latest after restart", StringComparison.Ordinal), "the rebuilt view's constructor to see its latest accepted edit");
            Assert.Contains("Imports=before kept/after kept", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dormant_conditional_import_keeps_its_normal_build_semantics_and_refuses_future_view_updates(bool indirect)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        string text = File.ReadAllText(project);
        File.WriteAllText(project, indirect ? text.Replace("<UseWPF>true</UseWPF>", """
            <UseWPF>true</UseWPF>
            <MSBuildUserExtensionsPath>$(MSBuildProjectDirectory)/Extensions</MSBuildUserExtensionsPath>
            <MSBuildUserExtensionsPath Condition="Exists('$(MSBuildProjectDirectory)/enable-hook')">$(MSBuildProjectDirectory)/OtherExtensions</MSBuildUserExtensionsPath>
            """, StringComparison.Ordinal) : text.Replace("<CustomBeforeMicrosoftCommonTargets>",
                "<CustomBeforeMicrosoftCommonTargets Condition=\"Exists('$(MSBuildProjectDirectory)/enable-hook')\">", StringComparison.Ordinal));
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Must stay pending");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains(indirect ? "custom file-dependent conditions" : "build-dependent property or condition", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.DoesNotContain(Read(window.LiveApp!), e => e.Text == "Must stay pending");
            Ui.Await(window.LiveApp!.InputAsync(new InputAction(InputKinds.Click) { Element = Read(window.LiveApp).Single(e => e.Name == "CreateView").Id }), "the ordinary app's dormant import to remain dormant");
            Ui.WaitFor(() => Output(window).Contains(indirect ? "Imports=before kept/after kept" : "Imports=BEFORE LOST/after kept", StringComparison.Ordinal), "the conditional import's original compiled effect");
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaFact]
    public void A_hooked_wpf_build_can_be_recompiled_and_run_normally_after_only_code_behind_changes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Latest while hooked");
            Assert.Equal(HotReloadResult.Applied, FutureApplied(window, false).Result);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the hooked app to stop before an ordinary external build");
            string main = Path.Combine(folder, "MainWindow.xaml.cs");
            File.WriteAllText(main, File.ReadAllText(main).Replace("DataContext = new State();", """
                DataContext = new State();
                Loaded += (_, _) =>
                {
                    RepeatedView view = new RepeatedView();
                    Console.WriteLine($"Ordinary build: named={FindName("Entry") == Entry}; child={view.FindName("NewLiteral") is System.Windows.Controls.TextBlock}; imports={ImportState.Before}/{ImportState.After}");
                    Application.Current.Shutdown();
                };
                """, StringComparison.Ordinal));
            ChildResult built = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["build", "WpfTreeApp.csproj", "--framework", "net11.0-windows", "--no-restore", "-nodeReuse:false"], folder), limit: TimeSpan.FromMinutes(2)),
                "the ordinary code-behind-only incremental build", TimeSpan.FromMinutes(2));
            Assert.True(built.Succeeded, built.Output + built.Error);
            ChildResult ran = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                [Path.Combine(folder, "bin", "Debug", "net11.0-windows", "WpfTreeApp.dll")], folder), limit: TimeSpan.FromSeconds(30)),
                "the normally built WPF app to populate its views and exit", TimeSpan.FromSeconds(40));
            Assert.True(ran.Succeeded, ran.Output + ran.Error);
            Assert.Contains("Ordinary build: named=True; child=True; imports=before kept/after kept", ran.Output, StringComparison.Ordinal);
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Creating_a_new_import_invalidates_the_shared_hook_before_the_next_real_build_in_the_same_window(int mode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        bool extension = mode > 0;
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        if (extension)
        {
            File.WriteAllText(project, File.ReadAllText(project).Replace("<UseWPF>true</UseWPF>",
                "<UseWPF>true</UseWPF><MSBuildUserExtensionsPath>$(MSBuildProjectDirectory)/FixtureExtensions</MSBuildUserExtensionsPath>", StringComparison.Ordinal));
            string state = Path.Combine(folder, "ImportState.cs");
            File.WriteAllText(state, File.ReadAllText(state).Replace("#if BEFORE_IMPORT_KEPT", "#if NEW_EXTENSION_KEPT\n    public const string Before = \"new extension kept\";\n#elif BEFORE_IMPORT_KEPT", StringComparison.Ordinal));
            if (mode == 2)
            {
                Directory.CreateDirectory(Path.Combine(folder, "FixtureExtensions", "Current", "Microsoft.Common.targets", "ImportBefore"));
            }
        }
        else
        {
            File.WriteAllText(project, File.ReadAllText(project).Replace("<CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)UserBefore.targets</CustomBeforeMicrosoftCommonTargets>", string.Empty, StringComparison.Ordinal));
        }
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            if (extension)
            {
                string directory = Path.Combine(folder, "FixtureExtensions", "Current", "Microsoft.Common.targets", "ImportBefore");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(folder, "NewBefore.targets"), "<Project><PropertyGroup><DefineConstants>$(DefineConstants);NEW_EXTENSION_KEPT</DefineConstants></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(directory, mode == 2 ? "obj" : "New.targets"), """
                    <Project><PropertyGroup><CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)../../../../NewBefore.targets</CustomBeforeMicrosoftCommonTargets></PropertyGroup></Project>
                    """);
            }
            else
            {
                File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)UserBefore.targets</CustomBeforeMicrosoftCommonTargets>
                    <DefineConstants>$(DefineConstants);DIRECTORY_IMPORT_CREATED</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            }
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "Pending after import creation");
            HotReloadOutcome stillRunning = FutureApplied(window, false);
            Ui.Run(window, AppActions.Save);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the originally hooked app to stop before a new build");
            string? problem = Ui.Await(window.DebugAsync(), "the same window to rebuild with its new import", TimeSpan.FromMinutes(3));
            Assert.True(problem is null, $"{problem}\n{window.BottomPanel.Build.Document.Snapshot}");
            Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected, "the app built with its newly discovered import");
            LiveApp app = window.LiveApp!;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the newly built app's import effects to execute");
            Ui.WaitFor(() => Output(window).Contains("Imports=", StringComparison.Ordinal), "the newly built import constants to be reported");
            Assert.Contains(extension ? "Imports=new extension kept/after kept" : "Imports=before kept/after kept", Output(window), StringComparison.Ordinal);
            Assert.Equal(HotReloadResult.RestartNeeded, stillRunning.Result);
            Typing.Replace(window, "Pending after import creation", "Must stay pending");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains("CustomBefore", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.DoesNotContain(Read(app), e => e.Text == "Must stay pending");
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaFact]
    public void Creating_a_custom_named_ancestor_import_invalidates_its_missing_search_and_keeps_ordinary_hook_observations()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("<UseWPF>true</UseWPF>",
            "<UseWPF>true</UseWPF><_DirectoryBuildTargetsFile>Company.targets</_DirectoryBuildTargetsFile>", StringComparison.Ordinal));
        string state = Path.Combine(folder, "ImportState.cs");
        File.WriteAllText(state, File.ReadAllText(state).Replace("#if BEFORE_IMPORT_KEPT",
            "#if COMPANY_ORIGINAL_HOOK\n    public const string Before = \"company original hook\";\n#elif BEFORE_IMPORT_KEPT", StringComparison.Ordinal));
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            File.WriteAllText(Path.Combine(folder, "Company.targets"), """
                <Project>
                  <PropertyGroup>
                    <DefineConstants Condition="'$(CustomBeforeMicrosoftCommonTargets)' == '$(MSBuildProjectDirectory)\UserBefore.targets'">$(DefineConstants);COMPANY_ORIGINAL_HOOK</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            Assert.Null(window.OpenFile(Path.Combine(folder, "RepeatedView.xaml")));
            Typing.Replace(window, "Created before", "After named import");
            HotReloadOutcome stillRunning = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, stillRunning.Result);
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.DoesNotContain(Read(window.LiveApp!), e => e.Text == "After named import");
            Ui.Run(window, AppActions.Save);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the app built before its custom-named import to stop");
            string? problem = Ui.Await(window.DebugAsync(), "the same window to rebuild with its newly found custom import", TimeSpan.FromMinutes(3));
            Assert.True(problem is null, $"{problem}\n{window.BottomPanel.Build.Document.Snapshot}");
            Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Connected, "the ordinarily rebuilt WPF app");
            LiveApp app = window.LiveApp!;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = Read(app).Single(e => e.Name == "CreateView").Id }), "the original Hook observation to execute in the rebuilt app");
            Ui.WaitFor(() => Output(window).Contains("Imports=company original hook/after kept", StringComparison.Ordinal), "the new import to observe the original CustomBefore property");
            Typing.Replace(window, "After named import", "Must remain pending");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains("reads the CustomBefore", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.DoesNotContain(Read(app), e => e.Text == "Must remain pending");
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaFact]
    public void Wpf_temporary_compiler_projects_are_not_discovered_or_reloaded_as_workspace_projects()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        string project = Path.Combine(folder, "WpfTreeApp.csproj");
        string temporary = Path.Combine(folder, "WpfTreeApp_abc123_wpftmp.csproj");
        File.Copy(project, temporary);
        MainWindow window = new MainWindow();
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.Settle();
            Assert.Equal(project, Assert.Single(window.Session.Projects).Path);
            File.Delete(temporary);
            File.Copy(project, temporary);
            string added = Path.Combine(folder, "RealProject.csproj");
            File.WriteAllText(added, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");
            Ui.WaitFor(() => window.Session.Projects.Any(p => p.Path == added), "the real new project to be discovered alongside compiler events");
            Assert.Equal(2, window.Session.Projects.Count);
            Assert.DoesNotContain(window.Session.Projects, p => p.Path == temporary);
            File.Delete(temporary);
            File.Delete(added);
            Ui.WaitFor(() => window.Session.Projects.Count == 1, "the real project's deletion to update the workspace");
            Assert.Equal(project, window.Session.Projects[0].Path);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_wpf_setter_creating_a_view_that_refuses_the_same_batch_rolls_back_known_values_and_requires_restart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Light");
        string path = Path.Combine(folder, "RepeatedView.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("xmlns:x=", "xmlns:local=\"clr-namespace:WpfTreeApp\" xmlns:x=", StringComparison.Ordinal)
            .Replace("    </StackPanel>", "        <local:SpawningControl Fragile=\"Safe\" />\n    </StackPanel>", StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(folder, "MainWindow.xaml.cs"), """

            public class SpawningControl : System.Windows.Controls.Control
            {
                private static bool spawned;
                public static readonly DependencyProperty FragileProperty = DependencyProperty.Register(nameof(Fragile), typeof(string), typeof(SpawningControl), new PropertyMetadata("Safe", (_, changed) =>
                {
                    if ((string)changed.NewValue == "Spawn")
                    {
                        if (spawned) { throw new InvalidOperationException("New view refused the batch"); }
                        spawned = true;
                        ((MainWindow)Application.Current.MainWindow!).NewViews.Children.Add(new RepeatedView());
                    }
                }));
                public string Fragile { get => (string)GetValue(FragileProperty); set => SetValue(FragileProperty, value); }
            }
            """);
        MainWindow window = FutureStarted(false, "net11.0-windows");
        try
        {
            LiveApp app = window.LiveApp!;
            long original = Read(app).Single(e => e.Name == "NewLiteral").Id;
            int failedEvents = 0;
            app.XamlFailed += (_, _) => failedEvents++;
            Assert.Null(window.OpenFile(path));
            Typing.Replace(window, "Created before", "Must roll back");
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Spawn\"");
            HotReloadOutcome refused = FutureApplied(window, false);
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Path == path && p.Message.Contains("newly created", StringComparison.Ordinal));
            Assert.Empty(refused.Files);
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.Equal(0, failedEvents);
            List<Element> after = Read(app);
            Assert.Equal("Created before", after.Single(e => e.Id == original).Text);
            Assert.Equal(2, after.Count(e => e.Type == "RepeatedView")); // The app's setter side effect is observed, never promised undone.
            Assert.DoesNotContain(after, e => e.Text == "Must roll back");
            foreach (Element element in after.Where(e => e.Type == "SpawningControl"))
            {
                Assert.Equal("\"Safe\"", Ui.Await(app.DetailsAsync(element.Id), "the rolled-back WPF property").Properties.Single(p => p.Name == "Fragile").Value);
            }
        }
        finally
        {
            FutureEnded(window, false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_literal_property_elements_keep_original_object_anchors_across_later_edits_and_future_views(bool watched)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        FutureViews("Dark");
        string child = Path.Combine(folder, "RepeatedView.xaml");
        MainWindow window = FutureStarted(watched, "net11.0-windows");
        try
        {
            window.ApplySettings(window.Settings with { RestartOnRudeEdit = false });
            LiveApp app = watched ? window.Session.RunApp! : window.LiveApp!;
            int? process = app.ProcessId;
            List<Element> before = Read(app);
            HashSet<long> old = [.. before.Select(e => e.Id)];
            long entry = before.First(e => e.Name == "NewEntry").Id;
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = entry, Text = "Kept added-property state", Replace = true }), "existing view state");
            Capture(app, "property-additions-" + (watched ? "run" : "debug") + "-before");
            Assert.Null(window.OpenFile(child));
            Typing.Replace(window, "Text=\"Created before\" Width=\"200\" />", "Text=\"Added first\" Width=\"200\">\n<TextBlock.FontSize>invalid</TextBlock.FontSize>\n</TextBlock>");
            HotReloadOutcome invalid = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, invalid.Result);
            Assert.Contains(invalid.Problems, p => p.Path == child && p.Message.Contains("FontSize", StringComparison.Ordinal));
            Assert.DoesNotContain(Read(app), e => e.Text == "Added first");
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Typing.Replace(window, "<TextBlock.FontSize>invalid</TextBlock.FontSize>", "<TextBlock.FontSize>22</TextBlock.FontSize>");
            HotReloadOutcome first = FutureApplied(window, watched);
            Assert.True(first.Result == HotReloadResult.Applied, first.Said);
            foreach (Element element in Read(app).Where(e => e.Name == "NewLiteral"))
            {
                PropertyValue size = Ui.Await(app.DetailsAsync(element.Id), "the added property-element source").Properties.Single(p => p.Name == "FontSize");
                Assert.Equal("22", size.Value);
                Assert.Equal(ValueSources.Local, size.Source);
            }
            Typing.Replace(window, "<TextBlock.FontSize>22</TextBlock.FontSize>", "<TextBlock.FontSize>24</TextBlock.FontSize>");
            Typing.Replace(window, "Text=\"Added first\"", "Text=\"Added latest\"");
            Typing.Replace(window, "Text=\"XAML before\"", "Text=\"Below added property\"");
            HotReloadOutcome latest = FutureApplied(window, watched);
            Assert.True(latest.Result == HotReloadResult.Applied, latest.Said);
            Assert.Equal(2 - 1, Read(app).Count(e => e.Name == "ConstructorValue" && e.Text == "Below added property"));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a future view after adding a property element");
            Ui.WaitFor(() => FutureOutput(window, watched).Contains("Constructor saw Added latest; Below added property", StringComparison.Ordinal), "the new view to receive both edits before its constructor writes");
            List<Element> after = Read(app);
            List<Element> made = [.. Flatten(after.Single(e => e.Type == "RepeatedView" && !old.Contains(e.Id)))];
            Assert.Equal("Constructor wins", made.Single(e => e.Name == "ConstructorValue").Text);
            Assert.Equal("Kept added-property state", after.Single(e => e.Id == entry).Text);
            Assert.Equal(2, after.Count(e => e.Name == "NewLiteral" && e.Text == "Added latest"));
            foreach (Element element in after.Where(e => e.Name == "NewLiteral"))
            {
                Assert.Equal("24", Ui.Await(app.DetailsAsync(element.Id), "the latest property element in every view").Properties.Single(p => p.Name == "FontSize").Value);
            }
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = made.Single(e => e.Name == "NewEntry").Id, Text = "New added-property state", Replace = true }), "the new view's compiled binding");
            Assert.Equal("New added-property state", Read(app).Single(e => e.Id == made.Single(e => e.Name == "NewBound").Id).Text);
            Capture(app, "property-additions-" + (watched ? "run" : "debug") + "-after");
            Typing.Replace(window, "x:Name=\"NewLiteral\"", "x:Name=\"NewLiteral\" FontSize=\"30\"");
            HotReloadOutcome attribute = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, attribute.Result);
            Assert.Contains(attribute.Problems, p => p.Path == child && p.Message.Contains("both an attribute and a property element", StringComparison.Ordinal));
            Typing.Replace(window, "x:Name=\"NewLiteral\" FontSize=\"30\"", "x:Name=\"NewLiteral\"");
            Typing.Replace(window, "<TextBlock.FontSize>24</TextBlock.FontSize>", "<TextBlock.FontSize>24</TextBlock.FontSize><TextBlock.FontSize>30</TextBlock.FontSize>");
            HotReloadOutcome duplicate = FutureApplied(window, watched);
            Assert.Equal(HotReloadResult.Errors, duplicate.Result);
            Assert.Contains(duplicate.Problems, p => p.Path == child && p.Message.Contains("more than once", StringComparison.Ordinal));
            Typing.Replace(window, "<TextBlock.FontSize>24</TextBlock.FontSize><TextBlock.FontSize>30</TextBlock.FontSize>", " ");
            Assert.DoesNotContain("<TextBlock.FontSize>", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            HotReloadOutcome removed = watched ? FutureApplied(window, true) : Ui.Await(window.Debugger.ApplyChangesAsync(), "the property-element removal to be refused");
            Assert.True(removed.Result == HotReloadResult.RestartNeeded, removed.Said);
            Assert.Contains(removed.Problems, p => p.Path == child && p.Message.Contains("Removing XAML property elements", StringComparison.Ordinal));
            if (watched)
            {
                Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the removed property to offer a restart");
                ChoiceDialog asked = window.OwnedWindows.OfType<ChoiceDialog>().Single();
                Ui.Settle();
                Avalonia.Controls.Button never = asked.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => (string?)b.Content == "Never");
                Avalonia.Point at = never.TranslatePoint(new Avalonia.Point(never.Bounds.Width / 2, never.Bounds.Height / 2), asked)!.Value;
                asked.MouseMove(at);
                asked.MouseDown(at, Avalonia.Input.MouseButton.Left);
                asked.MouseUp(at, Avalonia.Input.MouseButton.Left);
                Ui.Settle();
            }
            Assert.Equal(process, app.ProcessId);
            foreach (Element element in Read(app).Where(e => e.Name == "NewLiteral"))
            {
                Assert.Equal("24", Ui.Await(app.DetailsAsync(element.Id), "the refused removal's retained local value").Properties.Single(p => p.Name == "FontSize").Value);
            }
            Assert.True(watched ? window.Session.RunXaml.Changed : window.Debugger.HotReload!.Changed);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = before.Single(e => e.Name == "CreateView").Id }), "a future view while a removal remains pending");
            List<Element> pending = Read(app);
            Assert.Equal(3, pending.Count(e => e.Name == "NewLiteral" && e.Text == "Added latest"));
            foreach (Element element in pending.Where(e => e.Name == "NewLiteral"))
            {
                Assert.Equal("24", Ui.Await(app.DetailsAsync(element.Id), "the last accepted value in a later view").Properties.Single(p => p.Name == "FontSize").Value);
            }
        }
        finally
        {
            FutureEnded(window, watched);
        }
    }

    [AvaloniaFact]
    public void Unsafe_property_element_additions_and_reordering_preserve_the_live_tree_and_namescope()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            int? process = app.ProcessId;
            HashSet<long> original = [.. Read(app).Select(e => e.Id)];
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            Typing.Replace(window, "<StackPanel Margin", "<Window.Content>Must not replace the subtree</Window.Content><StackPanel Margin");
            HotReloadOutcome content = Ui.Await(window.Debugger.ApplyChangesAsync(), "the implicit-content replacement to be refused");
            Assert.Equal(HotReloadResult.RestartNeeded, content.Result);
            Assert.Contains(content.Problems, p => p.Message.Contains("implicit text or child content", StringComparison.Ordinal));
            Typing.Replace(window, "<Window.Content>Must not replace the subtree</Window.Content>", " ");
            Typing.Replace(window, "<TextBlock Text=\"Before\" />", "<TextBlock Text=\"Before\"><TextBlock.Name>Must not rename</TextBlock.Name></TextBlock>");
            HotReloadOutcome name = Ui.Await(window.Debugger.ApplyChangesAsync(), "the namescope edit to be refused");
            Assert.Equal(HotReloadResult.RestartNeeded, name.Result);
            Assert.Contains(name.Problems, p => p.Message.Contains("namescopes", StringComparison.Ordinal));
            Typing.Replace(window, "<TextBlock.Name>Must not rename</TextBlock.Name>", "<TextBlock.DataContext>Must not replace the viewmodel</TextBlock.DataContext>");
            HotReloadOutcome context = Ui.Await(window.Debugger.ApplyChangesAsync(), "the added data context to be refused");
            Assert.Equal(HotReloadResult.RestartNeeded, context.Result);
            Assert.Contains(context.Problems, p => p.Message.Contains("bindings", StringComparison.Ordinal));
            Typing.Replace(window, "<TextBlock.DataContext>Must not replace the viewmodel</TextBlock.DataContext>", "<TextBlock.>Incomplete property</TextBlock.>");
            HotReloadOutcome incomplete = Ui.Await(window.Debugger.ApplyChangesAsync(), "the incomplete property addition to be refused");
            Assert.Equal(HotReloadResult.RestartNeeded, incomplete.Result);
            Assert.Contains(incomplete.Problems, p => p.Message.Contains("structure", StringComparison.Ordinal));
            Typing.Replace(window, "<TextBlock.>Incomplete property</TextBlock.>", "<TextBlock.FontSize>14</TextBlock.FontSize><TextBlock.Opacity>1</TextBlock.Opacity>");
            HotReloadOutcome accepted = Ui.Await(window.Debugger.ApplyChangesAsync(), "two literal property additions to apply");
            Assert.True(accepted.Result == HotReloadResult.Applied, accepted.Said);
            Typing.Replace(window, "<TextBlock.FontSize>14</TextBlock.FontSize><TextBlock.Opacity>1</TextBlock.Opacity>", "<TextBlock.Opacity>1</TextBlock.Opacity><TextBlock.FontSize>14</TextBlock.FontSize>");
            HotReloadOutcome order = Ui.Await(window.Debugger.ApplyChangesAsync(), "existing property order to be retained");
            Assert.Equal(HotReloadResult.RestartNeeded, order.Result);
            Assert.Contains(order.Problems, p => p.Message.Contains("structure", StringComparison.Ordinal));
            Assert.True(window.Debugger.HotReload!.Changed);
            Assert.Equal(process, app.ProcessId);
            List<Element> after = Read(app);
            Assert.Equal(original.Order(), after.Select(e => e.Id).Order());
            Assert.Contains(after, e => e.Text == "Before");
            Assert.DoesNotContain(after, e => e.Name == "Must not rename" || e.Text == "Must not replace the subtree");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = after.Single(e => e.Name == "Entry").Id, Text = "Viewmodel survived", Replace = true }), "the original viewmodel and binding");
            Assert.Equal("Viewmodel survived", Read(app).Single(e => e.Name == "Bound").Text);
        }
        finally
        {
            End(window);
        }
    }

    private static HotReloadOutcome FutureApplied(MainWindow window, bool watched, bool all = false)
    {
        if (!watched)
        {
            return Ui.Await(window.Debugger.ApplyChangesAsync(), "the WPF XAML edit to apply");
        }
        HotReloadOutcome? before = window.Session.RunXaml.LastApplied;
        Ui.Run(window, all ? AppActions.ApplyHotReloadChanges : AppActions.Save);
        Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { } next && !ReferenceEquals(next, before), "the saved WPF edit to apply");
        return window.Session.RunXaml.LastApplied!;
    }

    private static string FutureOutput(MainWindow window, bool watched) => watched ? RunOutput(window) : Output(window);

    private static void FutureEnded(MainWindow window, bool watched)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => watched ? !window.IsRunning : !window.Debugger.IsDebugging, "the hooked WPF app to stop");
        window.Close();
    }

    private void AttributeView(bool throwing = false)
    {
        string view = View.Replace("<StackPanel Margin", "<Window.Resources><Style x:Key=\"Caption\" TargetType=\"{x:Type TextBlock}\"><Setter Property=\"Width\" Value=\"180\" /></Style></Window.Resources><StackPanel Margin", StringComparison.Ordinal)
            .Replace("Text=\"Before\"", "Text=\"Before\" Style=\"{StaticResource Caption}\"", StringComparison.Ordinal);
        if (throwing)
        {
            view = view.Replace("<local:RepeatedView />", "<local:RepeatedView /><local:ThrowingControl Fragile=\"Safe\" />", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.cs"), Code + """

                public class ThrowingControl : System.Windows.Controls.Control
                {
                    public static readonly DependencyProperty FragileProperty = DependencyProperty.Register(nameof(Fragile), typeof(string), typeof(ThrowingControl), new PropertyMetadata("Safe", (_, changed) => { if ((string)changed.NewValue == "Refuse") { throw new InvalidOperationException("Setter refused"); } }));
                    public string Fragile { get => (string)GetValue(FragileProperty); set => SetValue(FragileProperty, value); }
                }
                """);
        }
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), view);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_literal_attribute_can_be_added_and_edited_again_as_a_local_value_without_replacing_wpf_state(string theme)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        AttributeView();
        if (theme == "Dark")
        {
            string path = Path.Combine(folder, "MainWindow.xaml");
            File.WriteAllText(path, File.ReadAllText(path).Replace("Background=\"WhiteSmoke\" Foreground=\"Black\"", "Background=\"#222222\" Foreground=\"White\"", StringComparison.Ordinal));
        }
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            Element caption = Read(app).Single(e => e.Text == "Before");
            Assert.Equal(ValueSources.Style, Ui.Await(app.DetailsAsync(caption.Id), "the original WPF style").Properties.Single(p => p.Name == "Width").Source);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = Read(app).Single(e => e.Name == "Entry").Id, Text = "Kept input", Replace = true }), "the WPF input");
            Capture(app, "attribute-" + theme + "-before");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            Typing.Replace(window, "Text=\"Before\" Style", "Text=\"Before\" Width=\"260\" Style");
            Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
            Typing.Replace(window, "Width=\"260\"", "Width=\"300\"");
            Assert.Equal(HotReloadResult.Applied, Apply(window).Result);
            PropertyValue width = Ui.Await(app.DetailsAsync(caption.Id), "the new WPF attribute's local value").Properties.Single(p => p.Name == "Width");
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
    public void An_added_wpf_attribute_in_a_failed_batch_restores_the_style_source_instead_of_leaving_a_local_value()
        => AddedPropertyRollback(element: false);

    [AvaloniaFact]
    public void An_added_wpf_property_element_in_a_failed_batch_restores_the_style_source_instead_of_leaving_a_local_value()
        => AddedPropertyRollback(element: true);

    private void AddedPropertyRollback(bool element)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        AttributeView(throwing: true);
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            if (element)
            {
                Typing.Replace(window, "Style=\"{StaticResource Caption}\" />", "Style=\"{StaticResource Caption}\"><TextBlock.Width>260</TextBlock.Width></TextBlock>");
            }
            else
            {
                Typing.Replace(window, "Text=\"Before\" Style", "Text=\"Before\" Width=\"260\" Style");
            }
            Typing.Replace(window, "Fragile=\"Safe\"", "Fragile=\"Refuse\"");
            HotReloadOutcome refused = Ui.Await(window.Debugger.ApplyChangesAsync(), "the WPF setter to refuse the batch");
            Assert.Equal(HotReloadResult.RestartNeeded, refused.Result);
            Assert.Contains(refused.Problems, p => p.Message.Contains("Setter refused", StringComparison.Ordinal));
            PropertyValue width = Ui.Await(app.DetailsAsync(caption), "the rolled back WPF attribute's value source").Properties.Single(p => p.Name == "Width");
            Assert.Equal("180", width.Value);
            Assert.Equal(ValueSources.Style, width.Source);
            Assert.True(window.Debugger.HotReload!.Changed);
        }
        finally
        {
            End(window);
        }
    }

    [AvaloniaFact]
    public void Adding_a_wpf_attribute_over_a_constructor_binding_requires_restart_without_replacing_the_binding()
        => AddedPropertyBinding(element: false);

    [AvaloniaFact]
    public void Adding_a_wpf_property_element_over_a_constructor_binding_requires_restart_without_replacing_the_binding()
        => AddedPropertyBinding(element: true);

    private void AddedPropertyBinding(bool element)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), View.Replace("x:Name=\"Bound\" Text=\"{Binding Name}\"", "x:Name=\"Bound\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.cs"), Code.Replace("DataContext = new State();", "DataContext = new State(); System.Windows.Data.BindingOperations.SetBinding(Bound, System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(\"Name\"));", StringComparison.Ordinal));
        MainWindow window = Debugged();
        try
        {
            LiveApp app = window.LiveApp!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            if (element)
            {
                Typing.Replace(window, "x:Name=\"Bound\" />", "x:Name=\"Bound\"><TextBlock.Text>Must not replace</TextBlock.Text></TextBlock>");
            }
            else
            {
                Typing.Replace(window, "x:Name=\"Bound\"", "x:Name=\"Bound\" Text=\"Must not replace\"");
            }
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Must not apply\"");
            HotReloadOutcome result = Ui.Await(window.Debugger.ApplyChangesAsync(), "the WPF constructor binding to be preserved");
            Assert.Equal(HotReloadResult.RestartNeeded, result.Result);
            Assert.Contains(result.Problems, p => p.Message.Contains("binding", StringComparison.Ordinal));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Type) { Element = Read(app).Single(e => e.Name == "Entry").Id, Text = "Binding survived", Replace = true }), "the WPF binding source to change");
            Assert.Equal("Binding survived", Read(app).Single(e => e.Name == "Bound").Text);
            Assert.Contains(Read(app), e => e.Text == "Before");
        }
        finally
        {
            End(window);
        }
    }

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
    public void Wpf_wrong_source_and_unsupported_structure_refuse_while_a_verified_unused_view_registers_without_mutating_live_instances()
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
        Assert.Equal(HotReloadResult.RegisteredForFutureViews, absent.Result);
        Assert.Equal(1, absent.FutureViewRegistrations);
        Assert.Equal(0, absent.LiveXamlProperties);
        Assert.Contains(Path.Combine(folder, "UnopenedView.xaml"), absent.Files);
        Assert.DoesNotContain(Read(app), e => e.Text == "No instance");
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
        Typing.Replace(window, "<TextBlock Text=\"Before\" />", "<TextBlock Text=\"Must not apply\" /><TextBlock Text=\"Added\" />");
        HotReloadOutcome structure = Ui.Await(window.Debugger.ApplyChangesAsync(), "the structural WPF edit to be classified");
        Assert.Equal(HotReloadResult.RestartNeeded, structure.Result);
        Assert.Contains(structure.Problems, p => p.Path == Path.Combine(folder, "MainWindow.xaml") && p.Message.Contains("elements", StringComparison.Ordinal));
        Assert.Contains(Read(app), e => e.Text == "Before");
        End(window);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Wpf_run_under_watch_preserves_the_managed_agent_inspects_xaml_and_reconnects_after_a_rebuild(string theme)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs on Windows alone");
        if (theme == "Dark")
        {
            File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), View.Replace("Background=\"WhiteSmoke\" Foreground=\"Black\"", "Background=\"#222222\" Foreground=\"White\"", StringComparison.Ordinal));
        }
        // A project that switched UI frameworks can leave the former framework's output beside its target assembly.
        string stale = Directory.CreateDirectory(Path.Combine(folder, "bin", "Debug", AvaloniaApp.Metadata("TargetFramework") + "-windows")).FullName;
        File.WriteAllText(Path.Combine(stale, "Avalonia.Base.dll"), "stale former build output");
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Assert.Null(Ui.Await(window.RunAsync(), "the WPF app to start under watch", TimeSpan.FromMinutes(3)));
            try
            {
                Ui.WaitFor(() => window.Session.RunApp?.State == LiveAppState.Connected && RunOutput(window).Contains("Waiting for changes", StringComparison.Ordinal),
                    "the watched WPF target's inspector and file watchers", TimeSpan.FromMinutes(2));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"{error.Message}\n{RunOutput(window)}", error);
            }
            LiveApp app = window.Session.RunApp!;
            Assert.Equal(LiveAppKind.Wpf, app.Kind);
            int? first = app.ProcessId;
            long button = Read(app).Single(e => e.Name == "Increment").Id;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml.cs")));
            Typing.Replace(window, "Name={state.Name};", "Watched Name={state.Name};");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => RunOutput(window).Contains("changes applied", StringComparison.OrdinalIgnoreCase), "watch's C# update in WPF", TimeSpan.FromMinutes(2));
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button }), "the watched WPF app's updated handler");
            Ui.WaitFor(() => RunOutput(window).Contains("Watched Name=Original state; Count=1; Named=True", StringComparison.Ordinal), "the WPF C# update to run without restart");
            Assert.Equal(first, app.ProcessId);
            long caption = Read(app).Single(e => e.Text == "Before").Id;
            Capture(app, "watch-" + theme + "-before");
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            Typing.Replace(window, "Text=\"Before\"", "Text=\"Watched XAML\" Width=\"260\"");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied?.Result == HotReloadResult.Applied, "the saved WPF XAML edit to apply without rebuilding");
            Assert.Equal("Watched XAML", Read(app).Single(e => e.Id == caption).Text);
            PropertyValue savedWidth = Ui.Await(app.DetailsAsync(caption), "the saved WPF attribute's local source").Properties.Single(p => p.Name == "Width");
            Assert.Equal("260", savedWidth.Value);
            Assert.Equal(ValueSources.Local, savedWidth.Source);
            Assert.Equal(first, app.ProcessId);
            HotReloadOutcome saved = window.Session.RunXaml.LastApplied!;
            Typing.Replace(window, "Text=\"Watched XAML\"", "Text=\"Applied XAML\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { Result: HotReloadResult.Applied } next && next != saved, "Apply Changes to update the watched WPF controls");
            Assert.Equal("Applied XAML", Read(app).Single(e => e.Id == caption).Text);
            Assert.Equal(first, app.ProcessId);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = button }), "the retained WPF handler after XAML updates");
            Ui.WaitFor(() => RunOutput(window).Contains("Watched Name=Original state; Count=2; Named=True", StringComparison.Ordinal), "the original WPF namescope, state and handler");
            Capture(app, "watch-" + theme + "-after");
            Assert.Null(window.OpenFile(Path.Combine(folder, "WpfTreeApp.csproj")));
            Typing.Replace(window, "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><DefineConstants>WATCH_RESTART</DefineConstants>");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => app.State == LiveAppState.Connected && app.ProcessId != first, "the replacement WPF target's inspector", TimeSpan.FromMinutes(2));
            Assert.Same(app, window.Session.RunApp);
            Assert.Contains(Read(app), e => e.Text == "Applied XAML");
            HotReloadOutcome beforeReconnectEdit = window.Session.RunXaml.LastApplied!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.xaml")));
            Typing.Replace(window, "Applied XAML", "After reconnect");
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => window.Session.RunXaml.LastApplied is { Result: HotReloadResult.Applied } applied && applied != beforeReconnectEdit,
                "WPF XAML edits to use the replacement process's source anchors");
            Assert.Contains(Read(app), e => e.Text == "After reconnect");
        }
        finally
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning && window.Session.RunApp?.State == LiveAppState.Ended, "watch and its WPF inspector to end");
            window.Close();
        }
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

    private static string RunOutput(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

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
