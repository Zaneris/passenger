using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A folder whose global.json chose a .NET SDK that does not build its project's .NET, as a pin of an old SDK
/// with rollForward major does once the project moves on: the banner, the build's failure and Problems name global.json,
/// what it asks for, the SDK it chose and the SDKs installed that build the project, and open global.json at what it asks
/// for. Without an SDK installed older than the tests' framework, beside one that builds it, the test skips naming it.</summary>
public sealed class GlobalJsonSdkTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("globaljson-"));

    public void Dispose() => TestFolders.Delete(folder);

    private static Version ChannelOf(string sdk) => Version.Parse(string.Join('.', sdk.Split('.')[..2]));

    [AvaloniaFact]
    public void A_build_failing_on_the_SDK_global_json_chose_names_global_json_what_it_asks_and_the_SDK_and_opens_global_json()
    {
        IReadOnlyList<string> sdks = DotnetMachine.Read().Installation?.Sdks ?? [];
        Version framework = DotnetWant.Targeted(TestProjects.Framework)!;
        string? old = sdks.FirstOrDefault(s => ChannelOf(s) < framework);
        Assert.SkipWhen(old is null, $"No .NET SDK older than .NET {framework.ToString(2)} is installed beside the newer ones ({string.Join(", ", sdks)}): "
            + "the test needs one, such as the .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0");
        List<string> builders = [.. sdks.Where(s => ChannelOf(s) >= framework).DistinctBy(ChannelOf)];
        TestFolders.Write(folder, "global.json", $"{{\n  \"sdk\": {{\n    \"version\": \"{old}\",\n    \"rollForward\": \"major\"\n  }}\n}}\n");
        TestFolders.Write(folder, "Old.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);\n");
        string globalJson = Path.Combine(folder, "global.json");
        TextPosition asks = new(2, 0);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            string chose = $"global.json chose it, asking for {old} with rollForward major";
            Ui.WaitFor(() => window.DotnetSaid?.Contains(chose, StringComparison.Ordinal) == true,
                () => $"the banner to say global.json chose the SDK ({window.DotnetSaid}; {window.Status})", TimeSpan.FromMinutes(2));
            string said = window.DotnetSaid!;
            Assert.True(said.StartsWith($"Old targets .NET {framework.ToString(2)}, which the .NET SDK {old} does not build: ", StringComparison.Ordinal)
                && builders.All(b => said.Contains(b, StringComparison.Ordinal)) && said.EndsWith("have global.json ask for " + (builders.Count == 1 ? "it" : "one of them"), StringComparison.Ordinal),
                $"The banner does not name the project's .NET, the SDK global.json chose and the SDKs installed that build it ({string.Join(", ", builders)}): {said}");
            Assert.Null(window.DotnetWant.Install);

            Ui.Run(window, AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding && window.Status?.StartsWith("Build failed", StringComparison.Ordinal) == true,
                () => $"the build to fail on the SDK global.json chose ({window.Status})", TimeSpan.FromMinutes(2));
            Assert.True(window.Status == $"Build failed: {said}", $"The build's failure does not say global.json chose the SDK, in the banner's words: {window.Status}");
            Assert.Equal("open global.json", window.StatusPlace?.Name);
            Problem error = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == SdkChosen.TooOld);
            Assert.True(error.Path == globalJson && error.Position == asks && error.Message.StartsWith(said + "\n", StringComparison.Ordinal),
                $"Problems lists the SDK's error at {error.Where}, not at global.json's version, saying: {error.Message}");

            Ui.Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StatusMessage"));
            Assert.True(window.EditorArea.Active?.Path == globalJson && window.EditorArea.CodeEditor.CaretPosition.Line == asks.Line,
                $"The status bar's link opened {window.EditorArea.Active?.Path} at line {window.EditorArea.CodeEditor.CaretPosition.Line + 1}, not global.json at what it asks for");

            Assert.Null(window.OpenFile(Path.Combine(folder, "Program.cs")));
            Ui.ClickButton(window, "Open global.json");
            Assert.True(window.EditorArea.Active?.Path == globalJson, $"The banner's Open global.json opened {window.EditorArea.Active?.Path}");
        }
        finally
        {
            window.Close();
        }
    }
}
