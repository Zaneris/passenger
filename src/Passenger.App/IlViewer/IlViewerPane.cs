using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.App.IlViewer;

/// <summary>The last build's code beside the active source file.</summary>
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

    public IlViewerPane(Action changeMode, Action refresh)
    {
        AutomationProperties.SetName(output, "Compiled code");
        ToolTip.SetPlacement(output, PlacementMode.Pointer);
        output.Hovered += (_, hover) => ShowOpcodeTip(hover.Offset);
        output.HoverEnded += (_, _) => CloseOpcodeTip();
        output.ScrollChanged += (_, _) => CloseOpcodeTip();
        output.DetachedFromVisualTree += (_, _) => CloseOpcodeTip();
        modes.Add(CompiledView.Il, "IL", "IlViewerIl");
        modes.Add(CompiledView.LowLevelCSharp, "Low-Level C#", "IlViewerCSharp");
        modes.Choice = CompiledView.Il;
        modes.Chosen += (_, _) => changeMode();
        Button reload = ToolButtons.Of(IconKeys.Rerun, "Refresh compiled code", refresh);
        reload.Name = "IlViewerRefresh";
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

    public void Say(string message, string? file = null)
    {
        source.Text = file;
        ToolTip.SetTip(source, null);
        source.IsVisible = file is not null;
        empty.Text = message;
        empty.IsVisible = true;
        output.IsVisible = false;
        compiled = null;
        SetText(string.Empty, Language.PlainText);
    }

    public void Show(CompiledRequest request, CompiledSource result)
    {
        string file = Path.GetFileName(request.Source);
        if (result.Message is { } message)
        {
            Say(message, file);
            return;
        }
        source.Text = file;
        ToolTip.SetTip(source, $"{request.Source}\n{request.Assembly}");
        source.IsVisible = true;
        empty.IsVisible = false;
        output.IsVisible = true;
        compiled = result;
        SetText(result.Text, request.View == CompiledView.LowLevelCSharp ? Language.CSharp : Language.PlainText, result.Colours);
    }

    public void FollowCaret(TextPosition? caret)
    {
        TextRange? found = caret is { } at ? HighlightAt(at) : null;
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

    private TextRange? HighlightAt(TextPosition caret)
    {
        if (compiled is not { } data)
        {
            return null;
        }
        CompiledSequencePoint? nearest = null;
        foreach (CompiledSequencePoint point in data.SequencePoints)
        {
            if (point.Start.CompareTo(caret) <= 0 && caret.CompareTo(point.End) < 0
                && (nearest is null || point.Start.CompareTo(nearest.Value.Start) >= 0 && point.End.CompareTo(nearest.Value.End) <= 0))
            {
                nearest = point;
            }
        }
        if (nearest is { } mapped)
        {
            return mapped.Instructions;
        }

        CompiledSection? found = null;
        foreach (CompiledSection section in data.Sections)
        {
            if (section.FirstLine <= caret.Line && section.LastLine >= caret.Line
                && (found is null || section.LastLine - section.FirstLine < found.Value.LastLine - found.Value.FirstLine))
            {
                found = section;
            }
        }
        return found?.Heading;
    }

    private void ShowOpcodeTip(int offset)
    {
        if (output.Highlighter is not IlColours colours || colours.OpcodeAt(offset) is not { } opcode)
        {
            return;
        }
        StackPanel tip = new()
        {
            MaxWidth = PopupFrame.Narrow,
            Spacing = Gaps.Small,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.Code }, Text = opcode.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = opcode.Description,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = opcode.Description.Length > 0,
                },
                new TextBlock { Classes = { StyleClasses.Caption, StyleClasses.Muted }, Text = opcode.Stack },
            },
        };
        ToolTip.SetTip(output, tip);
        ToolTip.SetIsOpen(output, true);
    }

    private void CloseOpcodeTip()
    {
        if (ToolTip.GetTip(output) is not null)
        {
            ToolTip.SetIsOpen(output, false);
            ToolTip.SetTip(output, null);
        }
    }

    private void SetText(string text, Language language, IlColours? colours = null)
    {
        CloseOpcodeTip();
        highlighted = null;
        (output.Highlighter as IDisposable)?.Dispose();
        TextDocument document = new(text);
        output.Document = document;
        output.Highlighter = colours ?? Highlighters.For(language, document);
    }
}
