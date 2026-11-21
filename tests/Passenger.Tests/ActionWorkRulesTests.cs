using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Kind = Passenger.Tests.ActionFlows.Kind;

namespace Passenger.Tests;

/// <summary>An agent's ide_action waits for the works an action begins as it runs and answers what they came to: so an
/// action holds its work from its first moment, and tells what it came to.</summary>
public sealed class ActionWorkRulesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("action-work-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Every action of the window's registry, read from its run's built IL (ActionFlows): one that begins a Working,
    /// opens a dialog or a popup, or tells what it came to only after an await begins a Working before its first await, or
    /// ide_action would answer before any of it; and one that begins a Working tells what it came to, or ide_action could
    /// answer only that it ran. A run made of another delegate (a helper's parameter) is read with it; one refused to an
    /// agent but not to the person is the person's alone.</summary>
    [AvaloniaFact]
    public void Every_action_holds_its_work_from_its_first_moment_and_tells_what_it_came_to()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            ActionFlows flows = new();
            List<string> late = [];
            List<string> untold = [];
            ActionContext person = window.Actions.Context();
            ActionContext agent = person.ForAgent();
            // One refused to an agent and not to the person (Quit, Restart to Update) is the person's alone, never an agent's to hear.
            foreach (IdeAction action in window.Actions.All.Where(a => a.WhyNot(agent) is null || a.WhyNot(person) is not null))
            {
                (Dictionary<ActionFlows.Effect, string> atOnce, Dictionary<ActionFlows.Effect, string> later, HashSet<ActionFlows.Effect> going) = ([], [], []);
                foreach (MethodBase run in Runs(action.Run))
                {
                    ActionFlows.Flow flow = flows.Of(run);
                    foreach ((ActionFlows.Effect effect, string path) in flow.AtOnce)
                    {
                        atOnce.TryAdd(effect, path);
                    }
                    foreach ((ActionFlows.Effect effect, string path) in flow.Later)
                    {
                        later.TryAdd(effect, path);
                    }
                    going.UnionWith(flow.Going);
                }
                // One that asks the person at once goes on as they answer, the agent's ide_action refused or told so.
                if (!atOnce.Keys.Any(e => e.Kind is Kind.Begins or Kind.Dialog or Kind.Popup))
                {
                    foreach ((ActionFlows.Effect e, string path) in later.Where(e => e.Key.Kind is not Kind.Told))
                    {
                        late.Add($"{action.Id}: {Said(e)} after an await, holding no work from its first moment: {path} [at once: {string.Join(", ", atOnce.Select(e => $"{e.Key.Kind} {e.Value}"))}]");
                    }
                }
                // One that asks the person ends its work as it asks, as every action asking at once does.
                else if (atOnce.Keys.Any(e => e.Kind == Kind.Begins) && !atOnce.Keys.Concat(later.Keys).Concat(going).Any(e => e.Kind is Kind.Tells or Kind.Told or Kind.Dialog or Kind.Popup))
                {
                    untold.Add($"{action.Id}: begins its work in {atOnce.First(e => e.Key.Kind == Kind.Begins).Value} and never tells what it came to");
                }
            }
            Assert.True(late.Count + untold.Count == 0, "ide_action answers these actions before their work, or says only that they ran: begin the work "
                + "(ActionWork.Begin) before the first await, and tell what it came to (Working.Tell, ActionWork.Tell):\n"
                + string.Join('\n', late.Concat(untold).Order(StringComparer.Ordinal)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The methods an action's run calls as it runs: its own, and those of the delegates it holds (a helper's
    /// parameter it calls on with the context's row or path).</summary>
    private static IEnumerable<MethodBase> Runs(Delegate run)
    {
        yield return run.Method;
        if (run.Target is { } held && held.GetType().IsDefined(typeof(CompilerGeneratedAttribute)))
        {
            foreach (FieldInfo field in held.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.GetValue(held) is Delegate inner)
                {
                    yield return inner.Method;
                }
            }
        }
    }

    private static string Said(ActionFlows.Effect effect) => effect.Kind switch
    {
        Kind.Begins => $"begins its work in {effect.Where}",
        Kind.Dialog => $"opens a dialog in {effect.Where}",
        Kind.Popup => $"opens a popup in {effect.Where}",
        _ => $"tells what it came to in {effect.Where}",
    };
}
