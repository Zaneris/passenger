using Avalonia;
using Avalonia.Headless;
using Passenger.Inspector;

namespace Passenger.App;

/// <summary>The platform a Passenger nobody sees draws on (an agent's <c>--mcp</c>, <c>--headless</c>, a screenshot, the
/// tests): Avalonia's headless platform drawing with real Skia, its text shaped by HarfBuzz, so text measures as on a
/// desktop, with the hotkeys of the system it runs on (<see cref="SystemHotkeys"/>), so its text boxes and lists take the
/// keys a person's take there.</summary>
public static class HeadlessPlatform
{
    public static AppBuilder UseHeadlessPlatform(this AppBuilder builder) =>
        builder.UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithSystemHotkeys();
}
