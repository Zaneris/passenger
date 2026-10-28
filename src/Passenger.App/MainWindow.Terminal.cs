using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The window's terminal: its tool window, the action that opens and leaves it, and the keys it takes ahead of
/// the IDE's.</summary>
public partial class MainWindow
{
    private ToolWindows.TerminalPanel? terminal;

    /// <summary>The Terminal tool window, built as it is first shown or asked for (a program run in it, Open In Terminal,
    /// an agent's command).</summary>
    public ToolWindows.TerminalPanel TerminalPanel => terminal ?? (ToolWindows.TerminalPanel)Dock[ToolWindowIds.Terminal].Content;

    private void AddTerminal()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Terminal, "Terminal", IconKeys.Terminal, MakeTerminal, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowTerminal,
            Tools = () => terminal?.HeaderTools,
            Menus = [new MenuPlace("Tools", 0, 2)],
            Activate = ToggleTerminal,
        });
        actions!.Add(new IdeAction(AppActions.NewTerminalSession, "New Terminal Session", "Terminal", _ => IdeErrors.Observe(NewTerminalSessionAsync())) { FromTerminal = true });
    }

    /// <summary>A shell of its own in the open folder, its tab made before the window shows, so showing it starts no other.</summary>
    private Task<Terminal.TerminalSession> NewTerminalSessionAsync()
    {
        Task<Terminal.TerminalSession> starting = TerminalPanel.NewSessionAsync();
        ShowToolWindow(ToolWindowIds.Terminal);
        return starting;
    }

    /// <summary>The Terminal window's view, as it is first shown or asked for, with the settings in force then.</summary>
    private ToolWindows.TerminalPanel MakeTerminal()
    {
        ToolWindows.TerminalPanel panel = terminal = new ToolWindows.TerminalPanel(() => Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        panel.ActionRequested += (_, action) => Request(action);
        panel.Emptied += (_, _) =>
        {
            Dock.Hide(ToolWindowIds.Terminal);
            if (Editors.Active is not null)
            {
                Editors.CodeEditor.Focus();
            }
        };
        // Going to the terminal is leaving the code, as going to another program is: what is not saved is (Autosave).
        panel.View.GotFocus += (_, _) => SaveOnLeaving();
        AddTerminalLinks(panel);
        FollowTerminalSettings(panel, settings, null);
        return panel;
    }

    /// <summary>The terminal's font and shell as Settings give them; a shell no longer set given back to the system's.</summary>
    private static void FollowTerminalSettings(ToolWindows.TerminalPanel panel, Passenger.App.Settings.AppSettings next, Passenger.App.Settings.AppSettings? before)
    {
        panel.View.FontSize = next.TerminalFontSize;
        if (next.TerminalShell is { } shell && Run.CommandLine.Split(shell) is { Count: > 0 } parts)
        {
            panel.Shell = (parts[0], [.. parts.Skip(1)]);
        }
        else if (before?.TerminalShell is not null)
        {
            panel.Shell = null;
        }
    }

    /// <summary>Open In, Terminal from the Explorer: a new tab of the Terminal window, its shell started in a
    /// folder.</summary>
    public Task<Terminal.TerminalSession> OpenTerminalAt(string at)
    {
        // The tab is made before the window shows, so showing it starts no shell of its own in the open folder.
        Task<Terminal.TerminalSession> starting = TerminalPanel.NewSessionAsync(at);
        ShowToolWindow(ToolWindowIds.Terminal);
        return starting;
    }

    /// <summary>The terminal's shortcut: to the terminal from anywhere, and back to the editor from the terminal.</summary>
    private void ToggleTerminal()
    {
        if (terminal?.View.IsFocused == true)
        {
            Dock.Hide(ToolWindowIds.Terminal);
            if (Editors.Active is not null)
            {
                Editors.CodeEditor.Focus();
            }
            return;
        }
        ShowToolWindow(ToolWindowIds.Terminal);
        TerminalPanel.FocusView();
    }

    /// <summary>The actions whose keys a program a run configuration runs in the terminal leaves the IDE: Run, Debug,
    /// Rerun, Stop and the debugger's, so a person runs it again, steps and stops with them.</summary>
    private static readonly HashSet<string> ProgramLeaves =
    [
        AppActions.RunProgram, AppActions.Debug, AppActions.Rerun, AppActions.Stop,
        AppActions.Resume, AppActions.Pause, AppActions.StepOver, AppActions.StepInto, AppActions.StepOut,
    ];

    /// <summary>Whether a key goes to the focused terminal rather than the IDE: everything but Cmd's keys on the Mac (save
    /// Cmd+K, its Clear Buffer), the second key of a chord already begun, the shortcuts of the actions that run from the
    /// terminal (<see cref="IdeAction.FromTerminal"/>: the tool windows' and their frame's, but never a Ctrl+letter, the
    /// shell's), and, in a program's tab, Stop's and the debugger's (<see cref="ProgramLeaves"/>).</summary>
    private bool TerminalTakes(object? focused, KeyEventArgs e)
    {
        if (focused is not Terminal.TerminalView || keys.IsPending)
        {
            return false;
        }
        if (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            // Cmd's keys are the IDE's, but for Cmd+K, the terminal's Clear Buffer (the editor's chords begin with it).
            return e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.K;
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.A and <= Key.Z)
        {
            // A control character the shell reads (^W deletes a word), whatever a keymap gives it (VS Code's ^W on the Mac).
            return true;
        }
        if (keys.Keymap.ActionFor(new Shortcut(Keystroke.From(e))) is not { } action)
        {
            return true;
        }
        return actions![action] is not { FromTerminal: true } && !(terminal?.ActiveTab?.Run is not null && ProgramLeaves.Contains(action));
    }
}
