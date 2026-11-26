using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.CSharp;

/// <summary>A card over the editor, every one alike: hover information, a diagnostic's message, a signature, the
/// completion list's item. Code is drawn as the editor draws it (<see cref="ColouredCode"/>), a signature too long for the
/// card a parameter a line, prose in the interface's font, a problem in its severity's colour; as wide as what it holds,
/// up to the popups' Medium, a width words read well at. A paused program's value with members is its row of the stop's
/// values (<see cref="AddValues"/>), which the card then takes the pointer and the keys for, up to the popups' Wide. A
/// screen reader meets it as a tooltip called by what it is for (Parameter Info).</summary>
internal sealed class InfoCard : Border, IHoverCard, IHeldToEditor, IHeardPopup
{
    /// <summary>The most rows of a value the card shows before it scrolls.</summary>
    private const int MostRows = 16;

    private readonly StackPanel parts = new() { Spacing = Gaps.Small };

    /// <summary>The width a card of values has taken: it only grows while it shows them, so a row closed never narrows it
    /// under the pointer.</summary>
    private double fitted;

    public InfoCard(string called)
    {
        Called = called;
        Classes.Add(StyleClasses.Popup);
        Padding = PopupFrame.CardInset;
        MaxWidth = PopupFrame.Medium;
        Child = parts;
        IsHitTestVisible = false;
    }

    /// <summary>The most the card is wide: a value's rows the widest of the popups, words and code a width they read at.</summary>
    private double Widest => Values is null ? PopupFrame.Medium : PopupFrame.Wide;

    public AutomationControlType Role => AutomationControlType.ToolTip;

    public string Called { get; }

    public bool IsEmpty => parts.Children.Count == 0;

    /// <summary>Whether it holds something to use (a sound's Play), so it takes the pointer and stays while the pointer is on it.</summary>
    public bool Usable { get; private set; }

    /// <summary>Plays a sound a preview offers; nothing without it.</summary>
    public Action<string>? Play { get; set; }

    /// <summary>The largest a preview's picture is drawn, in logical units.</summary>
    private const double PictureSize = 128;

    /// <summary>The pictures its previews decoded, let go as it is cleared.</summary>
    private readonly List<Avalonia.Media.Imaging.Bitmap> pictures = [];

    /// <summary>The rows of a value the card shows, null for a card of words alone.</summary>
    public Debugging.VariablesList? Values { get; private set; }

    /// <summary>What the card says, a line a part, a value's rows a line each, indented as they are.</summary>
    public string Text => string.Join('\n', parts.Children.Select(part => part switch
    {
        TextBlock words => words.Text,
        ColouredCode code => code.Code,
        Border { Child: Panel { Children: [ColouredCode overload, ..] } } => overload.Code,
        Panel row => string.Join(' ', row.Children.OfType<TextBlock>().Select(t => t.Text)),
        Debugging.VariablesList values => string.Join('\n', values.Tree.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Name} = {r.TypeLabel}{r.Shown}")),
        _ => null,
    }).OfType<string>());

    public void Clear()
    {
        parts.Children.Clear();
        foreach (Avalonia.Media.Imaging.Bitmap picture in pictures)
        {
            picture.Dispose();
        }
        pictures.Clear();
        Values = null;
        Usable = false;
        fitted = 0;
        IsHitTestVisible = false;
        MaxWidth = PopupFrame.Medium;
    }

    /// <summary>Held to a room narrower than it would be, its words wrapping to it; as wide as it takes of itself, up to
    /// <see cref="Widest"/>, in a wider one.</summary>
    public void Within(double room) => MaxWidth = Math.Min(Widest, Math.Max(0, room));

