using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// Main, no text shaping in its build, a headless platform that does not fit its Avalonia, or a builder choosing no
    /// renderer.</summary>
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
        if (Unfit() is { } unfit)
        {
            return unfit;
        }
        AppBuilder app = (AppBuilder)build.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)!;
        if (app.RenderingSubsystemName is null)
        {
            return $"its {Builder}() chooses no renderer (UseSkia, which UsePlatformDetect chooses), and the headless platform draws with the app's own";
        }
        prepared = Headless(app);
        return null;
    }

    /// <summary>Why the headless platform the app is given (its own, else the build Passenger carries for its Avalonia) does
    /// not fit the app's Avalonia, null where it does: each of its types loaded and each method compiled, so what its
    /// Avalonia lacks refuses the app here, saying so, rather than ending it as it starts. A method of its own, compiled only
    /// once <see cref="Prepare"/> has found HarfBuzz, which the platform names, in the app's build.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? Unfit()
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Assembly platform = typeof(AvaloniaHeadlessPlatform).Assembly;
        try
        {
            foreach (Type type in platform.GetTypes().Where(type => !type.ContainsGenericParameters))
            {
                foreach (MethodBase method in type.GetMethods(declared).Concat<MethodBase>(type.GetConstructors(declared)))
                {
                    if (!method.ContainsGenericParameters && method.GetMethodBody() is not null)
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    }
                }
            }
            return null;
        }
        catch (Exception error) when (error is ReflectionTypeLoadException or TypeLoadException or MissingMemberException or FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Exception first = error is ReflectionTypeLoadException loading ? loading.LoaderExceptions.OfType<Exception>().FirstOrDefault() ?? error : error;
            return $"the headless platform built against Avalonia {platform.GetName().Version?.ToString(3)} does not fit its Avalonia {AppAvaloniaContext.ProgramAvalonia().Version?.ToString(3)} ({first.Message})";
        }
    }

    /// <summary>The app's builder on the headless platform, shaping its text with HarfBuzz where it chose nothing to and
    /// taking the system's hotkeys (<see cref="SystemHotkeys"/>): a method of its own, compiled only once
    /// <see cref="Prepare"/> has found HarfBuzz in the app's build.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder Headless(AppBuilder app) =>
        (app.TextShapingSubsystemName is null ? app.UseHarfBuzz() : app).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithSystemHotkeys();

    /// <summary>Runs the app <see cref="Prepare"/> made with the classic desktop lifetime a Main gives it, its arguments the
    /// Main's; its exit code once it ends.</summary>
    public static int Run(string[] args) =>
        (prepared ?? throw new InvalidOperationException("The app's builder was not prepared")).StartWithClassicDesktopLifetime(args);
}
