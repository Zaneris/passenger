using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

internal sealed partial class MarkdownTableEditing
{
    private enum ToolbarRequest { Automatic, Shown, Dismissed }

    private Border toolbar = null!;
    private readonly ContextMenu tableMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private ToolbarRequest toolbarRequest;
    private bool selecting;
    private bool openingMenu;
    private bool toolbarQueued;

    public Canvas ToolbarLayer { get; } = new() { ClipToBounds = true };

    private void MakeToolbar()
    {
        Button bold = FormatButton("B", "Bold", Key.B, bold: true);
        Button italic = FormatButton("I", "Italic", Key.I, bold: false);
        Button table = ToolButtons.Of(new TextBlock { Text = "Table…" }, "Table operations");
        table.Name = "MarkdownTableMenu";
        table.Width = double.NaN;
        table.Padding = new Thickness(Gaps.Small, 0);
        table.ContextMenu = tableMenu;
        table.Click += (_, _) =>
        {
            FillTableMenu(tableMenu);
            openingMenu = true;
            try
            {
                tableMenu.Open(table);
            }
            finally
            {
                openingMenu = false;
                QueueToolbarPosition();
            }
        };
        tableMenu.Closed += (_, _) => QueueToolbarPosition();
        toolbar = new Border
        {
            Name = "MarkdownFormattingToolbar",
            Classes = { StyleClasses.Popup },
            Padding = new Thickness(Gaps.Small, Gaps.Hair),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gaps.Tight,
                Children = { bold, italic, ToolBars.Rule(), table },
            },
        };
        AutomationProperties.SetName(toolbar, "Markdown formatting");
        ToolbarLayer.Children.Add(toolbar);

        editor.SelectionChanged += (_, _) => ToolbarChanged();
        editor.ScrollChanged += (_, _) => QueueToolbarPosition();
        editor.LayoutUpdated += (_, _) => PositionToolbar();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty)
            {
                if (e.NewValue is false && toolbarRequest == ToolbarRequest.Shown && !openingMenu && !tableMenu.IsOpen)
                {
                    toolbarRequest = ToolbarRequest.Automatic;
                }
                QueueToolbarPosition();
            }
        };
        editor.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source == editor && e.GetCurrentPoint(editor).Properties.IsLeftButtonPressed)
            {
                selecting = true;
                ToolbarChanged();
            }
        }, RoutingStrategies.Tunnel);
        editor.AddHandler(InputElement.PointerReleasedEvent, (_, _) => EndSelectionDrag(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.PointerCaptureLost += (_, _) => EndSelectionDrag();
    }

    private Button FormatButton(string label, string name, Key key, bool bold)
    {
        TextBlock mark = new()
        {
            MinWidth = Gaps.Wide,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = bold ? FontStyle.Normal : FontStyle.Italic,
            Text = label,
        };
        Button button = ToolButtons.Of(mark, name, () => Format(bold));
        button.Name = "Markdown" + name;
        ToolButtons.Tip(button, name, KeyWords.Of(new Keystroke(key, Keystroke.Command)));
        return button;
    }

    public void ShowToolbar()
    {
        if (!editor.IsKeyboardFocusWithin)
        {
            editor.Focus();
        }
        toolbarRequest = ToolbarRequest.Shown;
        QueueToolbarPosition();
    }

    private void EndSelectionDrag()
    {
        if (selecting)
        {
            selecting = false;
            QueueToolbarPosition();
        }
    }

    private void ToolbarChanged()
    {
        toolbarRequest = ToolbarRequest.Automatic;
        QueueToolbarPosition();
    }

    private void ResetToolbar()
    {
        toolbarRequest = ToolbarRequest.Automatic;
        selecting = false;
        tableMenu.Close();
        toolbar.IsVisible = false;
    }

    private void QueueToolbarPosition()
    {
        if (toolbarQueued)
        {
            return;
        }
        toolbarQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            toolbarQueued = false;
            PositionToolbar();
        }, DispatcherPriority.Loaded);
    }

    private void PositionToolbar()
    {
        // A table command still targets the cell at the time the menu opened, even while the menu has focus.
        if (openingMenu || tableMenu.IsOpen)
        {
            return;
        }
        if (followed is null || followed != editor.Document || editor.IsReadOnly || !editor.IsEffectivelyVisible
            || !editor.IsKeyboardFocusWithin || selecting || toolbarRequest == ToolbarRequest.Dismissed)
        {
            toolbar.IsVisible = false;
            return;
        }
        Rect viewport = new(ToolbarLayer.Bounds.Size);
        Rect? anchor;
        double? beside = null;
        if (active is { } cell)
        {
            anchor = cell.Host.TranslatePoint(default, ToolbarLayer) is { } point ? new Rect(point, cell.Host.Bounds.Size) : null;
            if (cell.Host.GetVisualParent() is Grid table)
            {
                beside = table.TranslatePoint(new Point(table.Bounds.Width, 0), ToolbarLayer)?.X;
            }
        }
        else
        {
            anchor = SelectionAnchor(viewport);
        }
        if (anchor is { } visible && viewport.Intersects(visible))
        {
            PopupPlacement.PlaceFloatingToolbar(toolbar, ToolbarLayer, visible.Intersect(viewport), beside);
        }
        else
        {
            toolbar.IsVisible = false;
        }
    }

    private Rect? SelectionAnchor(Rect viewport)
    {
        Selection selection = editor.Selections.Primary;
        if (toolbarRequest != ToolbarRequest.Shown && (selection.IsEmpty || editor.Selections.Count != 1))
        {
            return null;
        }
        Rect start = RowAt(selection.Start);
        return viewport.Intersects(start) ? start : RowAt(selection.Caret);

        Rect RowAt(int offset) => new(editor.TranslatePoint(editor.PointOf(offset), ToolbarLayer) ?? default,
            new Size(Gaps.Hair, editor.LineHeight));
    }
}
