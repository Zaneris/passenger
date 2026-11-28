using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Run;

/// <summary>One thing Run Anything or a configuration chooser offers: a run configuration, a command line for a Terminal tab
/// of the open folder, or an action of the window's (Edit Configurations), with what it says beside its name and, for a
/// configuration that cannot launch here, why.</summary>
public sealed record RunOffer(Words Label, Words Detail, RunConfiguration? Configuration = null, string? Command = null,
    [IdentifierOf(typeof(AppActions))] string? Action = null, Words? Refused = null);

/// <summary>Run Anything and Choose and Run or Debug: a search box over what can be run, narrowed as words are typed; Enter
/// or a double-click takes the one chosen, Shift+Enter takes it the other way (<see cref="Otherwise"/>), Escape gives up.
/// Run Anything also offers what is typed as a command line of its own, after what it found. Enter and Shift+Enter are its
/// own (<see cref="IOwnKeys"/>), so no keymap runs them while it has the keys.</summary>
public sealed class RunOffersPopup : SearchPopup<RunOffer>, IOwnKeys
{
    private readonly IReadOnlyList<RunOffer> choices;
    private readonly bool commands;
    private readonly Words nothing;

    /// <param name="commands">Whether what is typed is offered as a command line for the Terminal.</param>
    public RunOffersPopup(Words title, Words placeholder, IReadOnlyList<RunOffer> choices, bool commands, Words nothing)
        : base(title, placeholder, PopupFrame.Medium)
    {
        this.choices = choices;
        this.commands = commands;
        this.nothing = nothing;
        QueryChanged();
    }

    /// <summary>Whether the choice was taken the other way: a configuration debugged where Enter runs it, or run where
    /// Enter debugs it.</summary>
    public bool Otherwise { get; private set; }

    /// <summary>What is typed, as the command line it offers: the query without the spaces around it.</summary>
    private string Typed => Query.Trim();

    protected override void QueryChanged()
    {
        string[] words = Typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<RunOffer> found = [.. choices.Where(c => words.All(w => c.Label.Shown.Contains(w, StringComparison.OrdinalIgnoreCase)
            || c.Detail.Shown.Contains(w, StringComparison.OrdinalIgnoreCase)))];
        if (commands && Typed.Length > 0 && !found.Any(c => c.Command == Typed))
        {
            found.Add(new RunOffer(Words.Verbatim(Typed), RunWords.InTerminal, Command: Typed));
        }
        Show(found, nothing);
    }

    protected override bool TakesKey(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Shift)
        {
            return false;
        }
        Otherwise = true;
        Choose();
        return true;
    }

    bool IOwnKeys.TakesKey(KeyEventArgs e) => e.Key == Key.Enter;

    /// <summary>A choice's row: its name, greyed where it cannot run here, and what it says beside that (why not, for
    /// such a one) at the right, muted.</summary>
    protected override Control Row()
    {
        TextBlock detail = ListRow.DetailText();
        TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
        row.Children.Add(detail);
        row.Children.Add(label);
        return row.Showing<RunOffer>(choice =>
        {
            ShownWords.Set(detail, TextBlock.TextProperty, choice.Refused ?? choice.Detail);
            ShownWords.Set(label, TextBlock.TextProperty, choice.Label);
            label.Classes.Set(StyleClasses.Muted, choice.Refused is not null);
        });
    }
}

/// <summary>What the run choices say of a command line.</summary>
public static class RunWords
{
    public const string InTerminal = "Run in the Terminal";

    public const string RecentInTerminal = "Run again in the Terminal";
}
