using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>The styles that stand still what would move of itself, laid over the rest while the system asks for less
/// motion (<see cref="SystemMotion"/>).</summary>
public sealed partial class StillMotion : Styles
{
    public StillMotion()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
