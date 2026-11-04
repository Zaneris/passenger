using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Renders the main window to a PNG and exits (<c>--screenshot PATH</c>), after opening what the command line
/// names and taking its steps. It runs on Avalonia's headless platform with real Skia drawing, so nothing appears on
/// screen and nothing takes the focus. The picture is the compositor's own frame, the path the desktop draws through,
/// not a re-render into a bitmap, so it shows what a person would see minus the system's window chrome;
/// <c>--window-buttons</c> draws macOS's traffic lights back in (<see cref="TrafficLights"/>).</summary>
public static class Screenshot
{
    /// <summary>How long to wait for background work (colouring, indexing) before capturing anyway.</summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(90);

    /// <summary>How long an agent (<c>--agent</c>) may run, as long as the longest <c>--wait</c>.</summary>
    private static readonly TimeSpan AgentLimit = TimeSpan.FromMinutes(10);

    public static int Run(LaunchOptions launch, ScreenshotOptions options)
    {
        PassengerApp.RunsHeadless();
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(builder => Program.ApplyTheme(builder.Instance!, launch))
            .SetupWithoutStarting();

        // A capture lets in only its own agent: no socket at all without one.
        MainWindow window = new() { Width = options.Size.Width, Height = options.Size.Height, Autosaves = false, AgentsAllowed = options.Steps.OfType<ScriptStep.Agent>().Any(), TakesLaunches = false };
        window.SetRenderScaling(options.Scale);
        bool settled;
        try
        {
            settled = Prepare(window, launch, options.Steps);
        }
        catch (StepRefused refused)
        {
            PassengerApp.Quit();
            PassengerApp.End();
            Console.Error.WriteLine($"screenshot: {refused.Message}");
            return 2;
        }

        // A diff or a dialog the steps opened is what the person would be looking at, drawn at the window's scale.
        Avalonia.Controls.Window front = window.Frontmost;
        if (front != window)
        {
            front.SetRenderScaling(options.Scale);
            settled &= Settle();
        }
        else if (options.WindowButtons && window.DrawTrafficLights())
        {
            settled &= Settle();
        }
        WriteableBitmap frame = front.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless window rendered no frame.");
        using (frame)
        {
            if (options.Crop is { } crop)
            {
                using WriteableBitmap part = Crop(frame, PixelRect.FromRect(crop, options.Scale));
                part.Save(options.Path, PngBitmapEncoderOptions.Default);
            }
            else
            {
                frame.Save(options.Path, PngBitmapEncoderOptions.Default);
            }
        }

        double elapsed = Stopwatch.GetElapsedTime(Program.StartedAt).TotalMilliseconds;
        // A program or a build the steps started ends with the windows, as it does with the person's.
        PassengerApp.Quit();
        PassengerApp.End();
        if (!settled)
        {
            // The picture is kept to show what was still going on, but it is not the settled window.
            Console.Error.WriteLine($"screenshot: {options.Path} taken with background work still pending after {SettleLimit.TotalSeconds:0} s");
            return 1;
        }
        Console.WriteLine($"screenshot: {options.Path} after {elapsed:F0} ms");
        return 0;
    }

    /// <summary>Shows the window, opens what the command line names and takes the steps, the background work each
    /// starts landed before the next: the window as its picture is taken, with no wait of its own. False when work was
    /// still pending at the limit.</summary>
    public static bool Prepare(MainWindow window, LaunchOptions launch, IReadOnlyList<ScriptStep> steps)
    {
        window.Show();
        bool settled = Settle();
        window.Start(launch);
        settled &= Settle();
        foreach (ScriptStep step in steps)
        {
            if (step is ScriptStep.Open open)
            {
                // As File, Open's panel hands the window what was chosen.
                window.Open(open.Path);
            }
            else if (step is ScriptStep.Agent agent)
            {
                RunAgent(window, agent);
            }
            else if (step is ScriptStep.Launch launched)
            {
                settled &= Start(window, launched.Start);
            }
            else
            {
                // A dialog a step opened takes the next ones, as it takes a person's keys and clicks.
                Take(window.Frontmost, step);
            }
            settled &= Settle();
            // A dialog the step opened takes the window's scale at once, or what is under the pointer is found wrongly.
            if (window.Frontmost is { } front && front != window && front.RenderScaling != window.RenderScaling)
            {
                front.SetRenderScaling(window.RenderScaling);
                settled &= Settle();
            }
        }
        return settled;
    }

    private static void Take(Avalonia.Controls.Window window, ScriptStep step)
    {
        switch (step)
        {
            case ScriptStep.Type type:
                window.KeyTextInput(type.Text);
                break;
            case ScriptStep.Key key:
                RawInputModifiers modifiers = Raw(key.Stroke.Modifiers);
                // A dialog its key closes (Enter on Attach to Process) takes no release: it has gone.
                bool closed = false;
                EventHandler gone = (_, _) => closed = true;
                window.Closed += gone;
                window.KeyPress(key.Stroke.Key, modifiers, PhysicalKey.None, null);
                window.Closed -= gone;
                if (!closed)
                {
                    window.KeyRelease(key.Stroke.Key, modifiers, PhysicalKey.None, null);
                }
                break;
            case ScriptStep.Wait wait:
                Pump(wait.For);
                break;
            case ScriptStep.Hover hover:
                window.MouseMove(hover.At, RawInputModifiers.None);
                // Past the editor's hover delay, and the card's answer.
                Take(window, new ScriptStep.Wait(TimeSpan.FromMilliseconds(1500)));
                break;
            case ScriptStep.Drag drag:
                window.MouseDown(drag.From, MouseButton.Left, RawInputModifiers.None);
                for (int i = 1; i <= 10; i++)
                {
                    window.MouseMove(drag.From + ((drag.To - drag.From) * (i / 10.0)), RawInputModifiers.LeftMouseButton);
                }
                if (drag.Release)
                {
                    window.MouseUp(drag.To, MouseButton.Left, RawInputModifiers.None);
                }
                break;
            case ScriptStep.Click click:
                for (int i = 0; i < click.Count; i++)
                {
                    window.MouseDown(click.At, click.Button, RawInputModifiers.None);
                    window.MouseUp(click.At, click.Button, RawInputModifiers.None);
                }
                break;
            case ScriptStep.ClickNamed named:
                Point centre = Centre(window, named.Name);
                // The pointer goes there first, as a person's does, so the control is under it as it is pressed.
                window.MouseMove(centre, RawInputModifiers.None);
                Take(window, new ScriptStep.Click(centre, 1));
                break;
        }
    }

