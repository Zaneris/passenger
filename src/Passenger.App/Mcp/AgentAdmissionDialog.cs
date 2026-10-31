using Avalonia.Controls;

namespace Passenger.App.Mcp;

/// <summary>The question a person's window asks when an agent's call reaches it while Enable MCP Server is off: "NAME
/// wants to work in this window", Allow or Don't allow, and Always for this folder at the footer's left, which puts the
/// folder among Settings' folders agents join unasked (never a no). It comes on its own, so no key answers Allow: Enter is
/// no answer, and Escape and closing answer no.</summary>
public sealed class AgentAdmissionDialog : IdeWindow
{
    public const string Allow = "Allow";
    public const string DontAllow = "Don't allow";

    private readonly CheckBox always = new() { Content = "Always for this folder" };

    public AgentAdmissionDialog(string agent, string? folder)
    {
        Button allow = new() { Content = Allow, Classes = { StyleClasses.Primary } };
        Button dontAllow = new() { Content = DontAllow, IsCancel = true };
        allow.Click += (_, _) => Answer(Admission.Allowed);
        dontAllow.Click += (_, _) => Answer(Admission.NotAllowed);

        string where = folder is null ? "this window" : Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        DialogFooter.Question(this, $"{agent} wants to work in this window",
        [
            DialogFooter.Paragraph($"It asked through Passenger's MCP server. Allowed, it can read and search {where}, see the window, save files, stop "
                + "what runs and step through what is debugged; Passenger asks you before it first makes each other kind of change, such "
                + "as an edit, a build, a run, a breakpoint or code run in the debugged program. Enable MCP Server in Settings lets agents in "
                + "without asking, and Settings' MCP Server page lists the folders allowed always, each to take back."),
        ], DialogFooter.Answers(allow, dontAllow), always);
    }

    /// <summary>Whether Always for this folder is ticked, which keeps an Allow.</summary>
    public bool Always => always.IsChecked == true;

    /// <summary>The person's answer, once they gave one: what the window, which shows the question without waiting on it,
    /// reads as it closes.</summary>
    public Admission? Answered { get; private set; }

    private void Answer(Admission said)
    {
        Answered = said;
        Close(said);
    }
}
