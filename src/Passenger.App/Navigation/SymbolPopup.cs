using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place a symbol popup offers: a type, a member, a file. <see cref="Depth"/> indents it under its
/// container (File Structure's members under their type).</summary>
public sealed record SymbolRow(string Name, string? Detail, string? Where, SymbolKind Kind, string Path, TextPosition Position, int Depth = 0, int? EndLine = null)
{
    /// <summary>Whether a line (from zero) is inside the declaration: the caret's member is chosen when File
    /// Structure opens.</summary>
    public bool Spans(int line) => Position.Line <= line && line <= (EndLine ?? Position.Line);

    /// <summary>Where the language server put it, in its lines, read again in its file as it is gone to, which may have
    /// changed since the row's <see cref="Position"/> was read from it.</summary>
    public Passenger.Lsp.LspPosition? ServerPosition { get; init; }

    /// <summary>A file, not a symbol: shown with its file icon (Go to Everything's files).</summary>
    public const SymbolKind FileKind = default;
}

/// <summary>A query over symbols, answered as it is typed (a moment after the last key, the previous answer given
/// up): Go to Symbol, Go to Everything, File Structure and Recent Files, whose switch (Show edited only) asks again as it
/// turns. Up and Down move, Enter goes, Escape leaves. A search the language server cannot answer fails with a
/// <see cref="LanguageServerFailed"/>, whose message the popup shows.</summary>
public sealed class SymbolPopup : SearchPopup<SymbolRow>
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(120);

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search;
    private readonly Debounce typing;
    private CancellationTokenSource? asking;

    public SymbolPopup(string title, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search, string? initialQuery = null,
        (string Label, bool Checked)? option = null)
        : base(title, placeholder, PopupFrame.Wide, initialQuery, option)
    {
        this.search = search;
        typing = new Debounce(Pause, Refresh);
        IdeErrors.Observe(Refresh());
    }

    /// <summary>The popup's switch turned, before it asks again; its search reads what it is now.</summary>
    public event EventHandler<bool>? OptionChanged;

    /// <summary>The answer to the query as it now is is in.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>Which of the first rows to choose (the caret's member), before anything is typed.</summary>
    public Func<IReadOnlyList<SymbolRow>, int>? Initial { get; init; }

    protected override void QueryChanged() => typing.Restart();

    protected override void OptionTurned()
    {
        OptionChanged?.Invoke(this, IsOptionChecked);
        IdeErrors.Observe(Refresh());
    }

    protected override Control Draw(SymbolRow row) => Row(row);

    private async Task Refresh()
    {
        asking?.Cancel();
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        IsSettled = false;
        BackgroundWork.Begin();
        try
        {
            IReadOnlyList<SymbolRow> found = await search(Query, mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            Show(found, "Nothing matches", Initial is { } initial && Query.Length == 0 ? initial(found) : 0);
            IsSettled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (LanguageServerFailed failed)
        {
            Say(failed.Message);
            IsSettled = true;
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>A symbol's row as every symbol list draws it: its badge, its name and type, where it is.</summary>
    public static Control Row(SymbolRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }
        Control badge;
        if (row.Kind == SymbolRow.FileKind)
        {
            badge = new PathIcon { Data = Explorer.FileIcons.ForFile(row.Path), Foreground = Explorer.FileIcons.BrushForFile(row.Path), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            (string letter, string brush) = Badge(row.Kind);
            badge = KindBadge(letter, brush);
            badge.Margin = new Thickness(0, 0, 8, 0);
        }
        TextBlock where = new()
        {
            [DockPanel.DockProperty] = Dock.Right,
            Text = row.Where ?? string.Empty,
            Foreground = InfoCard.Brush(TokenKeys.TextMuted),
            FontSize = 12,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        StackPanel name = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center },
                // A member's type follows its name close, as "Sum(int): decimal"; a container stands apart.
                new TextBlock
                {
                    Text = row.Detail ?? string.Empty,
                    Foreground = InfoCard.Brush(TokenKeys.TextMuted),
                    FontSize = 12,
                    Margin = new Thickness(row.Detail?.StartsWith(':') == true ? -8 : 0, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 300,
                },
            },
        };
        return new DockPanel { Height = ListRow.Height, Margin = new Thickness(row.Depth * 16, 0, 0, 0), Children = { where, badge, name } };
    }

    /// <summary>A kind's letter on its colour, marking a symbol or a kind of type.</summary>
    public static Control KindBadge(string letter, string brush) => new Border
    {
        Width = 16,
        Height = 16,
        Classes = { "kindBadge" },
        Background = InfoCard.Brush(brush),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = letter,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = InfoCard.Brush(TokenKeys.BadgeText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>The letter and colour of a symbol of each kind (the protocol's SymbolKind).</summary>
    public static (string Letter, string Brush) Badge(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => ("N", TokenKeys.Syntax_Keyword),
        SymbolKind.Class => ("C", TokenKeys.Syntax_Class),
        SymbolKind.Method or SymbolKind.Function => ("m", TokenKeys.Syntax_Method),
        SymbolKind.Property => ("p", TokenKeys.Syntax_Property),
        SymbolKind.Field => ("f", TokenKeys.Syntax_Field),
        SymbolKind.Constructor => ("m", TokenKeys.Syntax_Method),
        SymbolKind.Enum => ("E", TokenKeys.Syntax_Enum),
        SymbolKind.Interface => ("I", TokenKeys.Syntax_Interface),
        SymbolKind.Variable => ("v", TokenKeys.TextMuted),
        SymbolKind.Constant => ("c", TokenKeys.Syntax_Constant),
        SymbolKind.EnumMember => ("e", TokenKeys.Syntax_EnumMember),
        SymbolKind.Struct => ("S", TokenKeys.Syntax_Struct),
        SymbolKind.Event => ("e", TokenKeys.Syntax_Event),
        SymbolKind.Operator => ("o", TokenKeys.TextMuted),
        SymbolKind.TypeParameter => ("T", TokenKeys.Syntax_TypeParameter),
        _ => ("·", TokenKeys.TextMuted),
    };
}
