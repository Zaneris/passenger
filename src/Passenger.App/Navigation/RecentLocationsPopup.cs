using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.CSharp;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place Recent Locations shows: where it is, its file's name, the type and member it is in when known, and its
/// line with those either side as the file has them (the first of them <see cref="FirstLine"/>, from zero), coloured as
/// the file is.</summary>
public sealed record RecentLocation(HistoryPlace Place, string Name, string? Member, int FirstLine, string Lines, IHighlighter? Colours)
{
    /// <summary>Whether a query typed into the popup finds it: in its file's name, what it is in, or its code.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Member?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
        || Lines.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Recent Locations: the places left and edited, the latest first, each with its file, what it is in and its
/// lines of code. Show edited only (or the action's keys again) keeps the places edited. Typing narrows them by file,
/// member or code; Up and Down move, Enter or a double-click goes, Delete forgets a place, Escape leaves.</summary>
public sealed class RecentLocationsPopup : SearchPopup<RecentLocation>
{
    /// <summary>How tall the list grows: its rows are a place's lines of code.</summary>
    private const double PlacesHeight = 560;

    private readonly Func<bool, IReadOnlyList<RecentLocation>> places;
    private IReadOnlyList<RecentLocation> all = [];

    public RecentLocationsPopup(Func<bool, IReadOnlyList<RecentLocation>> places, bool editedOnly)
        : base(string.Empty, "A file's name, a member or code", PopupFrame.Wide, option: ("Show edited only", editedOnly), listHeight: PlacesHeight)
    {
        this.places = places;
        Reload();
    }

    /// <summary>A place forgotten with Delete, which the history lets go of too.</summary>
    public event EventHandler<HistoryPlace>? Forgot;

    public bool EditedOnly
    {
        get => IsOptionChecked;
        set => IsOptionChecked = value;
    }

    protected override void QueryChanged() => Filter();

    protected override void OptionTurned() => Reload();

    protected override bool TakesKey(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && Query.Length == 0 && Selected is { } chosen)
        {
            Forget(chosen);
            return true;
        }
        return false;
    }

    /// <summary>The places asked for again, as the switch between all of them and those edited turns.</summary>
    private void Reload()
    {
        Title = EditedOnly ? "Recently Edited Locations" : "Recent Locations";
        all = places(EditedOnly);
        Filter();
    }

    private void Filter()
    {
        string typed = Query.Trim();
        Show([.. all.Where(p => p.Matches(typed))], "No recent locations found");
    }

    private void Forget(RecentLocation chosen)
    {
        int at = Rows.ToList().IndexOf(chosen);
        all = [.. all.Where(p => p != chosen)];
        Forgot?.Invoke(this, chosen.Place);
        Filter();
        Selected = Rows.Count == 0 ? null : Rows[Math.Min(at, Rows.Count - 1)];
    }

    /// <summary>A place's row: its file and what it is in over its lines of code, numbered as the file numbers them,
    /// the place's own line marked.</summary>
    protected override Control Row()
    {
        RowIcon icon = new()
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock name = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        TextBlock member = new()
        {
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        member.Classes.Add(StyleClasses.Muted);
        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Height = ListRow.Height,
            Children = { icon, name, member },
        };
        CodeEditor code = new()
        {
            IsReadOnly = true,
            ShowErrorStripe = false,
            ShowLineNumbers = false,
            ShowScrollBars = false,
            Focusable = false,
            // The row takes the clicks, so a click chooses the place and a double-click goes there.
            IsHitTestVisible = false,
        };
        TextBlock numbers = new()
        {
            FontFamily = code.FontFamily,
            FontSize = code.FontSize,
            LineHeight = code.LineHeight,
            TextAlignment = TextAlignment.Right,
            MinWidth = 28,
            Margin = new Thickness(8, 0, 10, 0),
        };
        numbers.Bind(TextBlock.ForegroundProperty, numbers.GetResourceObservable(TokenKeys.Editor_LineNumber));
        DockPanel fragment = new();
        DockPanel.SetDock(numbers, Dock.Left);
        fragment.Children.Add(numbers);
        fragment.Children.Add(code);
        Border frame = new() { Child = fragment, Classes = { StyleClasses.Box }, ClipToBounds = true, Padding = new Thickness(0, 4) };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable(TokenKeys.Editor_Background));
        // A row the list let go of lets go of the colours it was shown with, whose changes would otherwise still reach it.
        code.DetachedFromVisualTree += (_, _) => code.Highlighter = null;
        StackPanel row = new() { Margin = new Thickness(0, 2, 0, 6), Children = { header, frame } };
        return row.Showing<RecentLocation>(place =>
        {
            icon.Data = Explorer.FileIcons.ForFile(place.Place.Path);
            icon.Foreground = Explorer.FileIcons.BrushForFile(place.Place.Path);
            name.Text = place.Name;
            member.Text = place.Member ?? string.Empty;
            ToolTip.SetTip(header, place.Place.Path);
            TextDocument lines = new(place.Lines);
            int count = lines.Snapshot.LineCount;
            int own = Math.Clamp(place.Place.Line - place.FirstLine, 0, count - 1);
            code.Show(lines, SelectionSet.Single(lines.Snapshot.GetLineStart(own)), default);
            code.Highlighter = place.Colours;
            code.Height = count * code.LineHeight;
            numbers.Text = string.Join('\n', Enumerable.Range(place.FirstLine + 1, count));
        });
    }
}
