using Passenger.App.CSharp;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>Code Vision's hints after the declarations of the C# file shown.</summary>
    private CodeVisionController? codeVision;

    private void AddCodeVision() => codeVision = new CodeVisionController(new CodeVisionParts(Editors, () => session.CSharp,
        (document, text) => session.Vcs.BlameTextAsync(document, text), action => Perform(action), assist.Arrive, ShowCommitAsync), settingsStore.Effective);
}