    /// <summary>A value's rows, which open in the card: it takes the pointer and the keys, a width of the popups'
    /// (<see cref="PopupFrame.Fitting"/>), growing as rows open, and scrolls past <see cref="MostRows"/> rows or the room it is given,
    /// at least four rows but in an editor whose rows hold fewer (<paramref name="whole"/>, its height), as many as they hold.</summary>
    public void AddValues(Debugging.VariablesList values, double room, double whole)
    {
        Values = values;
        double rows = Math.Max(ListRow.Height * 4, Math.Min(ListRow.Height * MostRows, room - Padding.Top - Padding.Bottom));
        double holds = whole - Padding.Top - Padding.Bottom - BorderThickness.Top - BorderThickness.Bottom - values.Padding.Top - values.Padding.Bottom;
        values.MaxHeight = Math.Max(ListRow.Height, Math.Min(rows, holds)) + values.Padding.Top + values.Padding.Bottom;
        parts.Children.Add(values);
        IsHitTestVisible = true;
        values.Tree.Rows.CollectionChanged += (_, _) =>
        {
            if (Values == values)
            {
                InvalidateMeasure();
            }
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Values is null)
        {
            return base.MeasureOverride(availableSize);
        }
        // The narrowest of the popups' widths holding every row as laid out, else the widest; in a narrower room, the room.
        double natural = base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height)).Width;
        double wanted = PopupFrame.Fitting(natural);
        fitted = Math.Max(fitted, Math.Min(wanted, availableSize.Width));
        double width = Math.Min(fitted, availableSize.Width);
        return new Size(width, base.MeasureOverride(new Size(width, availableSize.Height)).Height);
    }

    /// <summary>Code in a language, coloured as its highlighter colours it (<see cref="ColouredCode"/>); plain for none. A
    /// signature too long for the card is laid out a parameter a line.</summary>
    public void AddCode(string code, Languages.Language? language = null) => parts.Children.Add(Signed(ColouredCode.Of(code, language)));

    /// <summary>Code a language server shows of a file in <paramref name="language"/>: in the colours of the parts it gives
    /// where they spell it (C#'s, through Passenger's extension), else as the language's highlighter colours it, but plain
    /// for C# and Razor, whose server shows display text rather than code to parse.</summary>
    public void AddServed(string code, IReadOnlyList<Roslyn.Extension.CodePart>? given, Languages.Language? language)
    {
        if (given is not null && Languages.CSharp.ServerCode.Of(given) is var (shown, spans) && Languages.CSharp.ServerCode.SameCode(shown, code))
        {
            parts.Children.Add(Signed(ColouredCode.Of(shown, spans, language)));
            return;
        }
        AddCode(code, language == Languages.Language.CSharp || language == Languages.Language.Razor ? null : language);
    }

    /// <summary>One of a call's overloads, among the rest: its code as <see cref="AddServed"/> shows it, the parameter the
    /// caret's argument fills strong (<see cref="ColouredCode.Marked"/>), set back where the call's arguments no longer fit
    /// it; with <paramref name="chosen"/>, a row of a list stepped through, marked as a chosen row is where it is the one.</summary>
    public void AddOverload(string code, IReadOnlyList<Roslyn.Extension.CodePart>? given, Languages.Language? language, bool fits, int parameter,
        bool? chosen = null)
    {
        AddServed(code, given, language);
        ColouredCode shown = (ColouredCode)parts.Children[^1];
        if (parameter >= 0 && Languages.SignatureLines.Parameters(shown.Code) is { } places && parameter < places.Count)
        {
            shown.Marked = places[parameter];
        }
        if (!fits)
        {
            shown.Classes.Add(StyleClasses.Unfit);
        }
        if (chosen is { } isChosen)
        {
            // A list stepped through: each a row, the one chosen marked as a chosen row is.
            parts.Children.RemoveAt(parts.Children.Count - 1);
            Border row = new();
            ChosenMark.Hold(row, shown);
            row.Classes.Set(StyleClasses.Chosen, isChosen);
            parts.Children.Add(row);
        }
    }

    /// <summary>The most overloads a card lists at once, so what follows them shows in the editor's room: Parameter Info's
    /// round the one chosen, a completion item's first.</summary>
    public const int MostOverloads = 8;

    /// <summary>A completion item's first <see cref="MostOverloads"/> overloads, each as <see cref="AddOverload"/> shows it,
    /// set back where the arguments of a call its name already makes no longer fit it, and how many more there are.</summary>
    public void AddOverloads(IReadOnlyList<IReadOnlyList<Roslyn.Extension.CodePart>> overloads, IReadOnlyList<bool>? fits, Languages.Language? language)
    {
        int listed = Math.Min(overloads.Count, MostOverloads);
        for (int i = 0; i < listed; i++)
        {
            AddOverload(Languages.CSharp.ServerCode.Of(overloads[i]).Text, overloads[i], language, fits is null || i >= fits.Count || fits[i], -1);
        }
        if (overloads.Count > listed)
        {
            AddText(Core.Plural.Of(overloads.Count - listed, "more overload"));
        }
    }

    /// <summary>A card's code, a signature among it laid out a parameter a line where the card is too narrow for its line.</summary>
    private static ColouredCode Signed(ColouredCode code)
    {
        code.Signature = true;
        return code;
    }

    public void AddText(string text, IBrush? colour = null) => parts.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { StyleClasses.Caption },
        Foreground = colour ?? Brush(TokenKeys.TextMuted),
    });

    void IHoverCard.AddText(string text) => AddText(text);

    public void AddChart(Control chart) => parts.Children.Add(chart);

    /// <summary>A file's preview: a picture drawn small (decoded off the window's thread), a sound with a button that plays
    /// it, a scene's top nodes in the code's font, each over its words.</summary>
    public void AddPreview(FilePreview preview)
    {
        switch (preview.Kind)
        {
            case PreviewKind.Picture:
                Image picture = new() { MaxWidth = PictureSize, MaxHeight = PictureSize, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                parts.Children.Add(picture);
                IdeErrors.Observe(ShowPictureAsync(picture, preview.File));
                AddText(preview.Named);
                break;
            case PreviewKind.Sound:
                Button play = ToolButtons.Of(IconKeys.Run, "Play", () => Play?.Invoke(preview.File));
                parts.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Gaps.Small,
                    Children = { play, new TextBlock { Classes = { StyleClasses.Caption }, Text = preview.Named, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(TokenKeys.TextMuted) } },
                });
                Usable = true;
                IsHitTestVisible = true;
                break;
            default:
                AddText(preview.Named);
                if (preview.Nodes.Count > 0)
                {
                    AddCode(string.Join('\n', preview.Nodes.Select((n, i) => i == 0 ? n : $"  {n}")));
                }
                break;
        }
    }

    /// <summary>A picture into its place once decoded, at most the pixels its largest size takes on this screen.</summary>
    private async Task ShowPictureAsync(Image picture, string file)
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 2;
        if (await Godot.GodotResources.PreviewAsync(file, (int)Math.Ceiling(PictureSize * Math.Max(scale, 1))) is not { } made)
        {
            return;
        }
        if (picture.Parent != parts)
        {
            // Cleared while it decoded: another card's words stand there now.
            made.Dispose();
            return;
        }
        pictures.Add(made);
        picture.Source = made;
    }

    /// <summary>A language server's hover, read by its kind: plain text as written, Markdown as <see cref="AddMarkdown"/>
    /// shows it.</summary>
    public void AddHover(Lsp.Hover hover) =>
        AddBlocks(HoverMarkdown.ByKind(Lsp.Shapes.Text(hover.Contents), HoverMarkdown.IsPlain(hover.Contents), words: true), hover.Code);

    /// <summary>A hover's Markdown: its fenced code as code (<see cref="AddServed"/>, by the parts given for it, else as the
    /// language its fence names), the prose between as its words (<see cref="HoverMarkdown"/>).</summary>
    /// <param name="overloadsShown">Whether the card lists the overloads above it (<see cref="AddOverloads"/>), which take
    /// the place of its signature and of what it says of how many more there are.</param>
    public void AddMarkdown(string markdown, IReadOnlyList<IReadOnlyList<Roslyn.Extension.CodePart>>? code = null, bool overloadsShown = false) =>
        AddBlocks(HoverMarkdown.ByKind(markdown, plain: false, words: true), code, overloadsShown);

    /// <summary>What a description says after its signature of how many more overloads there are, after the space it may
    /// begin with.</summary>
    private static readonly System.Text.RegularExpressions.Regex MoreOverloads =
        new(@"^\s*\(\+\s*\d+\s+(generic\s+)?overloads?\)\s*", System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void AddBlocks(IEnumerable<(IReadOnlyList<string> Lines, bool Code, string Info)> blocks, IReadOnlyList<IReadOnlyList<Roslyn.Extension.CodePart>>? given,
        bool overloadsShown = false)
    {
        List<IReadOnlyList<Roslyn.Extension.CodePart>> unused = [.. given ?? []];
        bool signature = overloadsShown;
        foreach ((IReadOnlyList<string> lines, bool code, string info) in blocks)
        {
            string text = string.Join('\n', lines).Trim('\n', ' ');
            if (overloadsShown)
            {
                text = MoreOverloads.Replace(text, string.Empty);
            }
            if (text.Length == 0)
            {
                continue;
            }
            if (code && signature)
            {
                // The signature the overloads listed include.
                signature = false;
                continue;
            }
            if (code)
            {
                int at = unused.FindIndex(p => Languages.CSharp.ServerCode.SameCode(Languages.CSharp.ServerCode.Of(p).Text, text));
                AddServed(text, at < 0 ? null : unused[at], ColouredCode.LanguageOf(info));
                if (at >= 0)
                {
                    unused.RemoveAt(at);
                }
            }
            else
            {
                AddText(text);
            }
        }
    }

    internal static IBrush Brush(string key) =>
        Application.Current is { } app ? Tokens.Brush(app, key) : Brushes.Transparent;
}

