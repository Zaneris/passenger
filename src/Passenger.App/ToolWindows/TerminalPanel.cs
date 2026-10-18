using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Terminal;

namespace Passenger.App.ToolWindows;

/// <summary>A tab of the Terminal window: a shell and its name (Local), or a program a run configuration runs there and
/// the configuration's name.</summary>
public sealed class TerminalTab(TerminalSession session, string name, Run.TerminalRun? run = null) : Observable
{
    private bool isActive;
    private bool hasEnded;

    public TerminalSession Session { get; } = session;

    public string Name { get; } = name;

    /// <summary>The program the tab runs in place of a shell, or null for a shell's.</summary>
    public Run.TerminalRun? Run { get; } = run;

    public bool IsActive
    {
        get => isActive;
        set => Set(ref isActive, value);
    }

    /// <summary>Whether its program has ended, the tab kept to say how.</summary>
    public bool HasEnded
    {
        get => hasEnded;
        set => Set(ref hasEnded, value);
    }
}

/// <summary>Rider's Terminal window: a shell per tab in the open folder, the first started when the window is first
/// shown, + for another; a tab whose shell ends closes, and the window hides with its last. A run configuration that uses
/// the terminal runs its program in a tab of its own (<see cref="RunAsync"/>), which stays once it ends.</summary>
public sealed class TerminalPanel : DockPanel
{
    private readonly ObservableCollection<TerminalTab> tabs = [];
    private readonly TerminalView view = new();
    private readonly ScrollViewer sessionStrip;
    private readonly Func<string> folder;
    private int opened;

