using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App;

/// <summary>One layout transform per window, beneath its visual layers so popups can inherit their target's transform
/// without being scaled twice. Fonts, geometry, layout and hit testing all use the same factor; the platform still
/// supplies the screen's DPI scaling. Native system menus and window decorations keep the system's scale.</summary>
internal static class InterfaceScale
{
    /// <summary>The application's resource holding the interface's layout transform.</summary>
    [IdentifierHome]
    public const string Key = "Passenger.InterfaceScale";

    public static void Use(Application app, double factor) => app.Resources[Key] = new ScaleTransform(factor, factor);

    public static void Install()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>((window, e) =>
        {
            if (e.NameScope.Find<VisualLayerManager>("PART_VisualLayerManager") is not { Child: { } content } layers
                || content is LayoutTransformControl)
            {
                return;
            }
            layers.Child = null;
            LayoutTransformControl scale = new() { Child = content };
            double previous = 1;
            scale.PropertyChanged += (_, change) =>
            {
                if (change.Property == LayoutTransformControl.LayoutTransformProperty && scale.LayoutTransform is ScaleTransform next)
                {
                    // Dialogs need room for the same controls at their new size. The main window keeps its user's size.
                    if (window is not MainWindow && window.PlatformImpl is not null)
                    {
                        double ratio = next.ScaleX / previous;
                        Avalonia.Platform.Screen? screen = window.Screens.ScreenFromWindow(window);
                        double width = screen is null ? double.PositiveInfinity : screen.WorkingArea.Width / screen.Scaling - 32;
                        double height = screen is null ? double.PositiveInfinity : screen.WorkingArea.Height / screen.Scaling - 32;
                        if (double.IsFinite(window.Width))
                        {
                            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width * ratio, width));
                        }
                        if (double.IsFinite(window.Height))
                        {
                            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height * ratio, height));
                        }
                    }
                    previous = next.ScaleX;
                }
            };
            layers.Child = scale;
            scale.Bind(LayoutTransformControl.LayoutTransformProperty,
                scale.GetResourceObservable(Key, found => found as ITransform ?? new ScaleTransform(1, 1)));
        });
    }
}
