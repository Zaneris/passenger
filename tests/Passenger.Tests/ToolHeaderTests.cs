using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Profiler tabs remain reachable while real runtime actions and status retain their own fitted space.</summary>
[Collection(Alone.Name)]
public sealed class ToolHeaderTests : IDisposable
{
    private readonly string folder = TestFolders.Create("tool-header-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profiler_tabs_and_running_actions_stay_reachable_without_losing_page_or_focus_on_resize(bool light)
    {
        string project = PathNames.Canonical(Path.Combine(folder, "Header.csproj"));
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "while (!System.IO.File.Exists(\"finish\")) { System.Threading.Thread.SpinWait(100_000); }\n");
        Restore.Run(folder);
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("Program.cs");
        Ui.Settle();
        string name = "A configuration with a deliberately long readable name";
        window.SetConfigurations([new RunConfiguration { Name = name, Project = project, WorkingDirectory = folder }], name);
        try
        {
            string? failed = Ui.Await(window.ProfileAsync(), "the real program to start under the profiler", TimeSpan.FromMinutes(3));
            Assert.True(failed is null, failed);
            Ui.WaitFor(() => window.ProfilerView.Status.StartsWith("Profiling ", StringComparison.Ordinal), "the runtime status");
            Control actions = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<Control>(), control => control.Name == "ProfilerRunTools");
            Control tabsBar = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<Control>(), control => control.Name == "ProfilerTabsBar");
            ScrollViewer strip = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<ScrollViewer>(), control => control.Name == "ProfilerPageStrip");
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                CheckActions(actions);
                CheckActions(tabsBar);
                Assert.Equal(3, actions.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible));
                UiAudit.Capture(window, $"profiler-running-{width:0}-{(light ? "light" : "dark")}");
            }
            File.WriteAllText(Path.Combine(folder, "finish"), string.Empty);
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, "the actual profile's snapshot", TimeSpan.FromMinutes(2));
            Ui.Settle();
            Assert.False(actions.IsVisible);
            ToggleButton[] pages = [.. strip.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Classes.Contains("panelTab"))];
            Assert.Equal(8, pages.Length);
            foreach (ToggleButton page in pages)
            {
                window.Width = 640;
                Ui.Settle();
                for (int attempt = 0; attempt < 20 && !Inside(page, strip); attempt++)
                {
                    double direction = page.TranslatePoint(default, strip)!.Value.X < 0 ? 3 : -3;
                    Point at = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(at, new Vector(direction, 0));
                    Ui.Settle();
                }
                Assert.True(Inside(page, strip), $"The '{page.Content}' tab could not be brought fully into its narrow strip by scrolling");
                Point hit = page.TranslatePoint(new Point(page.Bounds.Width / 2, page.Bounds.Height / 2), window)!.Value;
                window.MouseMove(hit);
                window.MouseDown(hit, MouseButton.Left);
                window.MouseUp(hit, MouseButton.Left);
                Ui.Settle();
                Assert.True(page.IsChecked == true);
                Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                object? focus = window.FocusManager?.GetFocusedElement();
                foreach (double width in new double[] { 1200, 683, 640, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    UiAudit.Capture(window, $"profiler-page-{page.Content}-{width:0}-{(light ? "light" : "dark")}");
                    Assert.True(page.IsChecked == true, $"'{page.Content}' lost selection at {width}px");
                    Assert.True(Inside(page, strip), $"'{page.Content}' escapes its strip at {width}px: tab bounds={page.Bounds}, "
                        + $"position={page.TranslatePoint(default, strip)}, strip={strip.Bounds}, viewport={strip.Viewport}, "
                        + $"extent={strip.Extent}, offset={strip.Offset}, content={((Control)strip.Content!).Bounds}, "
                        + $"profiler={window.ProfilerView.Bounds}, window={window.Bounds}");
                    Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                    Assert.Same(focus, window.FocusManager?.GetFocusedElement());
                    CheckActions(tabsBar);
                }
            }
            window.Width = 640;
            Ui.Settle();
            foreach (string navigation in new[] { "shift+tab", "tab" })
            {
                foreach (ToggleButton page in navigation == "tab" ? pages : pages.Reverse())
                {
                    for (int attempt = 0; attempt < 100 && window.FocusManager?.GetFocusedElement() != page; attempt++)
                    {
                        Ui.Press(window, navigation);
                    }
                    Assert.Same(page, window.FocusManager?.GetFocusedElement());
                    Assert.True(Inside(page, strip), $"The keyboard-focused '{page.Content}' tab is clipped out of its strip");
                    Ui.Press(window, "space");
                    Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "finish"), string.Empty);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NuGet_search_feed_options_and_count_fit_without_resetting_typed_values_on_resize(bool light)
    {
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        File.WriteAllBytes(Path.Combine(folder, "feed", "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        string feed = "A local package feed with a deliberately long readable name";
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"<configuration><packageSources><clear/><add key=\"{feed}\" value=\"feed\"/></packageSources></configuration>");
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.ShowToolWindow("nuget");
        try
        {
            Ui.Settle();
            Ui.WaitFor(() => window.NuGet.FeedChoices.Contains(feed), "the folder's real package feed");
            Control bar = Assert.Single(window.NuGet.GetVisualDescendants().OfType<Control>(), control => control.Name == "NuGetPackageTools");
            TextBox query = Assert.Single(bar.GetVisualDescendants().OfType<TextBox>(), text => text.PlaceholderText == "Search packages");
            ComboBox chooser = Assert.Single(bar.GetVisualDescendants().OfType<ComboBox>());
            CheckBox prerelease = Assert.Single(bar.GetVisualDescendants().OfType<CheckBox>());
            Button refresh = Assert.Single(bar.GetVisualDescendants().OfType<Button>(), button => Equals(ToolTip.GetTip(button), "Refresh"));
            TextBlock count = Assert.Single(bar.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "NuGetPackageStatus");
            Assert.StartsWith("0 packages", window.NuGet.Status, StringComparison.Ordinal);
            foreach (double width in new double[] { 1200, 683, 640 })
            {
                window.Width = width;
                Ui.Settle();
                CheckStatus();
                UiAudit.Capture(window, $"nuget-count-{width:0}-{(light ? "light" : "dark")}");
            }
            Click(window, chooser);
            Ui.Press(window, "end");
            Ui.Press(window, "enter");
            Assert.Equal(feed, window.NuGet.ChosenFeed);
            Click(window, query);
            window.KeyTextInput("Passenger");
            Ui.Settle();
            Click(window, prerelease);
            Ui.WaitFor(() => window.NuGet.Rows.Count == 1, "the real folder's package search result");
            Click(window, query);
            Ui.Press(window, "end");
            window.KeyTextInput(".Missing");
            Ui.Settle();
            Ui.WaitFor(() => window.NuGet.Rows.Count == 0 && window.NuGet.Status.StartsWith("Nothing found", StringComparison.Ordinal), "the absent package's status");
            Click(window, query);
            query.CaretIndex = 4;
            object? focus = window.FocusManager?.GetFocusedElement();
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                CheckNuGet();
                Assert.Same(focus, window.FocusManager?.GetFocusedElement());
                Assert.Equal(4, query.CaretIndex);
                UiAudit.Capture(window, $"nuget-packages-{width:0}-{(light ? "light" : "dark")}");
            }
            DockLayout layout = window.DockHost.Capture();
            window.DockHost.Apply(layout with
            {
                LeftWidth = 200,
                Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "nuget"
                    ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                    : pair.Value.Anchor == ToolAnchor.LeftTop ? pair.Value with { Shown = false } : pair.Value),
            });
            Ui.Settle();
            Assert.InRange(window.NuGet.Bounds.Width, 190, 210);
            CheckNuGet();
            UiAudit.Capture(window, $"nuget-packages-side-200-{(light ? "light" : "dark")}");
            Click(window, refresh);
            Ui.WaitFor(() => window.NuGet.Rows.Count == 0 && window.NuGet.Status.StartsWith("Nothing found", StringComparison.Ordinal), "the refreshed folder search");
            CheckNuGet();

            void CheckNuGet()
            {
                Assert.Equal("Passenger.Missing", query.Text);
                Assert.Equal(feed, window.NuGet.ChosenFeed);
                Assert.True(prerelease.IsChecked == true);
                List<Rect> taken = [];
                foreach (Control action in new Control[] { query, chooser, prerelease, refresh })
                {
                    Assert.True(action.IsEffectivelyVisible && Inside(action, bar), $"{action.GetType().Name} escapes the {bar.Bounds.Width}px NuGet toolbar");
                    Rect bounds = new(action.TranslatePoint(default, bar)!.Value, action.Bounds.Size);
                    Assert.DoesNotContain(taken, prior => prior.Intersects(bounds));
                    taken.Add(bounds);
                }
                CheckStatus();
                Assert.Equal(feed, ToolTip.GetTip(chooser));
                TextBlock selected = Assert.Single(chooser.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == feed);
                Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, selected.TextTrimming);
                Assert.True(Inside(selected, chooser), "The selected feed label escapes its actual chooser");
                Assert.True(selected.TextLayout.Width <= selected.Bounds.Width + 1, "The long feed label clips instead of ellipsizing");
            }

            void CheckStatus()
            {
                Assert.True(Inside(count, bar));
                Assert.True(count.TextLayout.Width <= count.Bounds.Width + 1, $"The package count clips in the {bar.Bounds.Width}px toolbar");
                Assert.Equal(window.NuGet.Status, ToolTip.GetTip(count));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_tool_headers_keep_titles_actions_and_tabs_inside_docked_and_floating_panes(bool light)
    {
        File.WriteAllText(Path.Combine(folder, "nuget.config"), "<configuration><packageSources><clear/></packageSources></configuration>");
        for (int index = 0; index < 8; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"File{index}.cs"), $"class File{index} {{ }}\n");
        }
        TestGit.Init(folder);
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/D", "/Q"])
            : ("/bin/sh", []);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("File0.cs");
        try
        {
            Ui.WaitFor(() => window.Vcs.Repository is not null, "the real folder's Git repository");
            for (int index = 0; index < 8; index++)
            {
                window.ShowLocalHistory(Path.Combine(folder, $"File{index}.cs"));
                Assert.NotNull(Ui.Await(window.ShowHistoryAsync(Path.Combine(folder, $"File{index}.cs")), "the actual file's Git history"));
            }
            DockHost dock = window.DockHost;
            foreach (string id in new[] { "nuget", "localHistory", "git", "terminal", "explorer" })
            {
                ToolWindow tool = dock[id]!;
                Control headerTools = Assert.IsAssignableFrom<Control>(tool.HeaderTools);
                DockLayout layout = dock.Capture();
                dock.Apply(layout with
                {
                    LeftWidth = 200,
                    Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == id
                        ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                        : pair.Value with { Shown = false }),
                });
                Ui.Settle();
                string title = tool.Title;
                ToggleButton? chosen = headerTools.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.IsChecked == true);
                object? focused = window.FocusManager?.GetFocusedElement();
                foreach (double width in new double[] { 1200, 640, 683, 1200, 640, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    CheckHeader(tool);
                    Assert.Same(chosen, headerTools.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.IsChecked == true));
                    Assert.Same(focused, window.FocusManager?.GetFocusedElement());
                }
                tool.Title = title + " with a dynamically extended name that must leave its actions reachable";
                Ui.Settle();
                CheckHeader(tool);
                tool.Title = title;
                Ui.Settle();
                Grid header = Header(tool);
                ScrollViewer strip = Assert.Single(header.GetVisualDescendants().OfType<ScrollViewer>(), control => control.Name == "ToolHeaderStrip");
                int tabs = headerTools.GetVisualDescendants().OfType<ToggleButton>().Count();
                for (int index = 0; index < tabs; index++)
                {
                    ToggleButton tab = headerTools.GetVisualDescendants().OfType<ToggleButton>().ElementAt(index);
                    for (int attempt = 0; attempt < 80 && !Inside(tab, strip); attempt++)
                    {
                        Point at = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                        window.MouseWheel(at, new Vector(tab.TranslatePoint(default, strip)!.Value.X < 0 ? 0.5 : -0.5, 0));
                        Ui.Settle();
                    }
                    Assert.True(Inside(tab, strip), $"{id} tab {index} is not reachable inside its 200px pane: {tab.TranslatePoint(default, strip)}, tab {tab.Bounds.Size}, strip {strip.Bounds.Size}, offset {strip.Offset}");
                    Click(window, tab);
                    Assert.Equal(ToolAnchor.LeftTop, tool.Anchor);
                    CheckHeader(tool);
                }
                foreach (string navigation in new[] { "shift+tab", "tab" })
                {
                    foreach (int index in navigation == "tab" ? Enumerable.Range(0, tabs) : Enumerable.Range(0, tabs).Reverse())
                    {
                        ToggleButton tab = headerTools.GetVisualDescendants().OfType<ToggleButton>().ElementAt(index);
                        for (int attempt = 0; attempt < 30 && window.FocusManager?.GetFocusedElement() != tab; attempt++)
                        {
                            Ui.Press(window, navigation);
                        }
                        Assert.True(window.FocusManager?.GetFocusedElement() == tab, $"{id} tab {index} is not reachable with {navigation}; focused {window.FocusManager?.GetFocusedElement()}");
                        Assert.True(Inside(tab, strip), $"The keyboard-focused {id} tab {index} is outside its strip");
                        Ui.Press(window, "space");
                        Assert.True(headerTools.GetVisualDescendants().OfType<ToggleButton>().ElementAt(index).IsChecked == true);
                        Assert.Same(tab, window.FocusManager?.GetFocusedElement());
                    }
                }
                if (id == "localHistory")
                {
                    ScrollBar horizontal = Assert.Single(strip.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal && bar.IsEffectivelyVisible);
                    Thumb thumb = Assert.Single(horizontal.GetVisualDescendants().OfType<Thumb>(), control => control.IsEffectivelyVisible);
                    Point from = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                    double before = strip.Offset.X;
                    window.MouseMove(from);
                    Ui.Settle();
                    UiAudit.Capture(window, $"shared-header-{id}-before-thumb-{(light ? "light" : "dark")}");
                    string hit = string.Join(" > ", (window.InputHitTest(from) as Visual)?.GetVisualAncestors().OfType<Control>().Select(control => $"{control.GetType().Name}#{control.Name}") ?? []);
                    Assert.True(thumb.IsPointerOver, $"The scrollbar thumb is not the pointer target at {from}; hit {hit}, thumb {thumb.Bounds}, bar {horizontal.Bounds}, strip {strip.Bounds}, offset {strip.Offset}");
                    window.MouseDown(from, MouseButton.Left);
                    window.MouseMove(from - new Vector(40, 0));
                    window.MouseUp(from - new Vector(40, 0), MouseButton.Left);
                    Ui.Settle();
                    Assert.True(strip.Offset.X < before, $"Dragging the header thumb did not change offset {before} to {strip.Offset.X}; thumb {thumb.Bounds}, bar {horizontal.Bounds}, anchor {tool.Anchor}");
                    Assert.Equal(ToolAnchor.LeftTop, tool.Anchor);
                }
                if (id is "localHistory" or "git")
                {
                    ToggleButton active = Assert.Single(headerTools.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true);
                    ToggleButton inactive = headerTools.GetVisualDescendants().OfType<ToggleButton>().ElementAt(id == "git" ? 1 : 0);
                    object? selected = active.Tag;
                    Close(inactive);
                    Assert.Equal(tabs - 1, headerTools.GetVisualDescendants().OfType<ToggleButton>().Count());
                    Assert.Same(selected, Assert.Single(headerTools.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true).Tag);
                    Assert.Same(active, window.FocusManager?.GetFocusedElement());
                    ToggleButton[] remaining = [.. headerTools.GetVisualDescendants().OfType<ToggleButton>()];
                    ToggleButton previous = remaining[Array.IndexOf(remaining, active) - 1];
                    Close(active);
                    Assert.Equal(tabs - 2, headerTools.GetVisualDescendants().OfType<ToggleButton>().Count());
                    Assert.Same(previous, Assert.Single(headerTools.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true));
                    Assert.Same(previous, window.FocusManager?.GetFocusedElement());

                    void Close(ToggleButton tab)
                    {
                        for (int attempt = 0; attempt < 80 && !Inside(tab, strip); attempt++)
                        {
                            Point at = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                            window.MouseWheel(at, new Vector(tab.TranslatePoint(default, strip)!.Value.X < 0 ? 0.5 : -0.5, 0));
                            Ui.Settle();
                        }
                        Assert.True(Inside(tab, strip));
                        window.MouseMove(tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value);
                        Ui.Settle();
                        Button close = Assert.Single(tab.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
                        Assert.True(close.IsEffectivelyVisible);
                        Click(window, close);
                    }
                }
                Button options = Assert.Single(header.GetVisualDescendants().OfType<Button>(), button => Equals(ToolTip.GetTip(button), "Options"));
                Click(window, options);
                Assert.NotNull(dock.OpenOptions);
                Ui.Press(TopLevel.GetTopLevel(dock.OpenOptions.Items.OfType<MenuItem>().First()) ?? window, "escape");
                Ui.Settle();
                Assert.Null(dock.OpenOptions);
                UiAudit.Capture(window, $"shared-header-{id}-side-200-{(light ? "light" : "dark")}");

                Control? focusedTab = headerTools.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.IsChecked == true);
                if (focusedTab is not null)
                {
                    Click(window, focusedTab);
                    Assert.True(window.FocusManager?.GetFocusedElement() == focusedTab, $"{id} selected tab did not receive focus before moving");
                }

                dock.Move(tool, ToolAnchor.BottomLeft);
                dock.Show(id);
                Ui.Settle();
                CheckHeader(tool);
                if (focusedTab is not null)
                {
                    Assert.True(TopLevel.GetTopLevel(tool.Content)!.FocusManager?.GetFocusedElement() == focusedTab, $"{id} lost its focused selected tab while moving to a different pane: focused {TopLevel.GetTopLevel(tool.Content)!.FocusManager?.GetFocusedElement()}, visible {focusedTab.IsEffectivelyVisible}");
                }
                UiAudit.Capture(window, $"shared-header-{id}-bottom-{(light ? "light" : "dark")}");
                dock.SetMode(tool, ToolMode.Floating);
                Ui.Settle();
                Window floating = Assert.Single(window.OwnedWindows);
                floating.SetRenderScaling(2);
                floating.Width = 240;
                Ui.Settle();
                Assert.Equal(window.ActualThemeVariant, floating.ActualThemeVariant);
                CheckHeader(tool);
                if (focusedTab is not null)
                {
                    Assert.True(floating.FocusManager?.GetFocusedElement() == focusedTab, $"{id} lost its focused selected tab while floating");
                }
                window.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
                Ui.Settle();
                Assert.Equal(window.ActualThemeVariant, floating.ActualThemeVariant);
                window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
                Ui.Settle();
                Assert.Equal(window.ActualThemeVariant, floating.ActualThemeVariant);
                UiAudit.Capture(floating, $"shared-header-{id}-floating-{(light ? "light" : "dark")}");
                dock.SetMode(tool, ToolMode.Docked);
                dock.Show(id);
                Ui.Settle();
                CheckHeader(tool);
                if (focusedTab is not null)
                {
                    Assert.True(window.FocusManager?.GetFocusedElement() == focusedTab, $"{id} lost its focused selected tab while docking again");
                }
                if (id == "nuget")
                {
                    ThemeVariant? closedTheme = floating.ActualThemeVariant;
                    window.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
                    Ui.Settle();
                    Assert.Equal(closedTheme, floating.ActualThemeVariant);
                    dock.SetMode(tool, ToolMode.Floating);
                    Ui.Settle();
                    Window reopened = Assert.Single(window.OwnedWindows);
                    Assert.NotSame(floating, reopened);
                    Assert.Equal(window.ActualThemeVariant, reopened.ActualThemeVariant);
                    Assert.Same(focusedTab, reopened.FocusManager?.GetFocusedElement());
                    dock.SetMode(tool, ToolMode.Docked);
                    Ui.Settle();
                    window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
                    Ui.Settle();
                }
                Button hide = Assert.Single(Header(tool).GetVisualDescendants().OfType<Button>(), button => Equals(ToolTip.GetTip(button), "Hide (⇧⎋)"));
                Click(window, hide);
                Assert.False(dock.IsShown(id));
                dock.Show(id);
                Ui.Settle();
                CheckHeader(tool);
            }
        }
        finally
        {
            Task closed = window.TerminalPanel.CloseAllAsync();
            Ui.WaitFor(() => closed.IsCompleted, "all real header-test shells to close");
            closed.GetAwaiter().GetResult();
            window.Close();
        }

        static Grid Header(ToolWindow tool)
        {
            Control pane = tool.Content.GetVisualAncestors().OfType<Control>().First(control => control.GetType().Name == "ToolPane");
            return Assert.Single(pane.GetVisualDescendants().OfType<Grid>(), control => control.Name == "ToolHeader");
        }

        static void CheckHeader(ToolWindow tool)
        {
            Grid header = Header(tool);
            TextBlock title = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == tool.Title);
            Assert.True(Inside(title, header));
            Assert.True(title.TextLayout.Width <= title.Bounds.Width + 1, $"The {tool.Id} title clips outside its fitted header");
            Assert.Equal(tool.Title, ToolTip.GetTip(title));
            CheckActions(header);
            ScrollViewer strip = Assert.Single(header.GetVisualDescendants().OfType<ScrollViewer>(), control => control.Name == "ToolHeaderStrip");
            Assert.True(Inside(strip, header));
            Assert.True(strip.Bounds.Width > 0);
            Assert.True(header.Bounds.Height is 32 or 64, $"The {tool.Id} header did not settle to one or two rows: {header.Bounds.Size}");
            Rect titleBounds = new(title.TranslatePoint(default, header)!.Value, title.Bounds.Size);
            Assert.False(titleBounds.Intersects(new Rect(strip.TranslatePoint(default, header)!.Value, strip.Bounds.Size)));
        }
    }

    private static void Click(Window window, Control control)
    {
        Point hit = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(hit);
        window.MouseDown(hit, MouseButton.Left);
        window.MouseUp(hit, MouseButton.Left);
        Ui.Settle();
    }

    private static bool Inside(Control child, Control parent) => child.TranslatePoint(default, parent) is { } at
        && at.X >= -0.5 && at.Y >= -0.5 && at.X + child.Bounds.Width <= parent.Bounds.Width + 0.5
        && at.Y + child.Bounds.Height <= parent.Bounds.Height + 0.5;

    private static void CheckActions(Control bar)
    {
        List<Rect> taken = [];
        foreach (Button button in bar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible
            && !button.GetVisualAncestors().TakeWhile(ancestor => ancestor != bar).OfType<ScrollViewer>().Any()))
        {
            Assert.True(Inside(button, bar), $"'{button.Content ?? ToolTip.GetTip(button)}' escapes the {bar.Name} toolbar");
            Rect bounds = new(button.TranslatePoint(default, bar)!.Value, button.Bounds.Size);
            Assert.DoesNotContain(taken, prior => prior.Intersects(bounds));
            taken.Add(bounds);
        }
    }
}