/// <summary>One completion row: the badge for its kind (a coloured letter, its colour the theme's resource of that key,
/// found as it is shown), its label and the detail the server gives (a type, a namespace). Made where the list is
/// filtered, off the UI thread.</summary>
internal sealed record CompletionRow(Lsp.CompletionItem Item, string Letter, string BadgeKey, string Detail)
{
    public string Label => Item.Label + (Item.LabelDetails?.Detail ?? string.Empty);
}

/// <summary>A place in the completion list, kept for good: the row it shows now, which its control follows while shown
/// (<see cref="CompletionRowView"/>); none where the list has fewer rows than places, the place then hidden.</summary>
internal sealed class CompletionSlot : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly System.ComponentModel.PropertyChangedEventArgs RowChanged = new(nameof(Row));

    private CompletionRow? row;

    public CompletionRow? Row
    {
        get => row;
        set
        {
            if (!ReferenceEquals(row, value))
            {
                row = value;
                PropertyChanged?.Invoke(this, RowChanged);
            }
        }
    }

    /// <summary>The slot shows another row.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The completion list: rows under the caret, the selected one highlighted, ten at a time, scrolled by its own
/// bar and the wheel. Its ten places (<see cref="CompletionSlot"/>) are made once, each with its container and controls,
/// and handed the rows they show now: a key that narrows the list, a move past its edge and a scroll change the words in
/// the places' controls, and a list of fewer rows hides the places it leaves, so a key lays out only the rows it changed,
/// and no container is ever let go of, to be attached and styled again.</summary>
internal sealed class CompletionPopup : Border, IHeardPopup
{
    /// <summary>How many rows the list shows at once.</summary>
    private const int Shown = 10;

