using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Vcs;

/// <summary>One thing a <see cref="ChoicePopup"/> offers: what it is called, what it says beside that, and what it is.</summary>
public sealed record Choice(string Label, string Detail, object Value);

/// <summary>The popups that choose a version to compare with (Compare with Branch or Tag, Compare with Revision) or a
/// change set (Recent Changes): a search box over the list, narrowed as words are typed; Enter or a double-click takes the
/// chosen one, Escape gives up.</summary>
public sealed class ChoicePopup : SearchPopup<Choice>
{
    private readonly IReadOnlyList<Choice> choices;
    private readonly string nothing;

    public ChoicePopup(string title, string placeholder, IReadOnlyList<Choice> choices, string nothing)
        : base(title, placeholder, PopupFrame.Medium)
    {
        this.choices = choices;
        this.nothing = nothing;
        QueryChanged();
    }

    protected override void QueryChanged()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Show([.. choices.Where(c => words.All(w => c.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(w, StringComparison.OrdinalIgnoreCase)))], nothing);
    }

    /// <summary>A choice's row: its label, and what it says beside that at the right, muted.</summary>
    protected override Control Row()
    {
        TextBlock detail = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        detail.Classes.Add(StyleClasses.Muted);
        TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(detail, Dock.Right);
        row.Children.Add(detail);
        row.Children.Add(label);
        return row.Showing<Choice>(choice =>
        {
            detail.Text = choice.Detail;
            label.Text = choice.Label;
        });
    }
}
