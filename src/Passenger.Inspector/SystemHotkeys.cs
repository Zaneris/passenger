using System.Reflection;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>The hotkeys of the system this process runs on, given to Avalonia's headless platform, whose own are Ctrl's on
/// every system: so on a Mac (<see cref="CommandKey"/>) its text boxes, a list's choosing of another row and the commands
/// bound to the platform's command key take Cmd, as on the screen there and as an agent's primary holds. Every headless
/// platform Passenger sets up takes them, an unseen app's (<see cref="Unseen"/>) and Passenger's own, which links this in
/// and is compiled against Avalonia's reference assemblies: what those leave out is reached by its name.</summary>
public static class SystemHotkeys
{
    /// <summary>The builder, its platform's hotkeys made the system's once its application is set up.</summary>
    public static AppBuilder WithSystemHotkeys(this AppBuilder builder) => builder.AfterSetup(built => Take(built.Instance));

    /// <summary>Puts Apple's hotkeys in the platform's own (<see cref="Apple"/>), so whatever has read them reads Apple's;
    /// nothing off Apple's systems, whose hotkeys are the headless platform's own.</summary>
    private static void Take(Application? app)
    {
        if (!CommandKey.IsCmd || app?.PlatformSettings?.HotkeyConfiguration is not { } platform)
        {
            return;
        }
        PlatformHotkeyConfiguration apple = Apple();
        foreach (PropertyInfo hotkey in typeof(PlatformHotkeyConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (hotkey.CanRead && hotkey.CanWrite)
            {
                hotkey.SetValue(platform, hotkey.GetValue(apple));
            }
        }
    }

    /// <summary>Avalonia's Apple set where its Avalonia has one, as its macOS platform takes, else Cmd's with Alt's word
    /// moves, as one without it builds its own.</summary>
    private static PlatformHotkeyConfiguration Apple() =>
        typeof(PlatformHotkeyConfiguration).GetProperty("Apple", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as PlatformHotkeyConfiguration
        ?? (PlatformHotkeyConfiguration)(typeof(PlatformHotkeyConfiguration).GetConstructor([typeof(KeyModifiers), typeof(KeyModifiers), typeof(KeyModifiers)])
            ?? throw new MissingMethodException("Avalonia's PlatformHotkeyConfiguration has neither its Apple set nor the constructor that builds one"))
            .Invoke([KeyModifiers.Meta, KeyModifiers.Shift, KeyModifiers.Alt]);
}
