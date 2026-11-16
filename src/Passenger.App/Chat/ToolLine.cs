using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>One tool call of the agent's as a line in its reply, updated in place as the call goes: a ring turning while
/// it is pending or running, then a check, or a cross where it failed, beside its title ("Read hello.py").</summary>
public sealed class ToolLine : DockPanel
{
    private readonly Panel mark = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private string status = string.Empty;

    public ToolLine(ToolCall call)
    {
        SetDock(mark, Dock.Left);
        Children.Add(mark);
        Children.Add(title);
        Show(call);
    }

    /// <summary>Its title now.</summary>
    public string Title => title.Text ?? string.Empty;

    /// <summary>Its call's status now: pending, in_progress, completed or failed.</summary>
    public string Status => status;

    /// <summary>The call as it stands now.</summary>
    public void Show(ToolCall call)
    {
        title.Text = call.Title.Length > 0 ? call.Title : call.Kind;
        ToolTip.SetTip(this, call.Locations.Count > 0 ? string.Join("\n", call.Locations) : null);
        if (call.Status == status)
        {
            return;
        }
        status = call.Status;
        mark.Children.Clear();
        mark.Children.Add(status switch
        {
            "completed" => new RowIcon { DataKey = IconKeys.Check, ForegroundKey = TokenKeys.Vcs_Added, Width = 14, Height = 14 },
            "failed" => new RowIcon { DataKey = IconKeys.TestFailed, ForegroundKey = TokenKeys.Error, Width = 14, Height = 14 },
            _ => new WorkingRing { Width = 12, Height = 12 },
        });
    }
}
