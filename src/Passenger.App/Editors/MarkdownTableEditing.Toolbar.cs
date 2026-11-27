using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

internal sealed partial class MarkdownTableEditing
{
    private enum ToolbarRequest { Automatic, Shown, Dismissed }

    private Border toolbar = null!;
    private Border tableSection = null!;
    private Grid tableTools = null!;
    private Button editSource = null!;
    private Button insertRowAbove = null!;
    private Button deleteRow = null!;
    private Button deleteColumn = null!;
    private ToolbarRequest toolbarRequest;
    private bool selecting;
    private bool toolbarQueued;

    public Canvas ToolbarLayer { get; } = new() { ClipToBounds = true };

    private void MakeToolbar()
    {
        Button bold = FormatButton("B", "Bold", Key.B, bold: true);
        Button italic = FormatButton("I", "Italic", Key.I, bold: false);
        Button insert = TextButton("InsertTable", "Insert Table", "Insert Table", InsertTable);
        editSource = TableButton("EditTableSource", "Edit Markdown", "Edit Table as Markdown", EditSource);
        tableTools = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddTableTools(0, "Rows",
            insertRowAbove = TableButton("InsertRowAbove", "Add Above", "Insert Row Above", spot => AddRow(spot, above: true)),
            TableButton("InsertRowBelow", "Add Below", "Insert Row Below", spot => AddRow(spot)),
            deleteRow = TableButton("DeleteRow", "Delete", "Delete Row", DeleteRow));
        AddTableTools(1, "Columns",
            TableButton("InsertColumnBefore", "Add Before", "Insert Column Before", spot => ChangeColumn(spot, insert: true, after: false)),
            TableButton("InsertColumnAfter", "Add After", "Insert Column After", spot => ChangeColumn(spot, insert: true, after: true)),
            deleteColumn = TableButton("DeleteColumn", "Delete", "Delete Column", spot => ChangeColumn(spot, insert: false, after: false)));
        AddTableTools(2, "Align",
            TableButton("AlignLeft", "Left", "Align Column Left", spot => Align(spot, ":---")),
            TableButton("AlignCenter", "Center", "Align Column Center", spot => Align(spot, ":---:")),
            TableButton("AlignRight", "Right", "Align Column Right", spot => Align(spot, "---:")));

        tableSection = new Border
        {
            BorderThickness = Hairlines.Above,
            Margin = new Thickness(0, Gaps.Tight, 0, 0),
            Padding = new Thickness(0, Gaps.Tight, 0, 0),
            Child = tableTools,
        };
        tableSection.Bind(Border.BorderBrushProperty, tableSection.GetResourceObservable(TokenKeys.Separator));

        toolbar = new Border
        {
            Name = "MarkdownFormattingToolbar",
            Classes = { StyleClasses.Popup },
            Padding = new Thickness(Gaps.Small, Gaps.Hair),
            IsVisible = false,
            Child = new ScrollViewer
            {
                Focusable = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children = { ButtonRow(bold, italic, insert, editSource), tableSection },
                },
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
                if (e.NewValue is false && toolbarRequest == ToolbarRequest.Shown)
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

    private void AddTableTools(int row, string title, params Button[] buttons)
    {
        TextBlock caption = MutedText.Of(title);
        caption.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        WrapPanel words = ButtonRow(buttons);
        Grid.SetRow(caption, row);
        Grid.SetRow(words, row);
        Grid.SetColumn(words, 1);
        tableTools.Children.Add(caption);
        tableTools.Children.Add(words);
    }

    private static WrapPanel ButtonRow(params Button[] buttons)
    {
        WrapPanel row = new() { ItemSpacing = Gaps.Hair };
        row.Children.AddRange(buttons);
        return row;
    }

    private static Button TextButton(string name, string label, string title, Action run)
    {
        Button button = ToolButtons.Of(new TextBlock { Text = label }, title, run);
        button.Name = "Markdown" + name;
        button.Width = double.NaN;
        button.Padding = new Thickness(Gaps.Small, 0);
        return button;
    }

    private Button TableButton(string name, string label, string title, Action<Spot> run) =>
        TextButton(name, label, title, () =>
        {
            if (toolbar.IsVisible && editor.PresentsMarkdown && !editor.IsReadOnly && followed == editor.Document && AtCaret() is { } spot)
            {
                run(spot);
            }
        });

    private void ShowTableTools(Spot? spot)
    {
        tableSection.IsVisible = editSource.IsVisible = spot is not null;
        if (spot is null)
        {
            return;
        }
        string? header = spot.Row == 0 ? "The header row is required" : null;
        EnabledWhen(insertRowAbove, header);
        EnabledWhen(deleteRow, header);
        EnabledWhen(deleteColumn, spot.Entry.Table.Aligns.Count == 1 ? "A table requires a column" : null);

        static void EnabledWhen(Button button, string? why)
        {
            if (button.IsEnabled != (why is null))
            {
                ToolButtons.Grey(button, why);
            }
        }
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
        if (followed is null || followed != editor.Document || editor.IsReadOnly || !editor.IsEffectivelyVisible
            || !editor.IsKeyboardFocusWithin || selecting || toolbarRequest == ToolbarRequest.Dismissed)
        {
            toolbar.IsVisible = false;
            return;
        }
        Rect viewport = new(ToolbarLayer.Bounds.Size);
        Rect? anchor;
        Rect? tableBounds = null;
        if (active is { } cell)
        {
            anchor = cell.Host.TranslatePoint(default, ToolbarLayer) is { } point ? new Rect(point, cell.Host.Bounds.Size) : null;
            if (cell.Host.GetVisualParent() is Grid table && table.TranslatePoint(default, ToolbarLayer) is { } origin)
            {
                tableBounds = new Rect(origin, table.Bounds.Size);
            }
        }
        else
        {
            anchor = SelectionAnchor(viewport);
        }
        if (anchor is { } visible && viewport.Intersects(visible))
        {
            ShowTableTools(AtCaret());
            PopupPlacement.PlaceFloatingToolbar(toolbar, ToolbarLayer, visible.Intersect(viewport), tableBounds);
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
