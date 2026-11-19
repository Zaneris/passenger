using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The menus macOS shows from an object's attached menu: the application's, each window's menu bar and the
/// Dock's. Each object is given one NativeMenu, kept for good, and only that menu's items change after, since Avalonia's
/// macOS exporter of each takes the first NativeMenu it is given and refuses any other ("The menu being updated does not
/// match").</summary>
public static class NativeMenus
{
    /// <summary>The menu <paramref name="owner"/> holds as <paramref name="menu"/>, given it now where it holds none.</summary>
    public static NativeMenu Of(AvaloniaObject owner, AttachedProperty<NativeMenu?> menu)
    {
        if (owner.GetValue(menu) is { } kept)
        {
            return kept;
        }
        NativeMenu made = new();
        owner.SetValue(menu, made);
        return made;
    }

    /// <summary>The menu <paramref name="owner"/> holds as <paramref name="menu"/>, now holding <paramref name="items"/>
    /// alone; one given for the first time is filled first, so its exporter takes it whole.</summary>
    public static NativeMenu Fill(AvaloniaObject owner, AttachedProperty<NativeMenu?> menu, IEnumerable<NativeMenuItemBase> items)
    {
        NativeMenu? kept = owner.GetValue(menu);
        NativeMenu filled = kept ?? new NativeMenu();
        filled.Items.Clear();
        foreach (NativeMenuItemBase item in items)
        {
            filled.Items.Add(item);
        }
        if (kept is null)
        {
            owner.SetValue(menu, filled);
        }
        return filled;
    }
}
