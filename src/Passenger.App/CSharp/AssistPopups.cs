using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Passenger.App.CSharp;

/// <summary>A card over the editor: hover information, a diagnostic's message, a signature. Code is
/// set in the editor's font, prose in the interface's, a problem in its severity's colour. A paused program's value with
/// members is its row of the stop's values (<see cref="AddValues"/>), which the card then takes the pointer and the keys for.</summary>
internal sealed partial class InfoCard : Border
{
    // Styled as the Border it is, so the theme's Border.popup reaches it.
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The most a card of words is wide; one of a value's rows takes a width of the popups' (<see cref="PopupFrame"/>).</summary>
    private const double WordsWidth = 640;

    /// <summary>The most rows of a value the card shows before it scrolls.</summary>
    private const int MostRows = 16;

    private readonly StackPanel parts = new() { Spacing = 6 };

    /// <summary>The width a card of values has taken: it only grows while it shows them, so a row closed never narrows it
    /// under the pointer.</summary>
    private double fitted;

    public InfoCard()
    {
        Classes.Add("popup");
        Padding = new Thickness(10, 8);
        MaxWidth = WordsWidth;
        Child = parts;
        IsHitTestVisible = false;
    }

    public bool IsEmpty => parts.Children.Count == 0;

    /// <summary>The rows of a value the card shows, null for a card of words alone.</summary>
    public Debugging.VariablesList? Values { get; private set; }

    /// <summary>What the card says, a line a part, a value's rows a line each, indented as they are.</summary>
    public string Text => string.Join('\n', parts.Children.Select(part => part switch
    {
        TextBlock words => words.Text,
        Debugging.VariablesList values => string.Join('\n', values.Tree.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Name} = {r.TypeLabel}{r.Shown}")),
        _ => null,
    }).OfType<string>());

    public void Clear()
    {
        parts.Children.Clear();
        Values = null;
        fitted = 0;
        IsHitTestVisible = false;
        MaxWidth = WordsWidth;
    }

    /// <summary>A value's rows, which open in the card: it takes the pointer and the keys, a width of the popups', growing as
    /// rows open, and scrolls past <see cref="MostRows"/> rows or the room it is given.</summary>
    public void AddValues(Debugging.VariablesList values, double room)
    {
        Values = values;
        values.MaxHeight = Math.Max(ListRow.Height * 4, Math.Min(ListRow.Height * MostRows, room - Padding.Top - Padding.Bottom)) + values.Padding.Top + values.Padding.Bottom;
        parts.Children.Add(values);
        IsHitTestVisible = true;
        MaxWidth = PopupFrame.Wide;
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
        // The narrowest of the popups' widths that holds every row as it would lay out, else the widest a card takes.
        double natural = base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height)).Width;
        double wanted = PopupFrame.Widths.Where(w => w <= PopupFrame.Wide).FirstOrDefault(w => w >= natural, PopupFrame.Wide);
        fitted = Math.Max(fitted, wanted);
        return new Size(fitted, base.MeasureOverride(new Size(fitted, availableSize.Height)).Height);
    }

    public void AddCode(string code) => parts.Children.Add(new TextBlock
    {
        Text = code,
        FontFamily = Fonts.Mono,
        FontFeatures = Fonts.NoLigatures,
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(TokenKeys.Text),
    });

    public void AddText(string text, IBrush? colour = null) => parts.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12.5,
        Foreground = colour ?? Brush(TokenKeys.TextMuted),
    });

    /// <summary>Hover markdown, as Roslyn writes it: fenced code blocks and prose between them.</summary>
    public void AddMarkdown(string markdown)
    {
        bool code = false;
        List<string> block = [];
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush(block, code);
                code = !code;
                continue;
            }
            block.Add(code ? raw : Plain(raw));
        }
        Flush(block, code);
    }

    private void Flush(List<string> block, bool code)
    {
        string text = string.Join('\n', block).Trim('\n', ' ');
        block.Clear();
        if (text.Length == 0)
        {
            return;
        }
        if (code)
        {
            AddCode(text);
        }
        else
        {
            AddText(text);
        }
    }

    /// <summary>Markdown's emphasis, escapes, images and links' addresses taken out, leaving the words.</summary>
    private static string Plain(string line) => MarkdownItalic().Replace(MarkdownLink().Replace(Imageless(line), "$1")
            .Replace("**", string.Empty)
            .Replace("__", string.Empty), "$1$2")
        .Replace("`", string.Empty)
        .Replace("\\", string.Empty)
        .Replace("&nbsp;", " ")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&amp;", "&");

    internal static IBrush Brush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    /// <summary>A line without its images, and without the space one began it with.</summary>
    private static string Imageless(string line)
    {
        string without = MarkdownImage().Replace(line, string.Empty);
        return without.Length != line.Length && line.TrimStart().StartsWith("![", StringComparison.Ordinal) ? without.TrimStart() : without;
    }

    /// <summary>Markdown's _italic_ and *italic* (TypeScript's *@param*), shown as their words.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\w)_([^_\n]+)_(?!\w)|(?<![\w*])\*(\S(?:[^*\n]*\S)?)\*(?![\w*])")]
    private static partial System.Text.RegularExpressions.Regex MarkdownItalic();

    /// <summary>A Markdown image, which a card of text cannot show (the CSS server's Baseline icon, a data URI): left out.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex MarkdownImage();

    /// <summary>A Markdown link, shown as its words (the CSS server's MDN Reference).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]+)\]\([^)\s]*\)")]
    private static partial System.Text.RegularExpressions.Regex MarkdownLink();
}

