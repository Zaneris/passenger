using Passenger.App.ToolWindows;

namespace Passenger.App.Run;

/// <summary>A program run with the Run or Debug window's console whose output said it needs a terminal
/// (<see cref="TerminalNeeds"/>): its configuration, the call it needs one for, and whether Debug started it.</summary>
public sealed record TerminalNeed(string Configuration, string Call, bool Debugged)
{
    /// <summary>The window whose console gave it pipes.</summary>
    public string Window => Debugged ? "Debug" : "Run";

    /// <summary>What it needs, in Refusals' words.</summary>
    public string Said => Refusals.NeedsTerminal(Configuration, Call, Window);
}

public sealed partial class BuildRunController
{
    private NeedHearing? runNeeds;
    private NeedHearing? debugNeeds;

    /// <summary>What the program Run last started in the Run window said it needs a terminal for; null where it said
    /// nothing of one, or ran elsewhere.</summary>
    public TerminalNeed? RunNeed => runNeeds?.Need;

    /// <summary>What the program Debug last started with the Debug window's console said it needs a terminal for; null where
    /// it said nothing of one, or ran elsewhere.</summary>
    public TerminalNeed? DebugNeed => debugNeeds?.Need;

    /// <summary>Hears what a configuration's program writes to a console, Run's or a Debug tab's, until it says it needs a
    /// terminal, when the window offers there to start it in one; null hears none, forgetting the need heard last.</summary>
    private void HearNeeds(OutputView? console, string configuration, bool debugged)
    {
        ref NeedHearing? hearing = ref debugged ? ref debugNeeds : ref runNeeds;
        hearing?.LetGo();
        hearing = console is null ? null : new NeedHearing(console, configuration, debugged, need => parts.NeedsTerminal?.Invoke(need, console));
    }

    /// <summary>One console's program heard until it says it needs a terminal, or until let go.</summary>
    private sealed class NeedHearing
    {
        private readonly TerminalNeeds needs = new();
        private readonly OutputView console;
        private readonly string configuration;
        private readonly bool debugged;
        private readonly Action<TerminalNeed> heard;

        public NeedHearing(OutputView console, string configuration, bool debugged, Action<TerminalNeed> heard)
        {
            this.console = console;
            this.configuration = configuration;
            this.debugged = debugged;
            this.heard = heard;
            console.Written += OnWritten;
        }

        public TerminalNeed? Need { get; private set; }

        public void LetGo() => console.Written -= OnWritten;

        private void OnWritten(object? sender, ConsoleWritten written)
        {
            foreach (ConsoleRun run in written.Runs)
            {
                if (needs.Hear(written.Text.AsSpan(run.Start, run.Length), run.Stream == ConsoleStream.Error))
                {
                    LetGo();
                    Need = new TerminalNeed(configuration, needs.Need!, debugged);
                    heard(Need);
                    return;
                }
            }
        }
    }
}
