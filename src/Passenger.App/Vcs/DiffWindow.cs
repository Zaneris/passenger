using Avalonia.Controls;
using Avalonia.Input;

namespace Passenger.App.Vcs;

/// <summary>A diff in a window of its own over the IDE's, as Rider opens one from the Commit window; Escape closes
/// it.</summary>
public sealed class DiffWindow : Window
{
    public DiffWindow(string title, DiffView view)
    {
        Title = title;
        Width = 1200;
        Height = 760;
        MinWidth = 600;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // A full code surface keeps the editor's ground; form dialogs inherit the shared chrome.
        Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Editor.Background"));
        View = view;
        Content = view;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) => view.Right.Focus();
    }

    public DiffView View { get; }
}