    /// <summary>How far a turn of the wheel by one notch scrolls, as a scroll viewer scrolls.</summary>
    private const double WheelStep = 50;

    private readonly CompletionSlot[] slots = [.. Enumerable.Range(0, Shown).Select(_ => new CompletionSlot())];
    private readonly ScrollBar bar;

    private IReadOnlyList<CompletionRow> rows = [];

    /// <summary>The row shown in the first place.</summary>
    private int top;

    /// <summary>The row chosen, shown or scrolled away; -1 with none.</summary>
    private int chosen = -1;

    /// <summary>Whether the list is handing its places their rows, its selection then its own rather than the person's.</summary>
    private bool placing;

    /// <summary>What the wheel has turned short of a whole row.</summary>
    private double turned;

    /// <summary>The item the selection was last said to be on, so a filter or an answer that leaves it there says nothing.</summary>
    private Lsp.CompletionItem? told;

    /// <summary>Whether the list is being laid out ahead (<see cref="LayOutAhead"/>), its choice no one's to hear of.</summary>
    private bool ahead;

    public CompletionPopup()
    {
        Classes.Add(StyleClasses.Popup);
        MinWidth = PopupFrame.Narrow;
        MaxWidth = PopupFrame.Wide;
        Padding = ListRow.PopupList;
        // Its places stand in a plain panel, which lets go of none it hides; its own scrolling never moves them.
        List = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = slots,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
            AutoScrollToSelectedItem = false,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Disabled,
        };
        // The popup is the list a screen reader meets, its rows straight under it.
        AutomationProperties.SetAccessibilityView(List, AccessibilityView.Raw);
        ReusedRows.Use(List, () => new CompletionRowView(this));
        List.ContainerPrepared += (_, e) => e.Container.IsVisible = slots[e.Index].Row is not null;
        List.SelectionChanged += (_, _) =>
        {
            if (!placing && List.SelectedIndex >= 0)
            {
                // The person's click on a row.
                chosen = top + List.SelectedIndex;
                FollowSelected();
            }
        };
        bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            Minimum = 0,
            SmallChange = 1,
            LargeChange = Shown,
            ViewportSize = Shown,
            IsVisible = false,
        };
        bar.ValueChanged += (_, e) =>
        {
            if (!placing)
            {
                ScrollTo((int)Math.Round(e.NewValue), fromBar: true);
            }
        };
        // Heard on its way in, before the list's own view, which a fraction of a pixel's rounding lets take it.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Filters.Margin = new Thickness(Gaps.Normal, Gaps.Hair, Gaps.Normal, 0);
        DockPanel.SetDock(Filters, Dock.Bottom);
        Child = new DockPanel { Children = { Filters, new Panel { Children = { List, bar } } } };
    }

    public ListBox List { get; }

    /// <summary>The filters by the kind of member under the rows, shown for a list of members of more than one kind.</summary>
    public CompletionFilters Filters { get; } = new();

    public AutomationControlType Role => AutomationControlType.List;

    public string Called => "Completion";

    /// <summary>Held to a room narrower than it would be, even than its least width, its names and details ending in an
    /// ellipsis rather than past the editor's edge; as wide as its rows make it, from the popups' Narrow to their Wide
    /// (<see cref="PopupFrame"/>), in a wider one: it hugs its rows rather than taking a width of the scale, measured once
    /// as a key changes them. Its least gives way in a room too narrow for it and a card beside it, so the list against
    /// either edge of the editor leaves its item's card room on the other side (<see cref="PopupPlacement.BesideRoom"/>).</summary>
    public void Within(double room)
    {
        MinWidth = Math.Clamp(room - PopupPlacement.BesideRoom, 0, PopupFrame.Narrow);
        MaxWidth = Math.Min(PopupFrame.Wide, Math.Max(0, room));
    }

    /// <summary>The selection moved to another item (<see cref="Lsp.CompletionItem.Same"/>): another row chosen, or the row
    /// chosen showing another item; never the same item a list answered again lists anew.</summary>
    public event EventHandler? SelectedChanged;

    /// <summary>A XAML colour's own swatch for its badge, null for an item badged by its kind.</summary>
    public Func<Lsp.CompletionItem, IBrush?>? Swatch { get; set; }

    public CompletionRow? Selected => chosen >= 0 && chosen < rows.Count ? rows[chosen] : null;

    /// <summary>Where the row chosen stands among <see cref="Rows"/>; -1 with none.</summary>
    public int ChosenAt => Selected is null ? -1 : chosen;

    public IReadOnlyList<CompletionRow> Rows => rows;

    /// <summary>The list's rows, the first chosen, scrolled only as far as shows the one chosen. Rows of the name already
    /// shown (<paramref name="sameName"/>: a list grown by items as they came, or answered again) keep the item chosen where
    /// it is among them, and, where the person had scrolled away from it, the rows scrolled to, wherever they now stand.</summary>
    public void Show(IReadOnlyList<CompletionRow> rows, bool sameName = false)
    {
        Lsp.CompletionItem? stays = sameName ? Selected?.Item : null;
        Lsp.CompletionItem? first = sameName && chosen >= 0 && (chosen < top || chosen >= top + Shown) && top < this.rows.Count ? this.rows[top].Item : null;
        this.rows = rows;
        chosen = rows.Count > 0 ? 0 : -1;
        if (stays is not null && IndexOf(stays) is >= 0 and int kept)
        {
            chosen = kept;
        }
        if (first is null)
        {
            Place();
            return;
        }
        if (IndexOf(first) is >= 0 and int scrolled)
        {
            top = scrolled;
        }
        ScrollTo(top, fromBar: false);
    }

    /// <summary>Where an item stands among the rows, however many answers ago it was listed; -1 where it is not.</summary>
    private int IndexOf(Lsp.CompletionItem item)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (Lsp.CompletionItem.Same(rows[i].Item, item))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The list laid out once before it is first shown, so its first list costs what any other does: the rows'
    /// controls made with their containers, styled and their words shaped. Shown and hidden again within one turn of the
    /// window's thread, it is never drawn; placed in its window, as a list off it lays out no row it would not see. The
    /// rows stay in their places for the first list to show its own in, and nothing hears of their choice.</summary>
    public void LayOutAhead(IReadOnlyList<CompletionRow> rows)
    {
        ahead = true;
        try
        {
            Canvas.SetLeft(this, 0);
            Canvas.SetTop(this, 0);
            Show(rows);
            IsVisible = true;
            UpdateLayout();
        }
        finally
        {
            IsVisible = false;
            ahead = false;
            told = null;
        }
    }

    public void Move(int step)
    {
        if (rows.Count == 0)
        {
            return;
        }
        chosen = Math.Clamp(chosen + step, 0, rows.Count - 1);
        Place();
    }

    /// <summary>The rows from the one chosen scrolled into view, by as few as show it, and handed to the places.</summary>
    private void Place()
    {
        if (chosen >= 0 && chosen < top)
        {
            top = chosen;
        }
        else if (chosen >= top + Shown)
        {
            top = chosen - Shown + 1;
        }
        ScrollTo(top, fromBar: false);
    }

    /// <summary>The places handed the rows from <paramref name="first"/> on, as far as the rows go, the rest hidden; the
    /// chosen row selected where it shows. The bar is moved to match unless it moved them (<paramref name="fromBar"/>),
    /// its thumb then where the pointer holds it.</summary>
    private void ScrollTo(int first, bool fromBar)
    {
        top = Math.Clamp(first, 0, Math.Max(0, rows.Count - Shown));
        placing = true;
        try
        {
            for (int i = 0; i < Shown; i++)
            {
                CompletionRow? row = top + i < rows.Count ? rows[top + i] : null;
                slots[i].Row = row;
                List.ContainerFromIndex(i)?.IsVisible = row is not null;
            }
            int selected = chosen >= top && chosen < top + Shown ? chosen - top : -1;
            if (List.SelectedIndex != selected)
            {
                List.SelectedIndex = selected;
            }
            bar.IsVisible = rows.Count > Shown;
            bar.Maximum = Math.Max(0, rows.Count - Shown);
            if (!fromBar)
            {
                bar.Value = top;
            }
        }
        finally
        {
            placing = false;
        }
        FollowSelected();
    }

    /// <summary>A turn of the wheel over the list scrolls it a row for each part of a notch that makes one, the choice
    /// staying where it is; the list's own view, which never scrolls, is not given it.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (rows.Count <= Shown)
        {
            return;
        }
        turned -= e.Delta.Y * WheelStep / ListRow.Height;
        int step = (int)turned;
        turned -= step;
        if (step != 0)
        {
            ScrollTo(top + step, fromBar: false);
        }
    }

    private void FollowSelected()
    {
        if (ahead)
        {
            return;
        }
        Lsp.CompletionItem? now = Selected?.Item;
        bool same = Lsp.CompletionItem.Same(now, told);
        told = now;
        if (!same)
        {
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A completion row's controls, made once with a container of the list and kept: the slot it is handed (its data
/// context) is shown while the row is, its words and badge changed in place as the slot shows another row.</summary>
internal sealed class CompletionRowView : DockPanel
{
    private readonly CompletionPopup popup;
    private readonly Border badge;
    private readonly TextBlock letter;
    private readonly TextBlock detail;
    private readonly TextBlock label;

    public CompletionRowView(CompletionPopup popup)
    {
        this.popup = popup;
        Height = ListRow.Height;
        badge = Tags.Kind(out letter);
        badge.Margin = new Thickness(0, 0, Gaps.Normal, 0);
        // The name first, the detail at the row's end in what it leaves: a list narrowed to its room cuts the detail first.
        label = new TextBlock
        {
            [DockPanel.DockProperty] = Dock.Left,
            FontFeatures = Fonts.NoLigatures,
            Classes = { StyleClasses.Code },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        detail = ListRow.DetailText();
        detail.Foreground = InfoCard.Brush(TokenKeys.TextMuted);
        detail.MaxWidth = 200;
        detail.HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(badge);
        Children.Add(label);
        Children.Add(detail);
        AutomationProperties.SetLabeledBy(this, label);
        this.Showing<CompletionSlot>(slot =>
        {
            if (slot.Row is { } row)
            {
                Show(row);
            }
        });
    }

    private void Show(CompletionRow row)
    {
        IBrush? swatch = popup.Swatch?.Invoke(row.Item);
        letter.Text = swatch is null ? row.Letter : string.Empty;
        badge.Background = swatch ?? InfoCard.Brush(row.BadgeKey);
        detail.Text = row.Detail;
        label.Text = row.Label;
    }
}

/// <summary>A titled list to choose from (fixes, definitions, usages, surround templates): Up and Down, Enter or a double
/// click to choose, a choice's digit to choose it at once.</summary>
public sealed class ChoicePopup : FittingFrame, IHeardPopup, IHeldToEditor
{
    private readonly ListBox list;

    /// <summary>What is offered, by its words.</summary>
    public IReadOnlyList<string> Titles { get; }

    /// <summary>What each choice says beside its words.</summary>
    public IReadOnlyList<string> Details { get; }

    /// <summary>What the list is titled.</summary>
    public string Title { get; }

    public ChoicePopup(string title, IReadOnlyList<Choice> choices)
    {
        Title = title;
        Titles = [.. choices.Select(c => c.Text)];
        Details = [.. choices.Select(c => c.Detail)];
        bool numbered = choices.Any(c => c.Mnemonic is not null);
        Padding = ListRow.PopupList;
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            MaxHeight = 26 * 14,
            ItemsSource = choices,
            SelectedIndex = choices.Count > 0 ? 0 : -1,
        };
        // The popup is the list a screen reader meets, its rows straight under it.
        AutomationProperties.SetAccessibilityView(list, AccessibilityView.Raw);
        ReusedRows.Use(list, () => ChoiceRow(numbered));
        Child = new StackPanel
        {
            Spacing = Gaps.Tight,
            Children =
            {
                new TextBlock { Text = title, Classes = { StyleClasses.PaneTitle }, Margin = new Thickness(Gaps.Wide, Gaps.Hair) },
                list,
            },
        };
        ListKeys.Act(list, new ListActs { Open = Choose, Keys = TakeDigit });
        list.SelectionChanged += (_, _) => Moved?.Invoke(this, list.SelectedItem as Choice);
        Dispatcher.UIThread.Post(() => ListKeys.Hold(list, chooseFirst: true), DispatcherPriority.Loaded);
    }

    public AutomationControlType Role => AutomationControlType.List;

    public string Called => Title;

    /// <summary>Held to a room narrower than the width it takes, its words then ending in an ellipsis.</summary>
    public void Within(double room) => MaxWidth = Math.Max(0, room);

    /// <summary>Where the choice selected stands among <see cref="Titles"/>; -1 with none.</summary>
    public int ChosenAt => list.SelectedIndex;

    /// <summary>The choice made.</summary>
    public event EventHandler<Choice>? Done;

    /// <summary>The choice the list has selected now, as Up and Down move it.</summary>
    public event EventHandler<Choice?>? Moved;

    /// <summary>A choice's digit chooses it at once.</summary>
    private bool TakeDigit(KeyEventArgs e)
    {
        if (Digit(e.Key) is not { } digit || list.ItemsSource is not IEnumerable<Choice> choices || choices.FirstOrDefault(c => c.Mnemonic == digit) is not { } marked)
        {
            return false;
        }
        Done?.Invoke(this, marked);
        return true;
    }

    /// <summary>A choice's row: its digit when the list has them, its words, and its detail muted at the row's end, in code's
    /// font for code.</summary>
    private static Control ChoiceRow(bool numbered)
    {
        TextBlock digit = new() { Classes = { StyleClasses.Code }, Width = 10, IsVisible = numbered, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Gaps.Normal, 0), [DockPanel.DockProperty] = Dock.Left };
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = ListRow.DetailText();
        detail.MaxWidth = 460;
        DockPanel row = new() { Height = ListRow.Height, Children = { digit, detail, text } };
        AutomationProperties.SetLabeledBy(row, text);
        string? detailFont = null;
        return row.Showing<Choice>(choice =>
        {
            digit.Text = choice.Mnemonic?.ToString();
            digit.Foreground = InfoCard.Brush(TokenKeys.TextMuted);
            text.Text = choice.Text;
            detail.Text = choice.Detail;
            detail.Foreground = InfoCard.Brush(TokenKeys.TextMuted);
            detail.Resource(TextBlock.FontFamilyProperty, ref detailFont, choice.DetailIsCode ? CodeFonts.Key : null);
        });
    }

    private static char? Digit(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        _ => null,
    };

    private void Choose()
    {
        if (list.SelectedItem is Choice choice)
        {
            Done?.Invoke(this, choice);
        }
    }
}

/// <summary>One thing to choose: what it says, a quieter detail beside it, what it stands for, and the digit that
/// chooses it, if it has one.</summary>
public sealed record Choice(string Text, string Detail, object Value, bool DetailIsCode = false, char? Mnemonic = null);

/// <summary>Inline rename: the name in a box over itself, all of it selected; Enter renames (a new name's box, under its
/// own caption, names what a refactoring makes). For a
/// type declared in files named after it, Also rename files to reflect this change is a check box under it, with the
/// files it would rename, clicked while the keys stay with the name.</summary>
public sealed class RenameBox : Border, IHeardPopup, IGivesWayInHeight
{
    private const int VisibleNameLimit = 6;
    private readonly string original;
    private readonly TextBox name;
    private readonly StackPanel body;
    private CheckBox? renameFiles;
    private ListBox? suggestionList;

    public RenameBox(string current, string caption = "Rename")
    {
        original = current;
        Called = caption;
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Narrow;
        Padding = PopupFrame.Inset;
        name = new TextBox { Classes = { StyleClasses.Code }, Text = current };
        TextBlock captioned = new() { Text = caption, Classes = { StyleClasses.PaneTitle } };
        AutomationProperties.SetLabeledBy(name, captioned);
        body = new StackPanel
        {
            Spacing = Gaps.Tight,
            Children =
            {
                captioned,
                name,
            },
        };
        Child = body;
        name.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    public AutomationControlType Role => AutomationControlType.Group;

    /// <summary>Held to a room narrower than a name's popup is wide.</summary>
    public void Within(double room) => Width = Math.Min(PopupFrame.Narrow, Math.Max(0, room));

    /// <summary>The list gives way so there is room for the name field and at least one candidate.</summary>
    public void WithinHeight(double room)
    {
        if (suggestionList is not { } list)
        {
            return;
        }
        list.MaxHeight = double.PositiveInfinity;
        if (double.IsFinite(room) && list.IsEffectivelyVisible)
        {
            Measure(Size.Infinity);
            list.MaxHeight = Math.Max(ListRow.Height, room - (DesiredSize.Height - list.DesiredSize.Height));
        }
    }

    /// <summary>What the box asks for, its caption.</summary>
    public string Called { get; }

    /// <summary>The new name, or null when cancelled.</summary>
    public event EventHandler<string?>? Done;

    /// <summary>The name as the box has it now.</summary>
    public string Text => name.Text ?? string.Empty;

    /// <summary>The files named after the type renamed, once offered; none otherwise.</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>Whether the files offered are to be renamed with the type.</summary>
    public bool RenamesFiles => renameFiles?.IsChecked == true;

    /// <summary>Also rename files to reflect this change, checked or cleared.</summary>
    public event EventHandler<bool>? RenamesFilesChanged;

    /// <summary>The options under the input changed how much room the box needs.</summary>
    internal event EventHandler? ContentsChanged;

    internal void OfferSuggestions(IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return;
        }

        string? chosen = null;
        ListBox list = new()
        {
            Classes = { StyleClasses.Tree },
            Focusable = false,
            ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock
            {
                Text = item,
                Classes = { StyleClasses.Code },
                TextTrimming = TextTrimming.CharacterEllipsis,
            }),
        };
        suggestionList = list;
        TextBlock label = new() { Text = "Suggested names", Classes = { StyleClasses.Muted, StyleClasses.Caption } };
        AutomationProperties.SetLabeledBy(list, label);
        StackPanel offered = new() { Spacing = Gaps.Tight, Children = { label, list } };
        body.Children.Insert(2, offered);

        void Filter(string prefix)
        {
            string[] matches = [.. suggestions.Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(VisibleNameLimit)];
            chosen = null;
            list.SelectedIndex = -1;
            list.ItemsSource = matches;
            offered.IsVisible = matches.Length > 0;
            InvalidateMeasure();
            ContentsChanged?.Invoke(this, EventArgs.Empty);
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is string selected)
            {
                chosen = selected;
                name.Text = selected;
                name.Focus();
                name.CaretIndex = selected.Length;
            }
        };
        name.TextChanged += (_, _) =>
        {
            if (Text != chosen)
            {
                Filter(Text);
            }
        };
        Filter(Text == original ? string.Empty : Text);
    }

    /// <summary>Offers to rename the files named after the type with it, checked as the option was left.</summary>
    public void OfferFiles(IReadOnlyList<string> files, bool on)
    {
        Files = files;
        renameFiles = PopupFrame.Option("Also rename files to reflect this change", on);
        renameFiles.IsCheckedChanged += (_, _) => RenamesFilesChanged?.Invoke(this, RenamesFiles);
        body.Children.Add(renameFiles);
        body.Children.Add(new TextBlock
        {
            Text = $"Corresponding files to be renamed: {string.Join(", ", files.Select(Path.GetFileName))}",
            Classes = { StyleClasses.Muted, StyleClasses.Caption },
            TextWrapping = TextWrapping.Wrap,
        });
        // Measured again now, as it grew, so it is placed for its new size before the next layout.
        InvalidateMeasure();
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down && suggestionList is { ItemCount: > 0 } list)
        {
            int direction = e.Key == Key.Down ? 1 : -1;
            int selected = list.SelectedIndex;
            list.SelectedIndex = selected < 0 ? (direction > 0 ? 0 : list.ItemCount - 1)
                : (selected + direction + list.ItemCount) % list.ItemCount;
            list.ScrollIntoView(list.SelectedIndex);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            string text = name.Text?.Trim() ?? string.Empty;
            Done?.Invoke(this, text.Length > 0 ? text : null);
            e.Handled = true;
        }
    }
}

internal static class Fonts
{
    /// <summary>Code as the editor draws it by default, its characters each its own: JetBrains Mono would join "&lt;&gt;" into
    /// a diamond and "=>" into an arrow.</summary>
    public static readonly FontFeatureCollection NoLigatures = [new FontFeature { Tag = "calt", Value = 0 }, new FontFeature { Tag = "liga", Value = 0 }];

    /// <summary>Code's features as Settings' Enable ligatures has the editor draw it: the font's own with it on.</summary>
    public static FontFeatureCollection? Features(bool ligatures) => ligatures ? null : NoLigatures;
}
