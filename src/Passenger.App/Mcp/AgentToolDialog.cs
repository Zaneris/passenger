using Avalonia.Controls;

namespace Passenger.App.Mcp;

/// <summary>The question a person's window asks before an agent's call of a tool that changes something goes on: what the
/// tool would do and what the call asked with, Allow Once for that call alone, Allow for This Session for every call of the
/// tool until the agent goes or the window leaves the folder, or Deny; or, at the agent's request, Allow for This Session
/// or Deny for every such tool at once. Allow Once, the safer answer, wears the accent where it is offered, Allow for This
/// Session where it is the one Allow, but the question comes on its own, so no key answers either Allow: Enter is no
/// answer, and Escape and closing answer Deny.</summary>
public sealed class AgentToolDialog : IdeWindow
{
    public const string AllowOnce = "Allow Once";
    public const string AllowForSession = "Allow for This Session";
    public const string Deny = "Deny";

    /// <summary>The question's title for every tool at once.</summary>
    public const string AllowEveryTool = "Allow every tool?";

    /// <param name="ownWindows">Whether the call gives input to Passenger's own windows (an app_ tool's app ide), which the
    /// question names, as there the agent's input does what the person's would.</param>
    public AgentToolDialog(string agent, string tool, string what, bool ownWindows, Control? arguments)
    {
        Button once = new() { Content = AllowOnce, Classes = { StyleClasses.Primary } };
        Button session = new() { Content = AllowForSession };
        once.Click += (_, _) => Answer(Admission.AllowedOnce);
        session.Click += (_, _) => Answer(Admission.Allowed);

        string asks = ownWindows
            ? $"{agent}, connected through Passenger's MCP server, wants to {what} (its {tool} tool), the app being Passenger's own windows, where its input does what yours would."
            : $"{agent}, connected through Passenger's MCP server, wants to {what} (its {tool} tool).";
        string uses = ownWindows ? $"{tool} in Passenger's windows" : tool;
        DialogFooter.Question(this, ownWindows ? $"Allow {tool} in Passenger's windows?" : $"Allow {tool}?",
        [
            DialogFooter.Paragraph(asks),
            DialogFooter.Paragraph($"Allow Once lets this call alone go on, asking again at the agent's next. Allow for This Session lets it use {uses} without asking in this folder until it disconnects."),
            .. arguments is null ? [] : new[] { arguments },
        ], DialogFooter.Answers(once, session, Denial()));
    }

    /// <summary>The question for every tool that changes something at once (the agent's request_full_access), naming what
    /// they do in the person's words over <paramref name="tools"/>, the list of them.</summary>
    public AgentToolDialog(string agent, Control tools)
    {
        Button session = new() { Content = AllowForSession, Classes = { StyleClasses.Primary } };
        session.Click += (_, _) => Answer(Admission.Allowed);
        DialogFooter.Question(this, AllowEveryTool,
        [
            DialogFooter.Paragraph($"{agent}, connected through Passenger's MCP server, asks to use the tools below without your leave at each call: to "
                + "edit and refactor files, build, run, debug and evaluate code, commit, run commands, and click or type in the apps it runs and "
                + "in Passenger's own windows, where its input does what yours would."),
            DialogFooter.Paragraph("Allow for This Session lets it use them all in this folder until it disconnects. Deny keeps each asking at "
                + "its call, and refuses the agent's later requests for every tool until then."),
            tools,
        ], DialogFooter.Answers(session, Denial()));
    }

    /// <summary>The person's answer, once they gave one: what the window, which shows the question without waiting on it,
    /// reads as it closes.</summary>
    public Admission? Answered { get; private set; }

    private Button Denial()
    {
        Button deny = new() { Content = Deny, IsCancel = true };
        deny.Click += (_, _) => Answer(Admission.NotAllowed);
        return deny;
    }

    private void Answer(Admission said)
    {
        Answered = said;
        Close(said);
    }
}
