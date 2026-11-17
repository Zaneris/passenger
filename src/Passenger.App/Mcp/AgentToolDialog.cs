using Avalonia.Controls;

namespace Passenger.App.Mcp;

/// <summary>The question a person's window asks before an agent's call of a tool that changes something goes on: what the
/// tool would do and what the call asked with, Allow Once for that call alone, Allow for This Session for every call of the
/// tool until the agent goes or the window leaves the folder, or Deny. Allow Once wears the accent, the safer answer, but it
/// comes on its own, so no key answers either Allow: Enter is no answer, and Escape and closing answer Deny.</summary>
public sealed class AgentToolDialog : IdeWindow
{
    public const string AllowOnce = "Allow Once";
    public const string AllowForSession = "Allow for This Session";
    public const string Deny = "Deny";

    /// <param name="ownWindows">Whether the call gives input to Passenger's own windows (an app_ tool's app ide), which the
    /// question names, as there the agent's input does what the person's would.</param>
    public AgentToolDialog(string agent, string tool, string what, bool ownWindows, Control? arguments)
    {
        Button once = new() { Content = AllowOnce, Classes = { StyleClasses.Primary } };
        Button session = new() { Content = AllowForSession };
        Button deny = new() { Content = Deny, IsCancel = true };
        once.Click += (_, _) => Answer(Admission.AllowedOnce);
        session.Click += (_, _) => Answer(Admission.Allowed);
        deny.Click += (_, _) => Answer(Admission.NotAllowed);

        string asks = ownWindows
            ? $"{agent}, connected through Passenger's MCP server, wants to {what} (its {tool} tool), the app being Passenger's own windows, where its input does what yours would."
            : $"{agent}, connected through Passenger's MCP server, wants to {what} (its {tool} tool).";
        string uses = ownWindows ? $"{tool} in Passenger's windows" : tool;
        DialogFooter.Question(this, ownWindows ? $"Allow {tool} in Passenger's windows?" : $"Allow {tool}?",
        [
            DialogFooter.Paragraph(asks),
            DialogFooter.Paragraph($"Allow Once lets this call alone go on, asking again at the agent's next. Allow for This Session lets it use {uses} without asking in this folder until it disconnects."),
            .. arguments is null ? [] : new[] { arguments },
        ], DialogFooter.Answers(once, session, deny));
    }

    /// <summary>The person's answer, once they gave one: what the window, which shows the question without waiting on it,
    /// reads as it closes.</summary>
    public Admission? Answered { get; private set; }

    private void Answer(Admission said)
    {
        Answered = said;
        Close(said);
    }
}
