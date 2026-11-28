using Avalonia.Controls;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Mcp;

/// <summary>The question a person's window asks when an agent's call reaches it while Enable MCP Server is off: "NAME
/// wants to work in this window", Allow or Don't allow, and Always for this folder at the footer's left, which puts the
/// folder among Settings' folders agents join unasked (never a no). It comes on its own, so no key answers Allow: Enter is
/// no answer, and Escape and closing answer no.</summary>
public sealed class AgentAdmissionDialog : IdeWindow
{
    private readonly CheckBox always = new() { [ShownWords.ContentProperty] = McpWords.AlwaysHere };

    public AgentAdmissionDialog(string agent, string? folder)
    {
        Button allow = new() { [ShownWords.ContentProperty] = McpWords.Allow, Classes = { StyleClasses.Primary } };
        Button dontAllow = new() { [ShownWords.ContentProperty] = McpWords.DontAllow, IsCancel = true };
        allow.Click += (_, _) => Answer(Admission.Allowed);
        dontAllow.Click += (_, _) => Answer(Admission.NotAllowed);

        Words where = folder is null ? McpWords.ThisWindow : Words.Verbatim(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        DialogFooter.Question(this, McpWords.WantsWindow(Words.Verbatim(agent)),
        [
            DialogFooter.Paragraph(McpWords.AdmissionSays(where)),
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
