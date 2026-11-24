using System.Reflection;

namespace Passenger.Inspector.WinUI;

/// <summary>A picture of one of a WinUI app's windows as the app itself draws it, through WinUI's own RenderTargetBitmap, so
/// it is the same on the screen, behind other windows or on a desktop of its own: the window's content and all it holds,
/// cropped to an element's part or a part asked for, the scale asked for the most it is drawn at. WinUI draws it on its UI
/// thread in a frame of its own, which the inspector waits for off that thread.</summary>
internal static class Pictures
{
    /// <summary>The longer side of a picture of a whole window when no scale is asked for.</summary>
    private const int Longest = 1600;

    /// <summary>How long WinUI has to draw a picture.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>What a picture draws: the window's content, its size in the window's units, the pixels a unit asked for, and
    /// the part of it kept.</summary>
    private sealed record Plan(object Content, double Width, double Height, double Scale, Box Area);

    public static async Task<Reply> TakeAsync(Inspector inspector, UiQueue ui, Request request)
    {
        try
        {
            Plan plan = await ui.Run(() => PlanOf(inspector, request));
            (object bitmap, Task rendered) = await ui.Run(() =>
            {
                object made = Activator.CreateInstance(Projected.Type("Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap")
                    ?? throw new TypeLoadException("The app's WinUI has no RenderTargetBitmap"))!;
                object rendering = Projected.Call(made, "RenderAsync", plan.Content, (int)Math.Ceiling(plan.Width * plan.Scale), (int)Math.Ceiling(plan.Height * plan.Scale))!;
                return (made, Awaited(rendering, null));
            });
            await rendered.WaitAsync(Patience);
            (Task pixels, int width, int height) = await ui.Run(() => (Awaited(Projected.Call(bitmap, "GetPixelsAsync")!, Projected.Type("Windows.Storage.Streams.IBuffer")),
                Projected.Get(bitmap, "PixelWidth") is int across ? across : 0, Projected.Get(bitmap, "PixelHeight") is int down ? down : 0));
            await pixels.WaitAsync(Patience);
            byte[] bytes = Bytes(pixels.GetType().GetProperty("Result")!.GetValue(pixels)!);
            if (width < 1 || height < 1 || bytes.Length < width * height * 4)
            {
                throw new Refusal("WinUI drew the window empty");
            }
            return new Reply { Id = request.Id, Picture = Cropped(bytes, width, height, plan) };
        }
        catch (Refusal refused)
        {
            return new Reply { Id = request.Id, Error = refused.Message };
        }
        catch (TimeoutException)
        {
            return new Reply { Id = request.Id, Error = $"WinUI drew no picture of the window within {Patience.TotalSeconds:0} s" };
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"{request.Method} {request.Id} failed in the app: {error}");
            return new Reply { Id = request.Id, Error = $"{error.GetType().Name}: {error.Message}" };
        }
    }

    private static Plan PlanOf(Inspector inspector, Request request)
    {
        Visual shown = inspector.Find(request.Element) ?? throw new Refusal(Words.ElementGone);
        Visual top = Inspector.TopOf(shown) ?? throw new Refusal($"{shown} is in no window");
        object? xamlRoot = inspector.ContentObjectOf(top) is { } made ? Projected.Get(made, "XamlRoot") : null;
        object content = (xamlRoot is null ? null : Projected.Get(xamlRoot, "Content")) ?? throw new Refusal($"{top} shows nothing yet");
        double scaling = Projected.Get(xamlRoot!, "RasterizationScale") is double rasterized ? rasterized : 1;
        object? size = Projected.Get(xamlRoot!, "Size");
        double width = Projected.Number(size, "Width");
        double height = Projected.Number(size, "Height");
        if (width < 1 || height < 1)
        {
            throw new Refusal($"{top} is not laid out yet");
        }
        double scale = Math.Clamp(request.Scale ?? Math.Min(scaling, Longest / Math.Max(width, height)), 0.05, 8);
        Box area = request.Area ?? (Inspector.IsWindow(shown) || inspector.ContentOf(top) == shown ? null
            : inspector.ObjectOf(shown) is { } element ? Shown.BoxIn(element) : null) ?? new Box(0, 0, width, height);
        return new Plan(content, width, height, scale, area);
    }

    /// <summary>A WinRT action's or operation's task, through the projection's own AsTask; an operation's of the result type
    /// named.</summary>
    private static Task Awaited(object operation, Type? result)
    {
        MethodInfo[] asTask = Projected.Type("System.WindowsRuntimeSystemExtensions")?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "AsTask" && m.GetParameters().Length == 1).ToArray() ?? [];
        MethodInfo method = result is null
            ? asTask.First(m => !m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType.IsInstanceOfType(operation))
            : asTask.First(m => m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1").MakeGenericMethod(result);
        return (Task)method.Invoke(null, [operation])!;
    }

    /// <summary>An IBuffer's bytes, through the projection's own ToArray.</summary>
    private static byte[] Bytes(object buffer) =>
        (byte[])Projected.CallStatic("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions", "ToArray", buffer)!;

    private static Picture Cropped(byte[] pixels, int drawnWidth, int drawnHeight, Plan plan)
    {
        int stride = drawnWidth * 4;
        double drawnScale = drawnWidth / plan.Width;
        int x = Math.Clamp((int)Math.Floor(plan.Area.X * drawnScale), 0, drawnWidth - 1);
        int y = Math.Clamp((int)Math.Floor(plan.Area.Y * drawnScale), 0, drawnHeight - 1);
        int width = Math.Clamp((int)Math.Ceiling(plan.Area.Width * drawnScale), 1, drawnWidth - x);
        int height = Math.Clamp((int)Math.Ceiling(plan.Area.Height * drawnScale), 1, drawnHeight - y);
        byte[] png = PngFiles.OfBgra(pixels.AsSpan((y * stride) + (x * 4)), width, height, stride);
        return new Picture(png, width, height, drawnScale, new Box(x / drawnScale, y / drawnScale, width / drawnScale, height / drawnScale));
    }
}
