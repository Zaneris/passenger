using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Passenger.Inspector;

namespace Passenger.TestFrames;

/// <summary>A failing test's windows, drawn into its result: the moment an exception leaves an xunit v3 test on the
/// Avalonia headless session's thread, each window shown, with the popups open over it drawn where they are, is added
/// to the test's attachments as a PNG, before the test's cleanup closes them.</summary>
public static class Frames
{
    /// <summary>What a failing test's output says when its headless app draws nothing to take a frame of.</summary>
    internal const string Undrawn = "Passenger shows a failing test's windows only when its headless app draws them: build it with .UseSkia() and AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }.";

    /// <summary>The tests whose failure was drawn, by xunit's test, so the same exception thrown again in each of xunit's
    /// frames draws it once.</summary>
    private static readonly ConditionalWeakTable<object, object> drawn = new();

    /// <summary>xunit's test context, once the test program has loaded xunit.</summary>
    private static XunitTests? xunit;

    /// <summary>Set while a failure is drawn, whose own exceptions (a window gone) are not failures.</summary>
    [ThreadStatic]
    private static bool drawing;

    public static void Watch()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) => xunit ??= XunitTests.In(loaded.LoadedAssembly);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            xunit ??= XunitTests.In(assembly);
        }
        AppDomain.CurrentDomain.FirstChanceException += Thrown;
    }

    private static void Thrown(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (drawing || xunit is not { } tests)
        {
            return;
        }
        drawing = true;
        try
        {
            if (tests.Running() is { } test && Dispatcher.FromThread(Thread.CurrentThread) is not null && !drawn.TryGetValue(test.Test, out _) && LeavesTheTest())
            {
                drawn.AddOrUpdate(test.Test, test.Test);
                if (!XunitTests.IsSkip(e.Exception))
                {
                    Draw(test);
                }
            }
        }
        catch (Exception error)
        {
            // The test's failure is the test's: what went wrong drawing it is only said beside it, on the error stream.
            Console.Error.WriteLine($"Passenger.TestFrames could not draw the failing test's windows: {error}");
        }
        finally
        {
            drawing = false;
        }
    }

    /// <summary>Whether the exception is leaving the test for xunit: the first frame below it outside the runtime's own is
    /// xunit's runner (its call of the test method, or its await of one), not the test's code or an assertion's.</summary>
    private static bool LeavesTheTest()
    {
        Assembly self = typeof(Frames).Assembly;
        foreach (StackFrame frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method)
            {
                continue;
            }
            Assembly assembly = method.Module.Assembly;
            if (assembly == self || assembly == typeof(object).Assembly || assembly.GetName().Name?.StartsWith("System.", StringComparison.Ordinal) == true)
            {
                continue;
            }
            return assembly.GetName().Name == XunitTests.Core && type.Namespace?.StartsWith("Xunit.v3", StringComparison.Ordinal) == true;
        }
        return false;
    }

    /// <summary>Each window shown, with its popups, as the test's attachments; the test's output says why when the headless
    /// app draws nothing.</summary>
    private static void Draw(XunitTest test)
    {
        List<TopLevel> shown = Shown();
        List<TopLevel> windows = [.. shown.Where(t => t is not PopupRoot)];
        if (windows.Count == 0)
        {
            return;
        }
        try
        {
            // Avalonia.Headless's own check: with its headless drawing there is no frame to take.
            (Headless("GetLastRenderedFrame", windows[0]) as IDisposable)?.Dispose();
        }
        catch (NotSupportedException)
        {
            test.Say(Undrawn);
            return;
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (TopLevel window in windows)
        {
            byte[]? png;
            try
            {
                png = Frame(window, shown);
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
            {
                // Closed as it failed.
                continue;
            }
            if (png is null)
            {
                continue;
            }
            string called = (window as Window)?.Title is { Length: > 0 } title ? title : window.GetType().Name;
            for (int again = 1; again < 100; again++)
            {
                string name = again == 1 ? called : $"{called} ({again})";
                if (names.Add(name) && test.Attach(name, png))
                {
                    break;
                }
            }
        }
    }

    /// <summary>A window's frame as a PNG, the popups open over it drawn into it where they are; null when it has drawn none.</summary>
    private static byte[]? Frame(TopLevel window, IReadOnlyList<TopLevel> shown)
    {
        using WriteableBitmap? frame = Capture(window);
        if (frame is null)
        {
            return null;
        }
        foreach (PopupRoot popup in shown.OfType<PopupRoot>().Where(p => AvaloniaHooks.Owner(p) == window))
        {
            using WriteableBitmap? over = Capture(popup);
            if (over is not null)
            {
                PixelPoint at = PixelPoint.FromPoint(window.PointToClient(popup.PointToScreen(default)), window.RenderScaling);
                Paint(frame, over, at);
            }
        }
        using MemoryStream png = new();
        frame.Save(png, PngBitmapEncoderOptions.Default);
        return png.ToArray();
    }

    /// <summary>The headless platform's frame of a window or popup, rendered anew so it shows what the test left, not
    /// the last frame some step drew.</summary>
    private static WriteableBitmap? Capture(TopLevel top) => Headless("CaptureRenderedFrame", top) as WriteableBitmap;

    /// <summary>One of Avalonia.Headless's extensions of a window, called by name: the hook is compiled against no
    /// Avalonia.Headless, which only a test program carries.</summary>
    private static object? Headless(string method, TopLevel top) =>
        (Type.GetType("Avalonia.Headless.HeadlessWindowExtensions, Avalonia.Headless", throwOnError: true)!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static, [typeof(TopLevel)])
            ?? throw new MissingMethodException($"Avalonia.Headless has no {method}"))
        .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [top], null);

    /// <summary>A popup's pixels over a window's, at its place in them, as much of it as falls inside.</summary>
    private static void Paint(WriteableBitmap frame, WriteableBitmap over, PixelPoint at)
    {
        using ILockedFramebuffer target = frame.Lock();
        using ILockedFramebuffer source = over.Lock();
        if (target.Format != source.Format)
        {
            return;
        }
        int bytes = target.Format.BitsPerPixel / 8;
        int left = Math.Max(0, at.X);
        int right = Math.Min(target.Size.Width, at.X + source.Size.Width);
        if (right <= left)
        {
            return;
        }
        byte[] row = new byte[(right - left) * bytes];
        for (int y = Math.Max(0, at.Y); y < Math.Min(target.Size.Height, at.Y + source.Size.Height); y++)
        {
            Marshal.Copy(source.Address + ((y - at.Y) * source.RowBytes) + ((left - at.X) * bytes), row, 0, row.Length);
            Marshal.Copy(row, 0, target.Address + (y * target.RowBytes) + (left * bytes), row.Length);
        }
    }

    /// <summary>The windows and popups Avalonia's media context draws, those shown: every one the test opened and has not
    /// closed.</summary>
    private static List<TopLevel> Shown() => [.. AvaloniaHooks.Drawn().Where(t => t.IsVisible && t.PlatformImpl is not null)];
}
