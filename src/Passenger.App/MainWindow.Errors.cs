using Avalonia.Interactivity;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window's share of IDE Internal Errors: the status bar's red mark counting what was caught, its popup,
/// and the C# server's restarts said where the person looks.</summary>
public partial class MainWindow
{
    private void AddErrors()
    {
        IdeErrors.Install();
        IdeErrors.Raised += OnIdeError;
        Closed += (_, _) => IdeErrors.Raised -= OnIdeError;
        ShowErrorCount();
        actions!.Add(new IdeAction(AppActions.ShowLog, ActionTitles.ShowLog(FileManager.Name), ActionGroups.Help, _ => ShowLog())
        {
            Menus = [new MenuPlace("Help", 1, 0)],
        });
    }

    /// <summary>Every window counts it; the one in front says it, where the person looks.</summary>
    private void OnIdeError(object? sender, IdeError error)
    {
        ShowErrorCount();
        if (PassengerApp.Front is not { IsVisible: true } front || front == this)
        {
            Say($"IDE error: {error.Message}", ErrorsPlace);
        }
    }

    /// <summary>The different errors listed, a repeat counting once; more past the list's limit, which only the log has.</summary>
    private void ShowErrorCount()
    {
        int count = IdeErrors.Errors.Count;
        ErrorsButton.IsVisible = count > 0;
        ErrorsCount.Text = IdeErrors.Unlisted > 0 ? $"{Numbers.Said(count)}+" : Numbers.Said(count);
    }

    private void OnErrorsClicked(object? sender, RoutedEventArgs e) => ShowErrors();

    /// <summary>The errors caught since the last Clear All, the latest chosen.</summary>
    public ErrorsPopup ShowErrors()
    {
        ErrorsPopup popup = new(IdeErrors.Errors, IdeErrors.Unlisted);
        popup.LogAsked += (_, _) => ShowLog();
        popup.Done += (_, _) =>
        {
            popups.Close();
            ShowErrorCount();
        };
        popups.Show(popup);
        return popup;
    }

    /// <summary>Help, Show Log in Finder (Explorer on Windows): the log chosen in the logs folder.</summary>
    public void ShowLog()
    {
        IdeErrors.Flush();
        Directory.CreateDirectory(IdeErrors.LogFolder);
        ShowInFileManager(File.Exists(IdeErrors.LogPath) ? IdeErrors.LogPath : IdeErrors.LogFolder);
    }
}
