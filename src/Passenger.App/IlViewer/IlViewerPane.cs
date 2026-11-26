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

    public void Say(string message, string? file = null) => Render(file, message);

    public void Show(CompiledRequest request, CompiledSource result) =>
        Render(Path.GetFileName(request.Source), result.Message, result, request);

    private void Render(string? file, string? message, CompiledSource? result = null, CompiledRequest? request = null)
    {
        CloseOpcodeTip();
        highlighted = null;
        compiled = message is null ? result : null;
        bool hasCode = compiled is not null;
        source.Text = file;
        source.IsVisible = file is not null;
        ToolTip.SetTip(source, hasCode && request is not null ? $"{request.Source}\n{request.Assembly}" : null);
        empty.Text = message;
        empty.IsVisible = !hasCode;
        output.IsVisible = hasCode;

        (output.Highlighter as IDisposable)?.Dispose();
        TextDocument document = new(compiled?.Text ?? string.Empty);
        output.Document = document;
        Language language = hasCode && request?.View == CompiledView.LowLevelCSharp ? Language.CSharp : Language.PlainText;
        output.Highlighter = compiled?.Colours ?? Highlighters.For(language, document);
    }

    public void FollowCaret(TextPosition? caret)
    {
        TextRange? found = caret is { } at ? compiled?.RangeAt(at) : null;
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
}
