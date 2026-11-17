using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>An app nobody is to see (<see cref="Wire.UnseenVariable"/>) whose own Main would draw where people do: its
/// BuildAvaloniaApp(), the builder beside its Main that Avalonia's previewer starts an app with too, is given Avalonia's
/// headless platform with its own renderer's drawing, and the startup hook starts it in place of the Main, so no window of
/// it reaches a screen and the pictures taken of it are its own.</summary>
public static class Unseen
{
    /// <summary>The builder's name, as Avalonia's previewer looks for it on the type of the app's Main.</summary>
    private const string Builder = "BuildAvaloniaApp";

    private static AppBuilder? prepared;

    /// <summary>Makes the app's builder and gives it the headless platform; why it cannot, else null: no builder beside its
    /// Main, one choosing no renderer, or no text shaping in its build.</summary>
    public static string? Prepare()
    {
        MethodInfo? build = Assembly.GetEntryAssembly()?.EntryPoint?.DeclaringType?.GetMethod(Builder, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        if (build is null || !typeof(AppBuilder).IsAssignableFrom(build.ReturnType))
        {
            return $"it has no static {Builder}() answering its AppBuilder beside its Main, which Avalonia's previewer starts an app through too";
        }
        if (StartupHooks.TrustedAssembly("Avalonia.HarfBuzz.dll") is null)
        {
            return "its build carries no Avalonia.HarfBuzz, which shapes its text there";
        }
        AppBuilder app = (AppBuilder)build.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)!;
        if (app.RenderingSubsystemName is null)
        {
            return $"its {Builder}() chooses no renderer (UseSkia, which UsePlatformDetect chooses), and the headless platform draws with the app's own";
        }
        prepared = Headless(app);
        return null;
    }

    /// <summary>The app's builder on the headless platform, shaping its text with HarfBuzz where it chose nothing to and
    /// taking the system's hotkeys (<see cref="SystemHotkeys"/>): a method of its own, compiled only once
    /// <see cref="Prepare"/> has found HarfBuzz in the app's build.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder Headless(AppBuilder app) =>
        (app.TextShapingSubsystemName is null ? app.UseHarfBuzz() : app).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterPlatformServicesSetup(_ => SystemHotkeys());

    /// <summary>Gives the headless platform the hotkeys the app has on its system's own, so its text boxes and the commands
    /// it binds to its platform's command key take Cmd on a Mac as they would on its screen, as an agent's primary does
    /// (<see cref="CommandKey"/>): Avalonia's Apple set where the app's Avalonia has one, else Cmd's with Alt's word moves.</summary>
    private static void SystemHotkeys()
    {
        if (!CommandKey.IsCmd)
        {
            return;
        }
        PlatformHotkeyConfiguration apple = typeof(PlatformHotkeyConfiguration).GetProperty(nameof(PlatformHotkeyConfiguration.Apple), BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null) as PlatformHotkeyConfiguration ?? new PlatformHotkeyConfiguration(KeyModifiers.Meta, wholeWordTextActionModifiers: KeyModifiers.Alt);
        AvaloniaLocator.CurrentMutable.Bind<PlatformHotkeyConfiguration>().ToConstant(apple);
    }

    /// <summary>Runs the app <see cref="Prepare"/> made with the classic desktop lifetime a Main gives it, its arguments the
    /// Main's; its exit code once it ends.</summary>
    public static int Run(string[] args) =>
        (prepared ?? throw new InvalidOperationException("The app's builder was not prepared")).StartWithClassicDesktopLifetime(args);
}
