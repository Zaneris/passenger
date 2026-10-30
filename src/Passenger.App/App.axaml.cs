using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Passenger.Core;

namespace Passenger.App;

public partial class App : Application
{
    /// <summary>Whether the class handlers are in, which are the process's (static events), not an App's: a process may
    /// make several Apps, and each adding them again would make every template applied anywhere ask each one.</summary>
    private static bool classHandlersAdded;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Themes.TakeAccentFromTokens(this);
        if (classHandlersAdded)
        {
            return;
        }
        classHandlersAdded = true;
        MenuShadow.Install();
        AgentAsks.Install();
        // In-window menus write keys as the keymap does (⇧⌘R), not as Fluent writes a KeyGesture (Shift+Cmd+R).
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) =>
        {
            if (item.InputGesture is { } keys && e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } shown)
            {
                shown.Text = new Passenger.Editor.Keystroke(keys.Key, keys.KeyModifiers).Display();
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Whichever dispatcher is the UI thread's when a tracked task ends: a headless session can bring its own.
        BackgroundWork.Install(work => Dispatcher.UIThread.Post(work));
        IdeErrors.Install();
        // The screenshot mode has no lifetime and makes its own window.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Only the person's window updates Passenger, and only a copy its installer or disk image put in place.
            Updates.Updater? updater = Updates.Updater.OfThisCopy(State.StateStore.DefaultRoot, out string notUpdating);
            MainWindow window = new() { AgentsAllowed = Program.Options.ListenForAgents, SelfUpdate = updater };
            // The person's windows open on the layout and settings they left; headless ones keep the defaults.
            window.KeepLayout();
            window.KeepSettings();
            // A theme named on the command line is the run's, over the one kept.
            Program.ApplyTheme(this, Program.Options);
            // Its default size is a large screen's: on a smaller one it opens inside the working area, centred.
            window.FitToScreen(keepPlace: false);
            window.Opened += (_, _) =>
            {
                IdeErrors.Write($"Passenger {Product.Version} ({Product.Commit ?? "no commit"}) on {RuntimeInformation.OSDescription} "
                    + $"({Product.Runtime}): window shown {Stopwatch.GetElapsedTime(Program.StartedAt).TotalSeconds:0.00} s after launch");
                window.Start(Program.Options);
                // The process asks for the latest release once, in this window, which hands the watch on as it closes.
                IdeErrors.Write(updater is null ? $"Updates: this copy only says when a newer release is out: {notUpdating}"
                    : $"Updates: this copy installs updates, in {updater.Installed}");
                window.WatchForUpdates();
                // The notes of a version updated to, once; one an agent started to work in leaves them to the person's next start.
                if (Mcp.McpProxy.StartedForAgent)
                {
                    IdeErrors.Write("What's New: started for an agent, so no notes are shown");
                }
                else
                {
                    window.ShowWhatsNewOnce();
                }
                window.ShowActivityInDiscord();
            };
            desktop.MainWindow = window;
            if (OperatingSystem.IsMacOS())
            {
                NativeMenu.SetMenu(this, window.ApplicationMenu());
            }
            // macOS hands the running Passenger what the Finder or the Dock's icon opens, rather than starting another.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += OnActivated;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is FileActivatedEventArgs { Files: { Count: > 0 } files })
        {
            string[] paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
            IdeErrors.Write($"Opened by the system: {string.Join(", ", paths)}");
            UiThread.Post(() => PassengerApp.OpenFromSystem(paths));
        }
    }
}
