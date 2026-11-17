using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Chat;

/// <summary>Steps of the agent's reply folded under one muted line, as Cursor folds them: the reads and searches it ran
/// one after another ("Explored hello.py, 1 search"), or a finished turn's steps ("Worked for 9s"). A click on the line
/// opens or folds what it holds.</summary>
public sealed class ChatFold : StackPanel
{
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly RowIcon chevron = new() { DataKey = IconKeys.ChevronRight, ForegroundKey = TokenKeys.TextMuted, Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ChatFold(bool explores)
    {
        Explores = explores;
        Spacing = 4;
        DockPanel.SetDock(chevron, Dock.Right);
        Button line = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = new DockPanel { Children = { chevron, said } },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        line.Click += (_, _) => Open(!IsOpen);
        Body = new StackPanel { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 2, 0, 0) };
        Children.Add(line);
        Children.Add(Body);
    }

    /// <summary>Whether it groups reads and searches, rather than a finished turn's steps.</summary>
    public bool Explores { get; }

    /// <summary>What it holds, hidden while it is folded.</summary>
    public StackPanel Body { get; }

    /// <summary>What its line says.</summary>
    public string Said
    {
        get => said.Text ?? string.Empty;
        set => said.Text = value;
    }

    /// <summary>Whether it shows what it holds.</summary>
    public bool IsOpen => Body.IsVisible;

    /// <summary>Shows what it holds, or folds it under its line.</summary>
    public void Open(bool shown)
    {
        Body.IsVisible = shown;
        chevron.DataKey = shown ? IconKeys.ChevronDown : IconKeys.ChevronRight;
    }
}
