using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Dap;

namespace Passenger.App.Run;

/// <summary>A program run with the Run or Debug window's console whose output said it needs a terminal
/// (<see cref="TerminalNeeds"/>): its configuration, the call it needs one for, and whether Debug started it.</summary>
public sealed record TerminalNeed(string Configuration, string Call, bool Debugged)
{
    /// <summary>Whether Profile started it, writing to the Run window's console.</summary>
    public bool Profiled { get; init; }

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

    /// <summary>What the program a Debug tab shows said it needs a terminal for, where Debug last started it with that tab's
    /// console; null otherwise.</summary>
    public TerminalNeed? DebugNeedOf(DebugTab tab) => debugNeeds is { Need: { } need } hearing && hearing.Console == tab.Console ? need : null;

    /// <summary>Hears the program Profile starts in the Run window's console as Run's own (<see cref="RunNeed"/>), whose
    /// Run in the Terminal the window offers: Profile starts a program in no terminal.</summary>
    public void HearProfiled(OutputView console, string configuration) => HearNeeds(console, configuration, debugged: false, profiled: true);

    /// <summary>Hears what a configuration's program writes to a console, Run's or a Debug tab's, until it says it needs a
    /// terminal, when the window offers there to start it in one; null hears none. The need heard last is forgotten, and
    /// its offer, which nothing then takes, goes from its console.</summary>
    private void HearNeeds(OutputView? console, string configuration, bool debugged, bool profiled = false)
    {
        ref NeedHearing? hearing = ref debugged ? ref debugNeeds : ref runNeeds;
        hearing?.LetGo();
        if (hearing is { Need: { } forgotten } before && before.Console.Offered == forgotten.Said)
        {
            before.Console.ShowOffer(null);
        }
        hearing = console is null ? null : new NeedHearing(console, configuration, debugged, profiled, need => parts.NeedsTerminal?.Invoke(need, console));
    }

    /// <summary>The program Debug last started with a tab's console stopped at an exception: one it leaves unhandled that
    /// says it needs a terminal is that need, offered at the stop, before the runtime writes it to the console.</summary>
    private void HearThrown(ThrownStop stopped)
    {
        if (debugNeeds is { Need: null } hearing && hearing.Console == stopped.Tab.Console
            && stopped.Exception.BreakMode is DapBreakModes.Unhandled or DapBreakModes.UserUnhandled
            && stopped.Exception.Details is { } details && TerminalNeeds.Thrown(details) is { } call)
        {
            hearing.Heard(call);
        }
    }

    /// <summary>One console's program heard until it says it needs a terminal, or until let go.</summary>
    private sealed class NeedHearing
    {
        private readonly TerminalNeeds needs = new();
        private readonly string configuration;
        private readonly bool debugged;
        private readonly bool profiled;
        private readonly Action<TerminalNeed> heard;

        public NeedHearing(OutputView console, string configuration, bool debugged, bool profiled, Action<TerminalNeed> heard)
        {
            Console = console;
            this.configuration = configuration;
            this.debugged = debugged;
            this.profiled = profiled;
            this.heard = heard;
            console.Written += OnWritten;
        }

        /// <summary>The console its program writes to.</summary>
        public OutputView Console { get; }

        public TerminalNeed? Need { get; private set; }

        public void LetGo() => Console.Written -= OnWritten;

        /// <summary>The program said it needs a terminal for <paramref name="call"/>: nothing more is heard, and the window
        /// offers one.</summary>
        public void Heard(string call)
        {
            LetGo();
            Need = new TerminalNeed(configuration, call, debugged) { Profiled = profiled };
            heard(Need);
        }

        private void OnWritten(object? sender, ConsoleWritten written)
        {
            foreach (ConsoleRun run in written.Runs)
            {
                if (needs.Hear(written.Text.AsSpan(run.Start, run.Length), run.Stream == ConsoleStream.Error))
                {
                    Heard(needs.Need!);
                    return;
                }
            }
        }
    }
}
