using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A feature's arguments and table columns checked as Reqnroll converts them at run time, with no build (the
/// Reqnroll solution of <see cref="ReqnrollProjects"/> with a shop's steps beside its calculator's): a step's argument its
/// definition's parameter cannot take, and a table's column no member of the type its definition reads it into takes, each
/// marked where it is written, saying what is taken.</summary>
public sealed class BddArgumentTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Steps =
        "namespace Calc.Specs.StepDefinitions;\n" +
        "\n" +
        "public enum Colour { Red, Green, DarkBlue }\n" +
        "\n" +
        "public sealed class Money { public decimal Amount { get; init; } }\n" +
        "\n" +
        "public sealed class Book { public string Title { get; set; } = \"\"; public int Pages { get; set; } }\n" +
        "\n" +
        "[Binding]\n" +
        "public sealed class ShopSteps\n" +
        "{\n" +
        "    [Given(\"I have {int} apples\")]\n" +
        "    public void GivenApples(int count) { }\n" +
        "\n" +
        "    [Given(@\"the shop opens on (.*)\")]\n" +
        "    public void GivenOpens(DateTime day) { }\n" +
        "\n" +
        "    [Given(\"the colour is {Colour}\")]\n" +
        "    public void GivenColour(Colour colour) { }\n" +
        "\n" +
        "    [Given(@\"the price is (.*)\")]\n" +
        "    public void GivenPrice(Money price) { }\n" +
        "\n" +
        "    [StepArgumentTransformation(@\"\\$(\\d+)\")]\n" +
        "    public Money ToMoney(int amount) => new() { Amount = amount };\n" +
        "\n" +
        "    [Given(\"these books\")]\n" +
        "    public void GivenBooks(Table table) { _ = table.CreateSet<Book>(); }\n" +
        "}\n";

    private const string Feature =
        "Feature: Shop\n" +
        "\n" +
        "Scenario: Shopping\n" +
        "  Given I have two apples\n" +
        "  And the shop opens on 31/01/2024\n" +
        "  And the colour is Purple\n" +
        "  And the price is twelve\n" +
        "  And these books\n" +
        "    | Title | Pagez |\n" +
        "    | Dune  | 412   |\n" +
        "  And I have 3 apples\n" +
        "  And the shop opens on 1/31/2024\n" +
        "  And the colour is dark blue\n" +
        "  And the price is $12\n";

    private readonly string folder;

    public BddArgumentTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bdd-args-", made =>
        {
            string solution = ReqnrollProjects.Write(made);
            TestFolders.Write(made, "Calc.Specs/StepDefinitions/ShopSteps.cs", Steps);
            TestFolders.Write(made, "Calc.Specs/Features/Shop.feature", Feature);
            return solution;
        }));
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void An_int_a_date_an_enum_a_transformation_and_a_table_column_its_definition_cannot_take_are_each_marked_saying_what_it_takes()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "Calc.Specs", "Features", "Shop.feature")));
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.Markers.Count >= 5, () => $"the shop's arguments and column marked: {Said(feature)}", TimeSpan.FromMinutes(3));

            Marker apples = MarkOn(feature, "two");
            Assert.Equal((MarkerKind.Warning, FeatureAnalysis.Undefined), (apples.Kind, apples.Code));
            Assert.Contains("its {int} takes a whole number in digits, which \"two\" is not", apples.Message, StringComparison.Ordinal);

            Marker date = MarkOn(feature, "31/01/2024");
            Assert.Equal((MarkerKind.Error, FeatureAnalysis.Argument), (date.Kind, date.Code));
            Assert.Contains("ShopSteps.GivenOpens(DateTime) takes day as a date (as en writes one, like 1/31/2024), which \"31/01/2024\" is not",
                date.Message, StringComparison.Ordinal);

            Marker colour = MarkOn(feature, "Purple");
            Assert.Equal((MarkerKind.Error, FeatureAnalysis.Argument), (colour.Kind, colour.Code));
            Assert.Contains("takes colour as one of Colour's names (Red, Green or DarkBlue, in any letter case, spaces left out) or a number",
                colour.Message, StringComparison.Ordinal);

            Marker price = MarkOn(feature, "twelve");
            Assert.Equal((MarkerKind.Error, FeatureAnalysis.Argument), (price.Kind, price.Code));
            Assert.Contains("takes price as text matching ^\\$(\\d+)$ (made by ShopSteps.ToMoney), which \"twelve\" is not", price.Message, StringComparison.Ordinal);

            Marker column = MarkOn(feature, "Pagez");
            Assert.Equal((MarkerKind.Warning, FeatureAnalysis.Column), (column.Kind, column.Code));
            Assert.Contains("CreateSet<Book> sets no member from \"Pagez\", so it is passed over: Book has Title and Pages", column.Message, StringComparison.Ordinal);

            Assert.True(feature.Markers.Count == 5, $"Only the five refused were to be marked, and a run takes the rest: {Said(feature)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The one mark over a text of the feature's, where it is first written.</summary>
    private static Marker MarkOn(OpenDocument feature, string text)
    {
        int at = feature.Text.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        return Assert.Single(feature.Markers, m => m.Range.Start == at && m.Range.Length == text.Length);
    }

    private static string Said(OpenDocument feature) =>
        string.Join("; ", feature.Markers.Select(m => $"{feature.Text.Snapshot.GetText(m.Range.Start, m.Range.Length)}: {m.Message}"));
}
