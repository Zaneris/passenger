using Passenger.App.CSharp;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>Code Spyglass's hints after the declarations of the C# file shown.</summary>
    private CodeSpyglassController? codeSpyglass;

    private void AddCodeSpyglass() => codeSpyglass = new CodeSpyglassController(new CodeSpyglassParts(Editors, () => session.CSharp,
        (document, text) => session.Vcs.BlameTextAsync(document, text), action => Perform(action), assist.Arrive, ShowCommitAsync), settingsStore.Effective);
}
