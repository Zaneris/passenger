using Avalonia.Controls;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>The icon of each file and folder by its key (<see cref="Explorer.FileIcons.FileKey"/>, <see
/// cref="Explorer.FileIcons.FolderKey"/>), which names it: answered as asked (Dark or Light) from the application's
/// resources, so the VS Code file icon theme laid with the theme in force draws it by its name, Passenger's own icons the
/// rest (<see cref="Explorer.FileIcons.TryFind"/>). In App.axaml's resources; a key naming no file or folder is not its.</summary>
public sealed class NamedIcons : ResourceProvider
{
    public override bool HasResources => true;

    public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
    {
        value = null;
        return key is string named && Owner is { } owner && Explorer.FileIcons.TryFind(owner, named, theme, out value);
    }
}
