using System.Buffers.Binary;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Dap;

namespace Passenger.App.ToolWindows;

/// <summary>Several typed readings of the bytes at the memory grid's selection.</summary>
internal sealed class MemoryDataInspector : DockPanel
{
    private readonly ToolDropDown endian = new() { ItemsSource = new[] { "Little endian", "Big endian" }, SelectedIndex = 0 };
    private readonly TextBlock address = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(Gaps.Normal, Gaps.Small) };
    private readonly ListBox interpretations = new() { Classes = { StyleClasses.Tree } };
    private byte[] bytes = [];
    private int pointerSize = 8;
    private bool choseEndian;
    private bool settingEndian;

    public MemoryDataInspector()
    {
        AutomationProperties.SetName(endian, "Byte order");
        AutomationProperties.SetName(interpretations, "Data interpretations");
        ToolFlow labels = new() { Margin = new Thickness(Gaps.Small, Gaps.Tight), Spacing = Gaps.Small };
        labels.Children.AddRange([new TextBlock { Text = "Data Inspector", VerticalAlignment = VerticalAlignment.Center }, endian]);
        Children.Add(ToolBars.Top(labels));
        SetDock(address, Dock.Top);
        Children.Add(address);
        Children.Add(interpretations);
        ReusedRows.Use(interpretations, MeaningRow);
        endian.SelectionChanged += (_, _) =>
        {
            if (!settingEndian)
            {
                choseEndian = true;
                Render();
            }
        };
        interpretations.SelectionChanged += (_, _) => WidthSelected?.Invoke(this, (interpretations.SelectedItem as Meaning)?.Size ?? 1);
    }

    public event EventHandler<int>? WidthSelected;

    public void Show(ulong selected, ReadOnlySpan<byte> data, int pointerWidth, bool littleEndian)
    {
        address.Text = MemoryAddresses.Show(selected);
        bytes = data[..Math.Min(8, data.Length)].ToArray();
        pointerSize = pointerWidth == 4 ? 4 : 8;
        if (!choseEndian)
        {
            settingEndian = true;
            endian.SelectedIndex = littleEndian ? 0 : 1;
            settingEndian = false;
        }
        Render();
    }

    private void Render()
    {
        int selected = interpretations.SelectedIndex;
        bool little = endian.SelectedIndex != 1;
        interpretations.ItemsSource = new Meaning[]
        {
            Item("Int8", 1, bits => unchecked((sbyte)bits).ToString(CultureInfo.InvariantCulture)),
            Item("UInt8", 1, Unsigned),
            Item("Int16", 2, bits => unchecked((short)bits).ToString(CultureInfo.InvariantCulture)),
            Item("UInt16", 2, Unsigned),
            Item("Int32", 4, bits => unchecked((int)bits).ToString(CultureInfo.InvariantCulture)),
            Item("UInt32", 4, Unsigned),
            Item("Int64", 8, bits => unchecked((long)bits).ToString(CultureInfo.InvariantCulture)),
            Item("UInt64", 8, Unsigned),
            Item("Float32", 4, bits => BitConverter.Int32BitsToSingle(unchecked((int)bits)).ToString("R", CultureInfo.InvariantCulture)),
            Item("Float64", 8, bits => BitConverter.Int64BitsToDouble(unchecked((long)bits)).ToString("R", CultureInfo.InvariantCulture)),
            Item($"Pointer ({pointerSize * 8}-bit)", pointerSize, bits => pointerSize == 4
                ? string.Create(CultureInfo.InvariantCulture, $"0x{bits:X8}") : MemoryAddresses.Show(bits)),
        };
        interpretations.SelectedIndex = selected;

        Meaning Item(string name, int size, Func<ulong, string> show) => new(name, size,
            bytes.Length < size ? "Not enough readable bytes" : show(UnsignedBits(bytes.AsSpan(0, size), little)));
    }

    private static ulong UnsignedBits(ReadOnlySpan<byte> data, bool little) => (data.Length, little) switch
    {
        (1, _) => data[0],
        (2, true) => BinaryPrimitives.ReadUInt16LittleEndian(data),
        (2, false) => BinaryPrimitives.ReadUInt16BigEndian(data),
        (4, true) => BinaryPrimitives.ReadUInt32LittleEndian(data),
        (4, false) => BinaryPrimitives.ReadUInt32BigEndian(data),
        (_, true) => BinaryPrimitives.ReadUInt64LittleEndian(data),
        _ => BinaryPrimitives.ReadUInt64BigEndian(data),
    };

    private static string Unsigned(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static Control MeaningRow()
    {
        TextBlock name = new() { Classes = { StyleClasses.Muted } };
        TextBlock value = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        SetDock(name, Dock.Left);
        DockPanel row = new() { HorizontalSpacing = Gaps.Normal, Children = { name, value } };
        return row.Showing<Meaning>(meaning =>
        {
            name.Text = meaning.Name;
            value.Text = meaning.Value;
            ToolTip.SetTip(row, meaning.Value);
        });
    }

    private sealed record Meaning(string Name, int Size, string Value);
}
