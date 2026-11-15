using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Bdd;
using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>A Reqnroll solution's steps and their definitions as one thing (<see cref="ReqnrollProjects"/>): the bindings read
/// from the code with no build, a project's own and those of the library it references.</summary>
public sealed class BddTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BddTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bdd-", made => ReqnrollProjects.Write(made)));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string Specs => Path.Combine(folder, "Calc.Specs", "Calc.Specs.csproj");

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature");

    private string StepsFile => Path.Combine(folder, "Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

    /// <summary>The window on the solution, a feature open, its bindings read.</summary>
    private MainWindow Open(out ProjectSteps steps)
    {
        MainWindow window = Ui.OpenFolder(folder);
        Assert.Null(window.OpenFile(FeatureFile));
        Ui.WaitFor(() => window.Session.Steps.For(Specs) is { Bindings.Count: > 0 }, () => $"the specs' bindings read ({window.Status})", TimeSpan.FromMinutes(3));
        steps = window.Session.Steps.For(Specs)!;
        return window;
    }

    [AvaloniaFact]
    public void A_projects_bindings_are_read_from_its_code_and_the_library_it_references_with_no_build()
    {
        MainWindow window = Open(out ProjectSteps steps);
        try
        {
            Assert.False(Directory.EnumerateFiles(folder, "Calc.Specs.dll", SearchOption.AllDirectories).Any(), "The specs were built: bindings are to be read with no build");
            Assert.Equal(
                ["CalculatorStepDefinitions.GivenTheFirstNumberIs(Int32)", "CalculatorStepDefinitions.GivenTheSecondNumberIs(Int32)",
                    "CalculatorStepDefinitions.ThenNothingIsEverSaid()", "CalculatorStepDefinitions.ThenTheResultShouldBe(Int32)",
                    "CalculatorStepDefinitions.WhenTheTwoNumbersAreAdded()", "SharedSteps.Clear()"],
                steps.Bindings.Select(b => b.Shown).Order(StringComparer.Ordinal));
            StepBinding first = steps.Bindings.Single(b => b.Method == "GivenTheFirstNumberIs");
            Assert.Equal((BindingKind.Given, "the first number is {int}"), (first.Kind, first.Expression));
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 10, 16), first.MethodPlace);
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 9, 11), first.ExpressionPlace);
            StepBinding clear = steps.Bindings.Single(b => b.Method == "Clear");
            Assert.Equal((BindingKind.StepDefinition, PathNames.Canonical(Path.Combine(folder, "Calc.Steps", "Calc.Steps.csproj"))), (clear.Kind, clear.Project));
            StepHook hook = Assert.Single(steps.Hooks);
            Assert.Equal(("BeforeScenario", 5, "calc"), (hook.Kind, hook.Order, hook.Scopes.Single().Tag?.TrimStart('@')));
            Assert.Equal(("System.Double", "(\\d+) percent"), (steps.Transformations.Single().ReturnType, steps.Transformations.Single().Regex));
        }
        finally
        {
            window.Close();
        }
    }
}
