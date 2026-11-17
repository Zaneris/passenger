using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>One tool call of the agent's as a line in its reply, updated in place as the call goes: a ring turning while
/// it is pending or running, then a check, or a cross where it failed, beside its title ("Read hello.py"). An edit's line
/// is Cursor's file pill once done: its title opens the file in the editor, its changes marked, with what it added and removed.</summary>
public sealed class ToolLine : DockPanel
{
    private readonly Panel mark = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button link = new() { Classes = { StyleClasses.TextLink }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock added = Coloured(TokenKeys.Vcs_Added);
    private readonly TextBlock removed = Coloured(TokenKeys.Vcs_Deleted);
    private string status = string.Empty;

    public ToolLine(ToolCall call)
    {
        Id = call.Id;
        SetDock(mark, Dock.Left);
        Children.Add(mark);
        StackPanel counts = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), Children = { added, removed } };
        SetDock(counts, Dock.Right);
        Children.Add(counts);
        Children.Add(new Panel { Children = { title, link } });
        link.Click += (_, _) => DiffRequested?.Invoke(this, EventArgs.Empty);
        Show(call);
    }

    /// <summary>The edit's file was asked for, by a click on its title.</summary>
    public event EventHandler? DiffRequested;

    /// <summary>Its call's id.</summary>
    public string Id { get; }

    /// <summary>Its title now.</summary>
    public string Title => title.Text ?? string.Empty;

    /// <summary>Its call's status now: pending, in_progress, completed or failed.</summary>
    public string Status => status;

    /// <summary>What the edit added and removed, once counted: "+115 −0".</summary>
    public string? Counts => added.IsVisible ? $"{added.Text} {removed.Text}" : null;

    /// <summary>The call as it stands now.</summary>
    public void Show(ToolCall call)
    {
        title.Text = call.Title.Length > 0 ? call.Title : call.Kind;
        link.Content = title.Text;
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

    /// <summary>Shows what the edit added and removed, its title then opening its file.</summary>
    public void Counted(int add, int remove)
    {
        added.Text = $"+{add}";
        removed.Text = $"−{remove}";
        added.IsVisible = removed.IsVisible = true;
        link.IsVisible = true;
        title.IsVisible = false;
    }

    private static TextBlock Coloured(string colour)
    {
        TextBlock counted = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
    }
}
