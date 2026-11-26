using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.App.IlViewer;

/// <summary>The last build's IL and low-level C# of the file shown.</summary>
internal sealed class IlViewerPane : UserControl
{
    private readonly CodeEditor output = new()
    {
        Name = "IlViewerOutput",
        IsReadOnly = true,
        ReadOnlyWhy = Refusals.OnlyToRead,
        ShowErrorStripe = false,
        ShowLineNumbers = false,
        FontSize = TypeRamp.Code,
    };
    private readonly Segments<CompiledView> modes = new();
    private readonly EmptyState empty = new();
    private readonly TextBlock source = new()
    {
        Classes = { StyleClasses.Caption },
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private CompiledSource? compiled;
    private TextRange? highlighted;

    public IlViewerPane(Action changeMode, Action<string> request, Action<CompiledSymbol> navigate)
    {
        AutomationProperties.SetName(output, "Compiled code");
        ToolTip.SetPlacement(output, PlacementMode.Pointer);
        output.Hovered += (_, hover) => ShowOpcodeTip(hover.Offset);
        output.HoverEnded += (_, _) => CloseOpcodeTip();
        output.ScrollChanged += (_, _) => CloseOpcodeTip();
        output.DetachedFromVisualTree += (_, _) => CloseOpcodeTip();
        output.LinkClicked += (_, link) =>
        {
            if (link.Target is CompiledSymbol symbol)
            {
                navigate(symbol);
            }
        };
        modes.Add(CompiledView.Il, "IL", "IlViewerIl");
        modes.Add(CompiledView.LowLevelCSharp, "Low-Level C#", "IlViewerCSharp");
        modes.Choice = CompiledView.Il;
        modes.Chosen += (_, _) => changeMode();
        Button reload = ToolButtons.Of(AppActions.IlViewerRefresh, IconKeys.Rerun, "Refresh compiled code", request);
        ToolFlow bar = new()
        {
            Margin = new Thickness(Gaps.Small, Gaps.Hair),
            Spacing = Gaps.Small,
            Children = { modes, reload, source },
        };
        ToolFlow.SetFills(source, new ToolLayout.Fills(60));
        Content = new DockPanel
        {
            Children = { ToolBars.OverEditor(bar), new Grid { Children = { output, empty } } },
        };
    }

    public CompiledView Mode => modes.Choice;

    private IReadOnlyList<TextLink> LinksOn(TextSnapshot text, int line)
    {
        if (compiled?.Links is not { Length: > 0 } links)
        {
            return [];
        }
        LineSpan row = text.GetLine(line);
        ReadOnlySpan<TextLink> all = links;
        int start = Sorted.FirstAtOrAfter(all, static link => link.Range.Start, row.Start);
        int end = Sorted.FirstAtOrAfter(all, static link => link.Range.Start, row.End);
        return new ArraySegment<TextLink>(links, start, end - start);
    }

    public void Say(string message, string? file = null) => Render(file, message);

    public void Show(CompiledRequest request, CompiledSource result) =>
        Render(Path.GetFileName(request.Source), result.Message, result, $"{request.Source}\n{request.Assembly}");

    private void Render(string? caption, string? message, CompiledSource? result = null, string? captionTip = null)
    {
        CloseOpcodeTip();
        highlighted = null;
        compiled = message is null ? result : null;
        bool hasCode = compiled is not null;
        source.Text = caption;
        source.IsVisible = caption is not null;
        ToolTip.SetTip(source, hasCode ? captionTip ?? caption : null);
        empty.Text = message;
        empty.IsVisible = !hasCode;
        output.IsVisible = hasCode;

        (output.Highlighter as IDisposable)?.Dispose();
        TextDocument document = new(compiled?.Text ?? string.Empty);
        output.Document = document;
        output.Links = compiled?.Links is { Length: > 0 } ? LinksOn : null;
        Language language = hasCode && Mode == CompiledView.LowLevelCSharp ? Language.CSharp : Language.PlainText;
        output.Highlighter = compiled?.Colours ?? Highlighters.For(language, document);
    }

    public void FollowCaret(TextPosition? caret) => Highlight(caret is { } at ? compiled?.RangeAt(at) : null);

    private void Highlight(TextRange? found)
    {
        if (found == highlighted)
        {
            return;
        }
        highlighted = found;
        if (found is { } range)
        {
            output.SetHighlights([range], 0);
            output.GoTo(output.Document.Snapshot.GetPosition(range.Start));
        }
        else
        {
            output.SetHighlights([], -1);
        }
    }

    private void ShowOpcodeTip(int offset)
    {
        if (output.Highlighter is not InstructionColours colours || colours.At(offset) is not { } instruction)
        {
            return;
        }
        OpcodeHelp opcode = instruction.Opcode;
        output.SetUsageHighlights([new UsageHighlight(instruction.Range, IsWrite: false)]);
        StackPanel tip = new()
        {
            MaxWidth = PopupFrame.Narrow,
            Spacing = Gaps.Small,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.Code, StyleClasses.Strong }, Text = opcode.Name },
                new TextBlock
                {
                    Text = opcode.Description,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = opcode.Description.Length > 0,
                },
                new TextBlock { Classes = { StyleClasses.Caption, StyleClasses.Muted }, Text = opcode.Detail, TextWrapping = TextWrapping.Wrap },
            },
        };
        ToolTip.SetTip(output, tip);
        ToolTip.SetIsOpen(output, true);
    }

    private void CloseOpcodeTip()
    {
        if (ToolTip.GetTip(output) is not null)
        {
            output.SetUsageHighlights([]);
            ToolTip.SetIsOpen(output, false);
            ToolTip.SetTip(output, null);
        }
    }
}
