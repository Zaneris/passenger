using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>A SpecFlow project's features, as a Reqnroll one's: its bindings read from TechTalk.SpecFlow's attributes (here a
/// library of the solution's own declaring them, as SpecFlow's assembly does, so no package is restored), its steps
/// matched, and a missing step's definition written in SpecFlow's default style, a regular expression.</summary>
public sealed class BddSpecFlowTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BddSpecFlowTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bddspecflow-", Write));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private static string Write(string made)
    {
        TestFolders.Write(made, "Shop.slnx", "<Solution>\n  <Project Path=\"SpecFlow/TechTalk.SpecFlow.csproj\" />\n  <Project Path=\"Shop.Specs/Shop.Specs.csproj\" />\n</Solution>\n");
        TestFolders.Write(made, "SpecFlow/TechTalk.SpecFlow.csproj", TestProjects.Library());
        TestFolders.Write(made, "SpecFlow/Attributes.cs",
            "namespace TechTalk.SpecFlow;\n\n[System.AttributeUsage(System.AttributeTargets.Class)]\npublic class BindingAttribute : System.Attribute\n{\n}\n\n" +
            "public abstract class StepDefinitionBaseAttribute(string? regex) : System.Attribute\n{\n    public string? Regex { get; } = regex;\n}\n\n" +
            "public class GivenAttribute(string? regex) : StepDefinitionBaseAttribute(regex)\n{\n}\n\npublic class WhenAttribute(string? regex) : StepDefinitionBaseAttribute(regex)\n{\n}\n\n" +
            "public class ThenAttribute(string? regex) : StepDefinitionBaseAttribute(regex)\n{\n}\n\npublic class Table\n{\n}\n");
        TestFolders.Write(made, "Shop.Specs/Shop.Specs.csproj", TestProjects.Library().With("  <ItemGroup>\n    <ProjectReference Include=\"../SpecFlow/TechTalk.SpecFlow.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(made, "Shop.Specs/BasketSteps.cs",
            "using TechTalk.SpecFlow;\n\nnamespace Shop.Specs;\n\n[Binding]\npublic class BasketSteps\n{\n    [Given(@\"the basket holds (\\d+) items\")]\n    public void GivenTheBasketHolds(int count)\n    {\n    }\n}\n");
        TestFolders.Write(made, "Shop.Specs/Basket.feature", "Feature: Basket\n\nScenario: Full\n  Given the basket holds 3 items\n  Then the total is 12\n");
        return Path.Combine(made, "Shop.slnx");
    }

    [AvaloniaFact]
    public void A_specflow_projects_steps_are_matched_and_a_missing_one_is_defined_as_a_regular_expression()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "Shop.Specs", "Basket.feature")));
            OpenDocument feature = window.EditorArea.Active!;
            string specs = Path.Combine(folder, "Shop.Specs", "Shop.Specs.csproj");
            Ui.WaitFor(() => window.Session.Steps.For(specs) is { Bindings.Count: 1 }, () => $"the SpecFlow project's binding read ({window.Status})", TimeSpan.FromMinutes(3));
            Assert.True(window.Session.Steps.For(specs)!.IsSpecFlow);
            Ui.WaitFor(() => feature.Markers.Count == 1, () => $"the one missing step marked: {string.Join("; ", feature.Markers.Select(m => m.Message))}");
            Assert.Equal("Then the total is 12", feature.Text.Snapshot.GetText(feature.Markers[0].Range.Start, feature.Markers[0].Range.Length));

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Then the tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Define step 'the total is 12'"), () => $"Define step offered: {string.Join(", ", window.Assist.Offered)}");
            int index = window.Assist.Offered.ToList().IndexOf("Define step 'the total is 12'");
            for (int i = 0; i < index; i++)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("BasketSteps.cs", StringComparison.Ordinal) == true
                && window.EditorArea.Active.Text.Snapshot.ToString().Contains("[Then(@\"the total is (.*)\")]\n    public void ThenTheTotalIs(int p0)", StringComparison.Ordinal),
                () => $"the step defined as a regular expression: {window.EditorArea.Active?.Text.Snapshot}");
        }
        finally
        {
            window.Close();
        }
    }
}