    /// <param name="folder">Where a new shell starts: the open folder, else the home folder.</param>
    public TerminalPanel(Func<string> folder)
    {
        this.folder = folder;
        ItemsControl strip = new()
        {
            ItemsSource = tabs,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<TerminalTab>((tab, _) => tab is null ? new TextBlock() : Tab(tab), supportsRecycling: false),
        };
        sessionStrip = new ScrollViewer { Name = "TerminalSessionStrip", Classes = { "toolTabs" }, Content = strip };
        sessionStrip.PropertyChanged += (_, change) =>
        {
            if (change.Property == ScrollViewer.ViewportProperty)
            {
                foreach (ToggleButton tab in sessionStrip.GetVisualDescendants().OfType<ToggleButton>())
                {
                    tab.MaxWidth = Math.Max(0, sessionStrip.Viewport.Width);
                }
                RevealSession();
            }
        };
        Border bar = new()
        {
            [DockProperty] = Dock.Top,
            Height = 32,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = sessionStrip,
        };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("Passenger.Divider"));
        Children.Add(bar);
        Children.Add(view);
        Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Terminal.Background"));
        AttachedToVisualTree += (_, _) =>
        {
            if (tabs.Count == 0)
            {
                _ = NewSessionAsync();
            }
            FocusView();
        };
        Button add = new() { Classes = { "tool" }, Content = new PathIcon() };
        ((PathIcon)add.Content).Bind(PathIcon.DataProperty, add.GetResourceObservable("Icon.Add"));
        ToolTip.SetTip(add, "New Session");
        add.Click += (_, _) => _ = NewSessionAsync();
        HeaderTools = add;
    }

    /// <summary>Raised when the last shell has ended, so the window can hide.</summary>
    public event EventHandler? Emptied;

    /// <summary>The window header's own tools: New Session.</summary>
    public Control HeaderTools { get; }

    /// <summary>The shell to start in place of the user's, and its arguments (tests start a plain one).</summary>
    public (string App, string[] Arguments)? Shell { get; set; }

    public TerminalView View => view;

    public IReadOnlyList<TerminalTab> Tabs => tabs;

    public TerminalSession? Active => ActiveTab?.Session;

    /// <summary>The tab shown, a shell's or a program's.</summary>
    public TerminalTab? ActiveTab => tabs.FirstOrDefault(t => t.IsActive);

    /// <summary>Runs a program in a tab of its own named for what runs it (a run configuration), in place of an ended one
    /// of that name, and shows it, the keys going to it. The program gets the terminal's variables over those the start
    /// sets, and leads a process group of its own (a session, on macOS and Linux). Throws a <see cref="Run.LaunchException"/>
    /// saying why it could not start, which its tab says too.</summary>
    public async Task<Run.TerminalRun> RunAsync(string name, System.Diagnostics.ProcessStartInfo start)
    {
        UiThread.Check();
        int at = tabs.Count;
        if (tabs.FirstOrDefault(t => t.HasEnded && t.Name == name) is { } ended)
        {
            at = tabs.IndexOf(ended);
            tabs.RemoveAt(at);
            _ = ended.Session.DisposeAsync();
        }
        TerminalSession session = new(view.Session?.Screen.Columns ?? 120, view.Session?.Screen.Rows ?? 30);
        Run.TerminalRun run = new(name, session, start);
        TerminalTab tab = new(session, name, run);
        tabs.Insert(at, tab);
        Show(tab);
        if (view.Bounds.Width <= 0)
        {
            // The window just shown: the view is laid out, and the screen takes its size, before the program draws.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => view.Session = session, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        session.Exited += (_, code) =>
        {
            tab.HasEnded = true;
            run.OnExited(code);
        };
        try
        {
            await session.StartAsync(start.WorkingDirectory, start.FileName, [.. start.ArgumentList], Variables(start));
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            string problem = $"Could not start {Path.GetFileName(start.FileName)}: {failed.Message}";
            tab.HasEnded = true;
            run.Failed(problem);
            throw new Run.LaunchException(problem);
        }
        run.Started(session.ProcessId ?? 0);
        return run;
    }

    /// <summary>A start's environment as the terminal takes it, over the IDE's own: each variable it sets, and an empty value
    /// for each it takes away (a git hook's, for one).</summary>
    private static Dictionary<string, string> Variables(System.Diagnostics.ProcessStartInfo start)
    {
        Dictionary<string, string> variables = new(start.Environment.Count, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, string? value) in start.Environment)
        {
            variables[name] = value ?? string.Empty;
        }
        foreach (System.Collections.DictionaryEntry inherited in Environment.GetEnvironmentVariables())
        {
            if (inherited.Key is string name && !start.Environment.ContainsKey(name))
            {
                variables[name] = string.Empty;
            }
        }
        return variables;
    }

    /// <summary>Starts a shell in a tab of its own and shows it: in the open folder, or in another (Open In, Terminal).</summary>
    public async Task<TerminalSession> NewSessionAsync(string? at = null)
    {
        TerminalSession session = new(view.Session?.Screen.Columns ?? 120, view.Session?.Screen.Rows ?? 30);
        string name = ++opened == 1 ? "Local" : $"Local ({opened})";
        TerminalTab tab = new(session, name);
        tabs.Add(tab);
        Show(tab);
        session.Exited += (_, _) => Close(tab);
        try
        {
            await session.StartAsync(at ?? folder(), Shell?.App, Shell?.Arguments);
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            session.Screen.Feed($"Passenger could not start a shell: {failed.Message}\r\n");
        }
        return session;
    }

    /// <summary>Ends every shell, and every program with all it started, as the IDE closes.</summary>
    public async Task CloseAllAsync()
    {
        TerminalTab[] closing = [.. tabs];
        // Detach old tabs now so their teardown cannot clear a replacement session.
        tabs.Clear();
        view.Session = null;
        List<Task> disposing = [];
        foreach (TerminalTab tab in closing)
        {
            tab.Run?.Stop();
            // Start every close before yielding: the window's dispatcher may stop after Closed returns.
            disposing.Add(tab.Session.DisposeAsync().AsTask());
        }
        await Task.WhenAll(disposing);
    }

    private void Show(TerminalTab tab)
    {
        foreach (TerminalTab each in tabs)
        {
            each.IsActive = each == tab;
        }
        if (view.Session != tab.Session)
        {
            view.Session = tab.Session;
        }
        FocusView();
        RevealSession();
    }

    private void RevealSession() => Dispatcher.UIThread.Post(() =>
    {
        ToggleButton[] buttons = [.. sessionStrip.GetVisualDescendants().OfType<ToggleButton>()];
        // Browsing another tab with the keyboard must not hide its focused action or switch the running session.
        (buttons.FirstOrDefault(button => button.IsKeyboardFocusWithin) ?? buttons.FirstOrDefault(button => button.IsChecked == true))?.BringIntoView();
    }, DispatcherPriority.Loaded);

    /// <summary>The keys to the shell, once the view is laid out (a control not yet arranged cannot take the focus).</summary>
    public void FocusView() => Avalonia.Threading.Dispatcher.UIThread.Post(() => view.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);

    private void Close(TerminalTab tab)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        tabs.RemoveAt(index);
        _ = tab.Session.DisposeAsync();
        if (tabs.Count == 0)
        {
            view.Session = null;
            Emptied?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (tab.IsActive)
        {
            Show(tabs[Math.Min(index, tabs.Count - 1)]);
        }
        else
        {
            FocusView();
        }
    }

    private Control Tab(TerminalTab tab)
    {
        TextBlock name = new() { Text = tab.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Classes.Set("muted", tab.HasEnded);
        PathIcon closeGlyph = new() { Classes = { "small" } };
        closeGlyph.Bind(PathIcon.DataProperty, closeGlyph.GetResourceObservable("Icon.Close"));
        Button close = new() { Classes = { "tabClose" }, Content = closeGlyph };
        ToolTip.SetTip(close, "Close Session");
        close.Click += (_, e) =>
        {
            if (tab.Run is { } run)
            {
                run.Stop();
            }
            else
            {
                tab.Session.Kill();
            }
            Close(tab);
            e.Handled = true;
        };
        ToggleButton button = new()
        {
            Classes = { "panelTab" },
            IsChecked = tab.IsActive,
            MaxWidth = sessionStrip.Viewport.Width > 0 ? sessionStrip.Viewport.Width : double.PositiveInfinity,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Children = { name, close } },
        };
        Grid.SetColumn(close, 1);
        ToolTip.SetTip(button, tab.Name);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TerminalTab.IsActive))
            {
                button.IsChecked = tab.IsActive;
            }
            else if (e.PropertyName == nameof(TerminalTab.HasEnded))
            {
                name.Classes.Set("muted", tab.HasEnded);
            }
        }
        button.AttachedToVisualTree += (_, _) =>
        {
            tab.PropertyChanged += Changed;
            button.IsChecked = tab.IsActive;
            name.Classes.Set("muted", tab.HasEnded);
        };
        button.DetachedFromVisualTree += (_, _) => tab.PropertyChanged -= Changed;
        button.Click += (_, _) =>
        {
            Show(tab);
            // Clicking an already selected tab keeps it selected, as the other tool-window tabs do.
            button.IsChecked = true;
        };
        return button;
    }
}
