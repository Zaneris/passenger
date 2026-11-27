using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Passenger.Dap;
using Passenger.Editor;
using TextRun = Avalonia.Controls.Documents.Run;

namespace Passenger.App.ToolWindows;

/// <summary>At most one page of hex and text cells, both selecting the same byte.</summary>
internal sealed class MemoryByteGrid : ScrollViewer
{
    private const int RowSize = 16;
    private const int HexStart = 28;
    private const int AsciiStart = HexStart + RowSize * 3 + 1;
    private readonly TextBlock[] rows = new TextBlock[16];
    private ulong address;
    private byte[] bytes = [];
    private bool[] changes = [];
    private int selected;
    private int width = 1;

    public MemoryByteGrid()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "Memory bytes");
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        StackPanel lines = new() { Margin = new Thickness(Gaps.Normal) };
        TextBlock heading = Line();
        heading.Text = "Address             Offset  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII";
        heading.Classes.Add(StyleClasses.Muted);
        lines.Children.Add(heading);
        for (int i = 0; i < rows.Length; i++)
        {
            int index = i;
            TextBlock row = rows[i] = Line();
            row.PointerPressed += (_, e) => Pick(row, index, e);
            lines.Children.Add(row);
        }
        Content = lines;
        AddHandler(KeyDownEvent, Move, RoutingStrategies.Tunnel);
    }

    public event EventHandler<int>? ByteSelected;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public void Show(ulong start, byte[] data, bool[] changed, int selection)
    {
        if (address != start)
        {
            Offset = default;
        }
        address = start;
        bytes = data;
        changes = changed;
        selected = selection;
        Paint();
    }

    public void MarkWidth(int count)
    {
        if (width != count)
        {
            int repaint = Math.Max(width, count);
            width = count;
            PaintSelection(selected, repaint);
        }
    }

    private static TextBlock Line()
    {
        TextBlock line = new() { TextWrapping = TextWrapping.NoWrap };
        line.Bind(TextBlock.FontFamilyProperty, line.GetResourceObservable(CodeFonts.Key));
        return line;
    }

    private void Pick(TextBlock row, int index, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }
        TextHitTestResult hit = row.TextLayout.HitTestPoint(e.GetPosition(row));
        if (!hit.IsInside)
        {
            return;
        }
        int position = hit.TextPosition;
        int column = position >= AsciiStart ? position - AsciiStart
            : position >= HexStart && position < AsciiStart - 1 ? (position - HexStart) / 3 : -1;
        if (column >= 0 && column < RowSize && index * RowSize + column < bytes.Length)
        {
            Focus();
            Choose(index * RowSize + column);
            e.Handled = true;
        }
    }

    private void Move(object? sender, KeyEventArgs e)
    {
        int? step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -RowSize, Key.Down => RowSize, _ => null };
        if (step is int distance && bytes.Length > 0)
        {
            Choose(Math.Clamp(selected + distance, 0, bytes.Length - 1));
            rows[selected / RowSize].BringIntoView();
            e.Handled = true;
        }
    }

    private void Choose(int index)
    {
        int before = selected;
        selected = index;
        PaintSelection(before, width);
        if (before / RowSize != index / RowSize || before % RowSize + width > RowSize || index % RowSize + width > RowSize)
        {
            PaintSelection(index, width);
        }
        ByteSelected?.Invoke(this, index);
    }

    private void PaintSelection(int start, int count)
    {
        for (int row = start / RowSize; row <= (start + count - 1) / RowSize && row * RowSize < bytes.Length; row++)
        {
            PaintAt(row);
        }
    }

    private void Paint()
    {
        for (int i = 0; i < rows.Length; i++)
        {
            PaintAt(i);
        }
    }

    private void PaintAt(int index)
    {
        TextBlock row = rows[index];
        int start = index * RowSize;
        row.IsVisible = start < bytes.Length;
        row.Inlines!.Clear();
        if (row.IsVisible)
        {
            PaintRow(row, start, Math.Min(RowSize, bytes.Length - start));
        }
    }

    private void PaintRow(TextBlock row, int start, int count)
    {
        row.Inlines!.Add(new TextRun(string.Create(CultureInfo.InvariantCulture, $"{MemoryAddresses.Show(address + (ulong)start)}  +{start:X5}  ")));
        for (int i = start; i < start + count; i++)
        {
            AddByte(row, i, bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            row.Inlines.Add(new TextRun(" "));
        }
        row.Inlines.Add(new TextRun(new string(' ', (RowSize - count) * 3 + 1)));
        for (int i = start; i < start + count; i++)
        {
            byte value = bytes[i];
            AddByte(row, i, value is >= 32 and <= 126 ? ((char)value).ToString() : ".");
        }
    }

    private void AddByte(TextBlock row, int index, string text)
    {
        TextRun run = new(text);
        if (changes[index])
        {
            run.Bind(TextElement.ForegroundProperty, row.GetResourceObservable(TokenKeys.Memory_Changed));
        }
        if (index >= selected && index < selected + width)
        {
            run.Bind(TextElement.BackgroundProperty, row.GetResourceObservable(index == selected ? TokenKeys.TextSelection : TokenKeys.Selected));
        }
        row.Inlines!.Add(run);
    }
}