/// <summary>One completion row: the badge for its kind (a coloured letter, its colour the theme's resource of that key,
/// found as it is shown), its label and the detail the server gives (a type, a namespace). Made where the list is
/// filtered, off the UI thread.</summary>
internal sealed record CompletionRow(Lsp.CompletionItem Item, string Letter, string BadgeKey, string Detail)
{
    public string Label => Item.Label + (Item.LabelDetails?.Detail ?? string.Empty);
}

/// <summary>A place in the completion list, kept from one filter to the next: the row it shows now, which its control
/// follows (<see cref="CompletionRowView"/>).</summary>
internal sealed class CompletionSlot(CompletionRow row)
{
    private CompletionRow row = row;

    public CompletionRow Row
    {
        get => row;
        set
        {
            if (!ReferenceEquals(row, value))
            {
                row = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The slot shows another row.</summary>
    public event EventHandler? Changed;
}

/// <summary>The completion list: rows under the caret, the selected one highlighted, ten at a time. Its places are kept as
/// the list is filtered (<see cref="CompletionSlot"/>), each given the row it shows now, so a key that narrows the list
/// changes the words in the rows' controls rather than making them anew: the list's containers and their controls stay.</summary>
internal sealed class CompletionPopup : Border
{
    // Styled as the Border it is, so the theme's Border.popup reaches it.
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly AvaloniaList<CompletionSlot> slots = [];

    /// <summary>The item the selection was last said to be on, so a filter that leaves it there says nothing.</summary>
    private Lsp.CompletionItem? told;

    public CompletionPopup()
    {
        Classes.Add("popup");
        MinWidth = 280;
        MaxWidth = 620;
        Padding = new Thickness(0, 4);
        List = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = 26 * 10,
            ItemsSource = slots,
            ItemTemplate = new FuncDataTemplate<CompletionSlot>((_, _) => new CompletionRowView(this), supportsRecycling: true),
        };
        List.SelectionChanged += (_, _) => FollowSelected();
        Child = List;
    }

    public ListBox List { get; }

    /// <summary>The selection moved to another item: another row chosen, or the row chosen showing another item.</summary>
    public event EventHandler? SelectedChanged;

    /// <summary>A XAML colour's own swatch for its badge, null for an item badged by its kind.</summary>
    public Func<Lsp.CompletionItem, IBrush?>? Swatch { get; set; }

    public CompletionRow? Selected => (List.SelectedItem as CompletionSlot)?.Row;

    public IReadOnlyList<CompletionRow> Rows => [.. slots.Select(s => s.Row)];

    /// <summary>Rows in the list's places, the first chosen: the places there already show their new rows, the rest are
    /// added or taken away at the end.</summary>
    public void Show(IReadOnlyList<CompletionRow> rows)
    {
        int kept = Math.Min(rows.Count, slots.Count);
        for (int i = 0; i < kept; i++)
        {
            slots[i].Row = rows[i];
        }
        if (slots.Count > rows.Count)
        {
            slots.RemoveRange(rows.Count, slots.Count - rows.Count);
        }
        else if (rows.Count > kept)
        {
            slots.AddRange(rows.Skip(kept).Select(row => new CompletionSlot(row)));
        }
        int first = rows.Count > 0 ? 0 : -1;
        if (List.SelectedIndex != first)
        {
            List.SelectedIndex = first;
        }
        FollowSelected();
        if (rows.Count > 0)
        {
            List.ScrollIntoView(0);
        }
    }

    public void Move(int step)
    {
        int count = List.ItemCount;
        if (count == 0)
        {
            return;
        }
        List.SelectedIndex = Math.Clamp(List.SelectedIndex + step, 0, count - 1);
        List.ScrollIntoView(List.SelectedIndex);
    }

    private void FollowSelected()
    {
        Lsp.CompletionItem? now = Selected?.Item;
        if (!ReferenceEquals(now, told))
        {
            told = now;
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A completion row's controls, made once for a place in the list and kept: the slot it is given (its data
/// context) is followed, its words and badge changed in place as the slot shows another row.</summary>
internal sealed class CompletionRowView : DockPanel
{
    private readonly CompletionPopup popup;
    private readonly Border badge;
    private readonly TextBlock letter;
    private readonly TextBlock detail;
    private readonly TextBlock label;
    private CompletionSlot? slot;

    public CompletionRowView(CompletionPopup popup)
    {
        this.popup = popup;
        Height = ListRow.Height;
        letter = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = InfoCard.Brush(TokenKeys.BadgeText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge = new Border
        {
            Width = 16,
            Height = 16,
            Classes = { "kindBadge" },
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = letter,
        };
        detail = new TextBlock
        {
            [DockPanel.DockProperty] = Dock.Right,
            Foreground = InfoCard.Brush(TokenKeys.TextMuted),
            FontSize = 12,
            Margin = new Thickness(12, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label = new TextBlock
        {
            FontFamily = Fonts.Mono,
            FontFeatures = Fonts.NoLigatures,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(badge);
        Children.Add(detail);
        Children.Add(label);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (slot is not null)
        {
            slot.Changed -= OnSlotChanged;
        }
        slot = DataContext as CompletionSlot;
        if (slot is not null)
        {
            slot.Changed += OnSlotChanged;
            Show(slot.Row);
        }
    }

    private void OnSlotChanged(object? sender, EventArgs e)
    {
        if (sender is CompletionSlot changed && changed == slot)
        {
            Show(changed.Row);
        }
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

/// <summary>A titled list to choose from (fixes, definitions, usages, surround templates): Up and Down, Enter to choose,
/// a choice's digit to choose it at once, Escape or a click away to dismiss.</summary>
internal sealed class ChoicePopup : Border
{
    // Styled as the Border it is, so the theme's Border.popup reaches it.
    protected override Type StyleKeyOverride => typeof(Border);

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
        Classes.Add("popup");
        MinWidth = 320;
        MaxWidth = 760;
        Padding = new Thickness(0, 6);
        list = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = 26 * 14,
            ItemsSource = choices,
            SelectedIndex = choices.Count > 0 ? 0 : -1,
            ItemTemplate = new FuncDataTemplate<Choice>((choice, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Height = ListRow.Height,
                Children =
                {
                    new TextBlock
                    {
                        Text = choice?.Mnemonic?.ToString(),
                        Foreground = InfoCard.Brush(TokenKeys.TextMuted),
                        FontFamily = Fonts.Mono,
                        Width = 10,
                        IsVisible = numbered,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock { Text = choice?.Text, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = choice?.Detail,
                        Foreground = InfoCard.Brush(TokenKeys.TextMuted),
                        FontFamily = choice?.DetailIsCode == true ? Fonts.Mono : FontFamily.Default,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 460,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }, supportsRecycling: false),
        };
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, Classes = { "popupTitle" }, Margin = new Thickness(12, 2, 12, 2) },
                list,
            },
        };
        list.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.DoubleTapped += (_, _) => Choose();
        list.SelectionChanged += (_, _) => Moved?.Invoke(this, list.SelectedItem as Choice);
        Dispatcher.UIThread.Post(() =>
        {
            list.Focus();
            if (list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is Control first)
            {
                first.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The choice made, or null when dismissed.</summary>
    public event EventHandler<Choice?>? Done;

    /// <summary>The choice the list has selected now, as Up and Down move it.</summary>
    public event EventHandler<Choice?>? Moved;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Choose();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Done?.Invoke(this, null);
            e.Handled = true;
        }
        else if (Digit(e.Key) is { } digit && list.ItemsSource is IEnumerable<Choice> choices && choices.FirstOrDefault(c => c.Mnemonic == digit) is { } marked)
        {
            Done?.Invoke(this, marked);
            e.Handled = true;
        }
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
internal sealed record Choice(string Text, string Detail, object Value, bool DetailIsCode = false, char? Mnemonic = null);

/// <summary>Inline rename: the name in a box over itself, all of it selected; Enter renames, Escape leaves it. For a
/// type declared in files named after it, Also rename files to reflect this change is a check box under it, with the
/// files it would rename, clicked while the keys stay with the name.</summary>
internal sealed class RenameBox : Border
{
    // Styled as the Border it is, so the theme's Border.popup reaches it.
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly TextBox name;
    private readonly StackPanel body;
    private CheckBox? renameFiles;

    public RenameBox(string current)
    {
        Classes.Add("popup");
        Padding = new Thickness(8, 6);
        name = new TextBox { Text = current, Classes = { "search" }, MinWidth = 220, FontFamily = Fonts.Mono };
        body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Rename", Classes = { "muted" }, FontSize = 11 },
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

    /// <summary>Offers to rename the files named after the type with it, checked as the option was left.</summary>
    public void OfferFiles(IReadOnlyList<string> files, bool on)
    {
        Files = files;
        renameFiles = new CheckBox { Content = "Also rename files to reflect this change", IsChecked = on, Focusable = false, FontSize = 12, MinHeight = 0 };
        renameFiles.IsCheckedChanged += (_, _) => RenamesFilesChanged?.Invoke(this, RenamesFiles);
        body.Children.Add(renameFiles);
        body.Children.Add(new TextBlock
        {
            Text = $"Corresponding files to be renamed: {string.Join(", ", files.Select(Path.GetFileName))}",
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        });
        // Measured again now, as it grew, so it is placed for its new size before the next layout.
        InvalidateMeasure();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string text = name.Text?.Trim() ?? string.Empty;
            Done?.Invoke(this, text.Length > 0 ? text : null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Done?.Invoke(this, null);
            e.Handled = true;
        }
    }
}

internal static class Fonts
{
    public static readonly FontFamily Mono = new("avares://Passenger.Editor/Fonts#JetBrains Mono");

    /// <summary>Code as the editor draws it by default, its characters each its own: JetBrains Mono would join "&lt;&gt;" into
    /// a diamond and "=>" into an arrow.</summary>
    public static readonly FontFeatureCollection NoLigatures = [new FontFeature { Tag = "calt", Value = 0 }, new FontFeature { Tag = "liga", Value = 0 }];

    /// <summary>Code's features as Settings' Enable ligatures has the editor draw it: the font's own with it on.</summary>
    public static FontFeatureCollection? Features(bool ligatures) => ligatures ? null : NoLigatures;
}
