using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Chat;

/// <summary>The files the agent changed, above the box to write in, as Cursor's review bar: one line, "› 2 Files" with
/// Undo, Keep and Review, opening on a click into a row a file, its icon, its name, its folder and what it added and
/// removed, with its own Undo and Keep under the pointer. A file's name opens it in the editor, its changes marked; Review
/// opens the next one. Shown while any file waits for review.</summary>
public sealed class ReviewBar : Border
{
    private readonly TextBlock summary = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly RowIcon chevron = new() { DataKey = IconKeys.ChevronRight, ForegroundKey = TokenKeys.TextMuted, Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel rows = new();
    private readonly ScrollViewer list;
    private IReadOnlyList<ReviewedFile> shown = [];
    private bool open;

    public ReviewBar()
    {
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(8, 4);
        Margin = new Thickness(12, 0, 12, 0);
        IsVisible = false;
        Button undoAll = new() { Classes = { StyleClasses.TextLink }, Content = "Undo", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Button keepAll = new() { Classes = { StyleClasses.TextLink }, Content = "Keep", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Button review = new() { Content = "Review", Margin = new Thickness(10, 0, 0, 0) };
        undoAll.Click += (_, _) => UndoRequested?.Invoke(this, shown);
        keepAll.Click += (_, _) => KeepRequested?.Invoke(this, shown);
        review.Click += (_, _) => ReviewRequested?.Invoke(this, EventArgs.Empty);
        ToolTip.SetTip(undoAll, "Undo all: put every file listed back as it was before the agent changed it");
        ToolTip.SetTip(keepAll, "Keep all: keep every change listed");
        ToolTip.SetTip(review, "Open the next changed file in the editor, its changes marked");
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Children = { undoAll, keepAll, review } };
        DockPanel.SetDock(actions, Dock.Right);
        Button fold = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = new DockPanel { Children = { chevron, summary } },
            VerticalAlignment = VerticalAlignment.Center,
        };
        fold.Click += (_, _) => Open(!open);
        DockPanel heading = new() { Children = { actions, fold } };
        list = new ScrollViewer { MaxHeight = 180, Content = rows, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        Child = new StackPanel { Children = { heading, list } };
    }

    /// <summary>A file was asked to be opened in the editor, by its name.</summary>
    public event EventHandler<ReviewedFile>? DiffRequested;

    /// <summary>Review was pressed: the next file to open.</summary>
    public event EventHandler? ReviewRequested;

    /// <summary>These files are to be put back as they were.</summary>
    public event EventHandler<IReadOnlyList<ReviewedFile>>? UndoRequested;

    /// <summary>These files' changes are kept.</summary>
    public event EventHandler<IReadOnlyList<ReviewedFile>>? KeepRequested;

    /// <summary>The files it lists now.</summary>
    public IReadOnlyList<ReviewedFile> Shown => shown;

    /// <summary>Whether it lists its files, rather than its one line.</summary>
    public bool IsOpen => open;

    /// <summary>Lists <paramref name="files"/>, hidden when there are none; open or folded as it was.</summary>
    public void Show(IReadOnlyList<ReviewedFile> files)
    {
        shown = files;
        IsVisible = files.Count > 0;
        summary.Text = files.Count == 1 ? "1 File" : $"{files.Count} Files";
        rows.Children.Clear();
        foreach (ReviewedFile file in files)
        {
            rows.Children.Add(Row(file));
        }
    }

    /// <summary>Opens the list of files, or folds it to its one line.</summary>
    public void Open(bool listed)
    {
        open = listed;
        list.IsVisible = listed;
        chevron.DataKey = listed ? IconKeys.ChevronDown : IconKeys.ChevronRight;
    }

    private DockPanel Row(ReviewedFile file)
    {
        string name = Path.GetFileName(file.Path);
        DockPanel row = new() { Margin = new Thickness(0, 2, 0, 0), Background = Brushes.Transparent };
        // Its own Undo and Keep show under the pointer, as Cursor's do, so the list reads as names and counts.
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            IsVisible = false,
            Children =
            {
                ToolButtons.Of(IconKeys.Rollback, $"Undo the agent's {(file.Edits == 1 ? "change" : $"{file.Edits} changes")} to {name}", () => UndoRequested?.Invoke(this, [file])),
                ToolButtons.Of(IconKeys.Check, $"Keep the changes to {name}", () => KeepRequested?.Invoke(this, [file])),
            },
        };
        row.PointerEntered += (_, _) => buttons.IsVisible = true;
        row.PointerExited += (_, _) => buttons.IsVisible = false;
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        RowIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(file.Path),
            Foreground = Explorer.FileIcons.BrushForFile(file.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        Button named = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = file.IsNew ? $"{name} (new)" : file.Deleted ? $"{name} (deleted)" : name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        named.Click += (_, _) => DiffRequested?.Invoke(this, file);
        ToolTip.SetTip(named, $"Open {file.Path} with the agent's changes marked");
        DockPanel.SetDock(named, Dock.Left);
        row.Children.Add(named);
        StackPanel counts = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        counts.Children.Add(Counted($"+{file.Added}", TokenKeys.Vcs_Added));
        if (file.Removed > 0)
        {
            counts.Children.Add(Counted($"−{file.Removed}", TokenKeys.Vcs_Deleted));
        }
        DockPanel.SetDock(counts, Dock.Left);
        row.Children.Add(counts);
        string? folder = Path.GetDirectoryName(file.Path);
        row.Children.Add(new TextBlock
        {
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            Text = folder is null ? string.Empty : Path.GetFileName(folder),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return row;
    }

    private static TextBlock Counted(string text, string colour)
    {
        TextBlock counted = new() { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
    }

    /// <summary>Framed as Shell.axaml frames a Border, which styles a Border's subclass only through its key.</summary>
    protected override Type StyleKeyOverride => typeof(Border);
}
