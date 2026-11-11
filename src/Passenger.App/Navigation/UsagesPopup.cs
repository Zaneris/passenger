using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Languages.CSharp;

namespace Passenger.App.Navigation;

/// <summary>Show Usages: a symbol's usages under the caret, its declaration first and those in generated code last, each
/// its file and line with its line of code (the usage in bold) and what it does there, narrowed as a file's name or code
/// is typed, with what the search went without above them; Up and Down move, Enter or a double-click goes to one, Escape
/// leaves, and Open in Find Window (or Find Usages' keys) lists them in the Find window instead.</summary>
public sealed class UsagesPopup : SearchPopup<Usage>
{
    private readonly IReadOnlyList<Usage> all;
    private readonly string? note;

    public UsagesPopup(string title, IReadOnlyList<Usage> usages, string? note)
        : base($"{title}  {Core.Plural.Of(usages.Count, "usage")}{(usages.Count(u => u.Generated) is > 0 and int made ? $", {made} in generated code" : string.Empty)}",
            "A file's name or code", PopupFrame.Wide)
    {
        all = [.. usages.OrderBy(u => u.Generated).ThenBy(u => u.Kind == UsageKind.Declaration ? 0 : 1).ThenBy(u => u.Path, StringComparer.Ordinal).ThenBy(u => u.Line).ThenBy(u => u.Column)];
        this.note = note;
        FindWindow = new Button { Classes = { StyleClasses.Link }, Content = "Open in Find Window", Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        FindWindow.Click += (_, _) => FindWindowRequested?.Invoke(this, EventArgs.Empty);
        AddToHeading(FindWindow);
        QueryChanged();
    }

    /// <summary>The link that lists the usages in the Find window.</summary>
    public Button FindWindow { get; }

    /// <summary>The usages asked for in the Find window, by the link or Find Usages' keys.</summary>
    public event EventHandler? FindWindowRequested;

    protected override void QueryChanged()
    {
        string typed = Query.Trim();
        Show([.. all.Where(u => typed.Length == 0 || Path.GetFileName(u.Path).Contains(typed, StringComparison.OrdinalIgnoreCase)
            || u.LineText.Contains(typed, StringComparison.OrdinalIgnoreCase))], "No usages match");
        if (note is not null && Rows.Count > 0)
        {
            Say(note);
        }
    }

    protected override bool TakesKey(KeyEventArgs e)
    {
        if (!AppActions.Presses(AppActions.FindUsages, e))
        {
            return false;
        }
        FindWindowRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>A usage's row: its file's icon, name and line, its line of code with the usage in bold, and what it does
    /// there at the right, muted.</summary>
    protected override Control Row()
    {
        RowIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock place = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        TextBlock code = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont();
        TextBlock kind = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        kind.Classes.Add(StyleClasses.Muted);
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(place, Dock.Left);
        DockPanel.SetDock(kind, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(place);
        row.Children.Add(kind);
        row.Children.Add(code);
        return row.Showing<Usage>(usage =>
        {
            icon.Data = Explorer.FileIcons.ForFile(usage.Path);
            icon.Foreground = Explorer.FileIcons.BrushForFile(usage.Path);
            place.Text = $"{Path.GetFileName(usage.Path)}:{usage.Line + 1}";
            kind.Text = !usage.Generated ? KindOf(usage.Kind) : KindOf(usage.Kind) is { Length: > 0 } does ? $"{does} in generated code" : "In generated code";
            ToolTip.SetTip(row, usage.Path);
            code.Inlines = Code(usage);
        });
    }

    /// <summary>A usage's line without its indent, the usage itself in bold.</summary>
    private static InlineCollection Code(Usage usage)
    {
        string line = usage.LineText.TrimEnd();
        int indent = line.Length - line.TrimStart().Length;
        int start = Math.Clamp(usage.Column, indent, line.Length);
        int end = Math.Clamp(usage.Column + usage.Length, start, line.Length);
        InlineCollection inlines = [new Avalonia.Controls.Documents.Run(line[indent..start])];
        if (end > start)
        {
            inlines.Add(new Avalonia.Controls.Documents.Run(line[start..end]) { FontWeight = FontWeight.Bold });
        }
        inlines.Add(new Avalonia.Controls.Documents.Run(line[end..]));
        return inlines;
    }

    /// <summary>What a usage does where it is, in the Find window's words for its groups.</summary>
    private static string KindOf(UsageKind kind) => kind switch
    {
        UsageKind.Declaration => "Declaration",
        UsageKind.Write => "Write",
        UsageKind.Read => "Read",
        UsageKind.Invocation => "Invocation",
        UsageKind.Import => "Import",
        _ => string.Empty,
    };
}