    /// <summary>The centre of the control of a name shown in a window, the window's own before a template's part of that
    /// name; a name no control shown has is refused, since a step that clicks nothing would picture the wrong thing.</summary>
    private static Point Centre(Avalonia.Controls.Window window, string name)
    {
        Avalonia.Controls.Control control = window.GetVisualDescendants().OfType<Avalonia.Controls.Control>()
            .Where(c => c.Name == name && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
            .OrderBy(c => c.TemplatedParent is null ? 0 : 1)
            .FirstOrDefault()
            ?? throw new StepRefused($"--click-name {name}: no control named {name} is shown in the window");
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new StepRefused($"--click-name {name}: {name} is not in the window");
    }

    /// <summary>Runs an agent in the Terminal window, its calls answered by the window as they come, until it ends: held as
    /// background work all the while, so the picture waits for it; refused unless it ends in time with exit code 0, since
    /// an agent that failed would picture the wrong thing.</summary>
    private static void RunAgent(MainWindow window, ScriptStep.Agent agent)
    {
        string named = $"--agent '{agent.Text}'";
        Task<Run.TerminalRun> starting;
        try
        {
            starting = window.RunAgent(agent.Command);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or ArgumentException or UnauthorizedAccessException)
        {
            throw new StepRefused($"{named}: no agent can reach the window: {error.Message}");
        }
        Task<int> ended = Ended(starting);
        BackgroundWork.Track(ended);
        Stopwatch waited = Stopwatch.StartNew();
        while (!ended.IsCompleted && waited.Elapsed < AgentLimit)
        {
            Pump(TimeSpan.FromMilliseconds(100));
        }
        if (!ended.IsCompleted)
        {
            if (starting.IsCompletedSuccessfully)
            {
                starting.Result.Stop();
            }
            throw new StepRefused($"{named} was still running after {AgentLimit.TotalMinutes:0} minutes");
        }
        if (ended.IsFaulted)
        {
            throw new StepRefused($"{named}: {ended.Exception!.GetBaseException().Message}");
        }
        if (ended.Result != 0)
        {
            throw new StepRefused($"{named} ended with exit code {ended.Result}");
        }

        static async Task<int> Ended(Task<Run.TerminalRun> starting) => await (await starting).Ended;
    }

    /// <summary>Runs or debugs a configuration as the window's own <c>--run</c> does, held as background work until its
    /// action has run and its start handed over, so the picture waits for its build and start, never its program; refused
    /// where it could not start, since the picture of a run that never began would be the wrong one. False when work was
    /// still pending at the limit.</summary>
    private static bool Start(MainWindow window, ConfigurationStart start)
    {
        Task<string?> starting = window.StartConfigurationAsync(start);
        BackgroundWork.Track(starting);
        bool settled = Settle();
        if (starting.IsFaulted)
        {
            throw new StepRefused($"{start.Flag}: {starting.Exception!.GetBaseException().Message}");
        }
        return starting.IsCompletedSuccessfully && starting.Result is { } refused ? throw new StepRefused(refused) : settled;
    }

    /// <summary>A step the window cannot take as asked, which ends the screenshot with exit code 2 and why.</summary>
    private sealed class StepRefused(string message) : Exception(message);

    /// <summary>Runs the dispatcher's loop for a while: its timers (the editor's hover, a caret's blink)
    /// fire only inside a running frame, which running the queued jobs alone never is.</summary>
    public static void Pump(TimeSpan duration)
    {
        DispatcherFrame frame = new();
        DispatcherTimer.RunOnce(() => frame.Continue = false, duration, DispatcherPriority.Send);
        Dispatcher.UIThread.PushFrame(frame);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
    }

    /// <summary>Lets queued UI work, rendering and background work finish, so the picture shows the
    /// settled state rather than a frame mid-way; false when work was still pending at the limit.</summary>
    private static bool Settle()
    {
        Stopwatch waited = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            if (BackgroundWork.Pending == 0)
            {
                return true;
            }
            Thread.Sleep(5);
        }
        while (waited.Elapsed < SettleLimit);
        return false;
    }

    /// <summary>A copy of one region of a frame, clamped to the frame.</summary>
    private static WriteableBitmap Crop(WriteableBitmap frame, PixelRect region)
    {
        PixelRect bounds = region.Intersect(new PixelRect(frame.PixelSize));
        WriteableBitmap part = new(bounds.Size, frame.Dpi, frame.Format, AlphaFormat.Premul);
        using ILockedFramebuffer target = part.Lock();
        frame.CopyPixels(bounds, target.Address, target.RowBytes * bounds.Height, target.RowBytes);
        return part;
    }

    private static RawInputModifiers Raw(KeyModifiers modifiers)
    {
        RawInputModifiers raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            raw |= RawInputModifiers.Shift;
        }
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            raw |= RawInputModifiers.Control;
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            raw |= RawInputModifiers.Alt;
        }
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            raw |= RawInputModifiers.Meta;
        }
        return raw;
    }
}
