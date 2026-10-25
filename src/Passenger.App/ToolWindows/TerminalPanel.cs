using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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

/// <summary>The Terminal window: a shell per tab in the open folder, the first started when the window is first
/// shown, + for another; a tab whose shell ends closes, and the window hides with its last. A run configuration that uses
/// the terminal runs its program in a tab of its own (<see cref="RunAsync"/>), which stays once it ends.</summary>
public sealed class TerminalPanel : DockPanel
{
    private readonly ObservableCollection<TerminalTab> tabs = [];
    private readonly TerminalView view = new();
    private readonly Func<string> folder;
    private int opened;

    /// <param name="folder">Where a new shell starts: the open folder, else the home folder.</param>
    public TerminalPanel(Func<string> folder)
    {
        this.folder = folder;
        // The shells' tabs are in the window's header, after its title, then New Session.
        ItemsControl strip = new()
        {
            ItemsSource = tabs,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }),
            ItemTemplate = new FuncDataTemplate<TerminalTab>((tab, _) => tab is null ? new TextBlock() : Tab(tab), supportsRecycling: false),
        };
        Children.Add(view);
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Terminal_Background));
        AttachedToVisualTree += (_, _) =>
        {
            if (tabs.Count == 0)
            {
                IdeErrors.Observe(NewSessionAsync());
            }
            FocusView();
        };
        Button add = ToolButtons.Of(IconKeys.Add, "New Session", () => IdeErrors.Observe(NewSessionAsync()));
        HeaderTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { strip, add } };
    }

    /// <summary>Raised when the last shell has ended, so the window can hide.</summary>
    public event EventHandler? Emptied;

    /// <summary>The window header's own tools: New Session.</summary>
    public Control HeaderTools { get; }

    /// <summary>The shell to start in place of the user's, and its arguments.</summary>
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
            IdeErrors.Observe(ended.Session.DisposeAsync().AsTask());
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
        foreach (TerminalTab tab in tabs.ToArray())
        {
            tab.Run?.Stop();
            await tab.Session.DisposeAsync();
        }
        tabs.Clear();
        view.Session = null;
    }

    private void Show(TerminalTab tab)
    {
        foreach (TerminalTab each in tabs)
        {
            each.IsActive = each == tab;
        }
        view.Session = tab.Session;
        FocusView();
    }

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
        IdeErrors.Observe(tab.Session.DisposeAsync().AsTask());
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
    }

    private Control Tab(TerminalTab tab)
    {
        TextBlock name = new() { Text = tab.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Classes.Set("muted", tab.HasEnded);
        ToggleButton button = ToolTabs.Tab(name, tab.IsActive, () =>
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
        }, "Close Session");
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalTab.IsActive))
            {
                button.IsChecked = tab.IsActive;
            }
            else if (e.PropertyName == nameof(TerminalTab.HasEnded))
            {
                name.Classes.Set("muted", tab.HasEnded);
            }
        };
        // A press on the open tab leaves it open, where the toggle would let it go.
        button.Click += (_, _) =>
        {
            Show(tab);
            button.IsChecked = tab.IsActive;
        };
        return button;
    }
}
