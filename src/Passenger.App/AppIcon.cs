using Avalonia.Controls;
using Avalonia.Platform;

namespace Passenger.App;

/// <summary>Every window's icon, which Theme/Shell.axaml's Window style gives each: the icon file's own pictures at each
/// size Windows draws a window's icon (16, 24 and 32 pixels and their scales), taken as they are where one large picture
/// scaled down would blur and fringe. On Linux a window's icon is the file's largest picture; macOS shows none.</summary>
public static class AppIcon
{
    /// <summary>The icon file, Passenger.exe's too (tools/app-icon.sh).</summary>
    public static Uri File { get; } = new("avares://Passenger/Assets/Passenger.ico");

    /// <summary>The one icon every window wears, made once.</summary>
    public static WindowIcon Window { get; } = Made();

    private static WindowIcon Made()
    {
        using Stream file = AssetLoader.Open(File);
        return new WindowIcon(file);
    }
}
