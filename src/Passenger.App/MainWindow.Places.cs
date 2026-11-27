using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The places, from the navigation history (<see cref="Navigation.Navigator"/>): Last and Next Edit Location
/// step through where the files were edited, Recent Locations lists the places left and edited with their lines of
/// code, and Recently Changed Files (Recent Files with Show edited only) the files edited.</summary>
public partial class MainWindow
{
    private void AddPlaces()
    {
        Add(A.LastEditLocation, "Last Edit Location", "Navigate", _ => navigator.StepEdits(-1), _ => navigator.WhyNotStepEdits(-1), ("Navigate", 3, 6));
        Add(A.NextEditLocation, "Next Edit Location", "Navigate", _ => navigator.StepEdits(1), _ => navigator.WhyNotStepEdits(1), ("Navigate", 3, 7));
        Add(A.RecentChangedFiles, "Recently Changed Files", "IDE", _ => ShowRecentFiles(editedOnly: true), menu: ("View", 2, 1));
        Add(A.RecentLocations, "Recent Locations", "IDE", _ => ShowRecentLocations(), menu: ("View", 2, 2));
    }

    /// <summary>Recent Locations (<see cref="Navigation.Navigator.ShowRecentLocations"/>).</summary>
    public Navigation.RecentLocationsPopup ShowRecentLocations() => navigator.ShowRecentLocations();
}
