using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Passenger;

/// <summary>A token's brush (<see cref="TokenKeys"/>) as the theme a control, or the app, shows gives it, for code that draws
/// for itself where a binding cannot reach.</summary>
public static class Tokens
{
    /// <summary>The token's brush in <paramref name="host"/>'s theme, or Transparent where the theme gives none.</summary>
    public static IBrush Brush(IResourceHost host, string key) => Find(host, key) ?? Brushes.Transparent;

    /// <summary>The token's brush in <paramref name="host"/>'s theme, or null where the theme gives none.</summary>
    public static IBrush? Find(IResourceHost host, string key) =>
        host.TryFindResource(key, (host as IThemeVariantHost)?.ActualThemeVariant, out object? found) ? found as IBrush : null;
}
