using Avalonia.Controls;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Mcp;

/// <summary>The question a person's window asks before an agent's call of a tool that changes something goes on: what the
/// tool would do and what the call asked with, Allow Once for that call alone, Allow for This Session for every call of the
/// tool until the agent goes or the window leaves the folder, or Deny; or, at the agent's request, Allow for This Session
/// or Deny for every such tool at once. Allow Once, the safer answer, wears the accent where it is offered, Allow for This
/// Session where it is the one Allow, but the question comes on its own, so no key answers either Allow: Enter is no
/// answer, and Escape and closing answer Deny.</summary>
public sealed class AgentToolDialog : IdeWindow
{
    /// <param name="ownWindows">Whether the call gives input to Passenger's own windows (an app_ tool's app ide), which the
    /// question names, as there the agent's input does what the person's would.</param>
    public AgentToolDialog(string agent, string tool, string what, bool ownWindows, Control? arguments)
    {
        Button once = new() { [ShownWords.ContentProperty] = McpWords.AllowOnce, Classes = { StyleClasses.Primary } };
        Button session = new() { [ShownWords.ContentProperty] = McpWords.AllowForSession };
        once.Click += (_, _) => Answer(Admission.AllowedOnce);
        session.Click += (_, _) => Answer(Admission.Allowed);

        Words agentName = Words.Verbatim(agent);
        Words toolName = Words.Verbatim(tool);
        DialogFooter.Question(this, ownWindows ? McpWords.AllowToolInOwnWindows(toolName) : McpWords.AllowTool(toolName),
        [
            DialogFooter.Paragraph(ownWindows ? McpWords.WantsToInOwnWindows(agentName, Words.Verbatim(what), toolName) : McpWords.WantsTo(agentName, Words.Verbatim(what), toolName)),
            DialogFooter.Paragraph(ownWindows ? McpWords.AllowOnceSaysInOwnWindows(toolName) : McpWords.AllowOnceSays(toolName)),
            .. arguments is null ? [] : new[] { arguments },
        ], DialogFooter.Answers(once, session, Denial()));
    }

    /// <summary>The question for every tool that changes something at once (the agent's request_full_access), naming what
    /// they do in the person's words over <paramref name="tools"/>, the list of them.</summary>
    public AgentToolDialog(string agent, Control tools)
    {
        Button session = new() { [ShownWords.ContentProperty] = McpWords.AllowForSession, Classes = { StyleClasses.Primary } };
        session.Click += (_, _) => Answer(Admission.Allowed);
        DialogFooter.Question(this, McpWords.AllowEveryTool,
        [
            DialogFooter.Paragraph(McpWords.AsksEveryTool(Words.Verbatim(agent))),
            DialogFooter.Paragraph(McpWords.EveryToolSays),
            tools,
        ], DialogFooter.Answers(session, Denial()));
    }

    /// <summary>The person's answer, once they gave one: what the window, which shows the question without waiting on it,
    /// reads as it closes.</summary>
    public Admission? Answered { get; private set; }

    private Button Denial()
    {
        Button deny = new() { [ShownWords.ContentProperty] = McpWords.Deny, IsCancel = true };
        deny.Click += (_, _) => Answer(Admission.NotAllowed);
        return deny;
    }

    private void Answer(Admission said)
    {
        Answered = said;
        Close(said);
    }
}
