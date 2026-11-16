using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Chat;

/// <summary>The files the agent changed, above the box to write in, as Cursor's and Stoat's review bar: "2 files
/// +157 −2" with Undo all and Keep all, then a row a file with its name, its folder, what it added and removed, and buttons
/// to see its diff, undo it and keep it. Shown while any file waits for review.</summary>
public sealed class ReviewBar : Border
{
    private readonly TextBlock summary = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock added = Counted(TokenKeys.Vcs_Added);
    private readonly TextBlock removed = Counted(TokenKeys.Vcs_Deleted);
    private readonly StackPanel rows = new();
    private IReadOnlyList<ReviewedFile> shown = [];

    public ReviewBar()
    {
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(8, 6);
        Margin = new Thickness(12, 0, 12, 0);
        IsVisible = false;
        Button undoAll = new() { Content = "Undo all", Margin = new Thickness(6, 0, 0, 0) };
        Button keepAll = new() { Content = "Keep all", Classes = { StyleClasses.Primary }, Margin = new Thickness(6, 0, 0, 0) };
        undoAll.Click += (_, _) => UndoRequested?.Invoke(this, shown);
        keepAll.Click += (_, _) => KeepRequested?.Invoke(this, shown);
        ToolTip.SetTip(undoAll, "Put every file listed back as it was before the agent changed it");
        ToolTip.SetTip(keepAll, "Keep every change listed");
        DockPanel heading = new();
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Children = { undoAll, keepAll } };
        DockPanel.SetDock(actions, Dock.Right);
        heading.Children.Add(actions);
        // Docked so a narrow window cuts the summary short rather than drawing it under the buttons.
        StackPanel total = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), Children = { added, removed } };
        DockPanel.SetDock(total, Dock.Right);
        heading.Children.Add(total);
        heading.Children.Add(summary);
        Child = new StackPanel { Spacing = 4, Children = { heading, new ScrollViewer { MaxHeight = 180, Content = rows } } };
    }

    /// <summary>A file's diff was asked for.</summary>
    public event EventHandler<ReviewedFile>? DiffRequested;

    /// <summary>These files are to be put back as they were.</summary>
    public event EventHandler<IReadOnlyList<ReviewedFile>>? UndoRequested;

    /// <summary>These files' changes are kept.</summary>
    public event EventHandler<IReadOnlyList<ReviewedFile>>? KeepRequested;

    /// <summary>The files it lists now.</summary>
    public IReadOnlyList<ReviewedFile> Shown => shown;

    /// <summary>Lists <paramref name="files"/>, hidden when there are none.</summary>
    public void Show(IReadOnlyList<ReviewedFile> files)
    {
        shown = files;
        IsVisible = files.Count > 0;
        summary.Text = files.Count == 1 ? "1 file" : $"{files.Count} files";
        ToolTip.SetTip(summary, $"{summary.Text} changed by the agent, to keep or undo");
        added.Text = $"+{files.Sum(f => f.Added)}";
        removed.Text = $"−{files.Sum(f => f.Removed)}";
        rows.Children.Clear();
        foreach (ReviewedFile file in files)
        {
            rows.Children.Add(Row(file));
        }
    }

    private DockPanel Row(ReviewedFile file)
    {
        string name = Path.GetFileName(file.Path);
        DockPanel row = new() { Margin = new Thickness(0, 2, 0, 0) };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                ToolButtons.Of(IconKeys.Diff, $"Show what the agent changed in {name}", () => DiffRequested?.Invoke(this, file)),
                ToolButtons.Of(IconKeys.Rollback, $"Undo the agent's {(file.Edits == 1 ? "change" : $"{file.Edits} changes")} to {name}", () => UndoRequested?.Invoke(this, [file])),
                ToolButtons.Of(IconKeys.Check, $"Keep the changes to {name}", () => KeepRequested?.Invoke(this, [file])),
            },
        };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        TextBlock plus = Counted(TokenKeys.Vcs_Added);
        plus.Text = $"+{file.Added}";
        TextBlock minus = Counted(TokenKeys.Vcs_Deleted);
        minus.Text = $"−{file.Removed}";
        StackPanel counts = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0), Children = { plus, minus } };
        DockPanel.SetDock(counts, Dock.Right);
        row.Children.Add(counts);
        string? folder = Path.GetDirectoryName(file.Path);
        TextBlock named = new()
        {
            Text = file.IsNew ? $"{name} (new)" : file.Deleted ? $"{name} (deleted)" : name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        DockPanel.SetDock(named, Dock.Left);
        TextBlock where = new()
        {
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            Text = folder is null ? string.Empty : Path.GetFileName(folder),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(new DockPanel { Children = { named, where } });
        ToolTip.SetTip(row, file.Path);
        return row;
    }

    private static TextBlock Counted(string colour)
    {
        TextBlock counted = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
    }

    /// <summary>Framed as Shell.axaml frames a Border, which styles a Border's subclass only through its key.</summary>
    protected override Type StyleKeyOverride => typeof(Border);
}
