using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>A SpecFlow project's features, as a Reqnroll one's: its bindings read from TechTalk.SpecFlow's attributes (here a
/// library of the solution's own declaring them, as SpecFlow's assembly does, so no package is restored), its steps
/// matched, and a missing step's definition written in SpecFlow's default style, a regular expression, into a new file
/// where no class binds its feature's steps, its lines ending as its .editorconfig asks.</summary>
public sealed class BddSpecFlowTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BddSpecFlowTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bddspecflow-", Write));
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The solution written and given to restore by its folder's real path, so the library the specs reference is
    /// never restored under two spellings at once.</summary>
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
        TestFolders.Write(made, "Shop.Specs/Checkout.feature", "Feature: Checkout\n\nScenario: Pay\n  Given the till opens\n");
        TestFolders.Write(made, "Shop.Specs/.editorconfig", "root = true\n\n[*.cs]\nend_of_line = crlf\n");
        return Path.Combine(PathNames.Canonical(made), "Shop.slnx");
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
            Choose(window, "Define step 'the total is 12'");
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("BasketSteps.cs", StringComparison.Ordinal) == true
                && window.EditorArea.Active.Text.Snapshot.ToString().Contains("[Then(@\"the total is (.*)\")]\n    public void ThenTheTotalIs(int p0)", StringComparison.Ordinal),
                () => $"the step defined as a regular expression: {window.EditorArea.Active?.Text.Snapshot}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_step_of_a_feature_no_class_binds_is_defined_in_a_new_file_whose_lines_end_as_its_editorconfig_asks()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "Shop.Specs", "Checkout.feature")));
            OpenDocument feature = window.EditorArea.Active!;
            string specs = Path.Combine(folder, "Shop.Specs", "Shop.Specs.csproj");
            Ui.WaitFor(() => window.Session.Steps.For(specs) is { Bindings.Count: 1 }, () => $"the SpecFlow project's binding read ({window.Status})", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => feature.Markers.Count == 1, () => $"the missing step marked: {string.Join("; ", feature.Markers.Select(m => m.Message))}");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Given the till");
            Ui.Run(window, AppActions.QuickFix);
            Choose(window, "Define step 'the till opens'");
            string made = Path.Combine(folder, "Shop.Specs", "CheckoutStepDefinitions.cs");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == made, () => $"the new step definitions shown ({window.Status})");
            string written = File.ReadAllText(made);
            Assert.True(written.StartsWith("using TechTalk.SpecFlow;\r\n\r\nnamespace Shop.Specs;\r\n\r\n[Binding]\r\npublic class CheckoutStepDefinitions\r\n{\r\n", StringComparison.Ordinal)
                && written.Replace("\r\n", "", StringComparison.Ordinal).IndexOfAny(['\r', '\n']) < 0,
                $"A new file of step definitions should end its lines as its .editorconfig asks, CRLF: {written.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}");
            Assert.Equal(written, window.EditorArea.Active!.Text.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    private static void Choose(MainWindow window, string label)
    {
        Ui.WaitFor(() => window.Assist.Offered.Contains(label), () => $"'{label}' offered among {string.Join(", ", window.Assist.Offered)}");
        int index = window.Assist.Offered.ToList().IndexOf(label);
        for (int i = 0; i < index; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }
}
