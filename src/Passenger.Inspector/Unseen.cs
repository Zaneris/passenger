using System.Reflection;
using Avalonia;
using Avalonia.Headless;
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
        prepared = (app.TextShapingSubsystemName is null ? app.UseHarfBuzz() : app).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        return null;
    }

    /// <summary>Runs the app <see cref="Prepare"/> made with the classic desktop lifetime a Main gives it, its arguments the
    /// Main's; its exit code once it ends.</summary>
    public static int Run(string[] args) =>
        (prepared ?? throw new InvalidOperationException("The app's builder was not prepared")).StartWithClassicDesktopLifetime(args);
}
