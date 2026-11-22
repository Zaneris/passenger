using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>The Step Library's lists: each definition once however many projects see it, in the order of the kind of step
/// it binds and its phrase, counted by the steps that bind it; hooks in the order they run; the filter's words and @tags.</summary>
public sealed class StepLibraryTests
{
    private const string Int = "System.Int32";

    private static StepBinding Binding(BindingKind kind, string? expression, string method, string? tag = null, params StepParameter[] parameters) =>
        new(kind, expression, method, "Calc.Steps.CalculatorSteps", parameters) { Scopes = tag is null ? [] : [new StepScope(tag, null, null)] };

    private static ProjectSteps Project(string name, IReadOnlyList<StepBinding> bindings, IReadOnlyList<StepHook>? hooks = null) =>
        new($"/src/{name}/{name}.csproj", false, bindings, hooks ?? [], []);

    private static readonly StepBinding First = Binding(BindingKind.Given, "the first number is {int}", "GivenTheFirstNumberIs", parameters: new StepParameter("number", Int));
    private static readonly StepBinding Added = Binding(BindingKind.When, "the two numbers are added", "WhenAdded");
    private static readonly StepBinding Shown = Binding(BindingKind.Then, "the display shows {string}", "ThenShown", "@display", new StepParameter("shown", "System.String"));
    private static readonly StepBinding Cleared = Binding(BindingKind.StepDefinition, "the calculator is cleared", "Clear");
    private static readonly StepBinding Answer = Binding(BindingKind.Then, "the answer is {int}", "ThenAnswer", parameters: new StepParameter("answer", Int));

    [Fact]
    public void The_step_library_lists_each_definition_and_hook_once_counted_by_each_step_and_filtered_by_words_and_tags() => Cases.All(
        ("each definition is listed once by its kind then phrase however many projects see it", () =>
        {
            IReadOnlyList<LibraryStep> listed = StepLibrary.Definitions(
                [Project("Specs", [Shown, Cleared, Answer, Added, First]), Project("Steps", [Cleared with { }])]);
            Assert.Equal(
                ["the first number is [int]", "the two numbers are added", "the answer is [int]", "the display shows \"[string]\"", "the calculator is cleared"],
                listed.Select(s => s.Sample.Text));
            Assert.Equal(["@display"], listed.Single(s => s.Binding == Shown).Tags);
            Assert.All(listed, s => Assert.Null(s.Uses));
        }),
        ("a definition is counted by each step binding it a step bound by two counting for both and an outlines once", () =>
        {
            IReadOnlyList<LibraryStep> listed = StepLibrary.Definitions([Project("Specs", [First, Added, Answer])]);
            StepMatch outline = new(MatchStatus.Defined, [new BoundStep(First, []), new BoundStep(First with { }, [])]);
            StepMatch plain = new(MatchStatus.Defined, [new BoundStep(First, [])]);
            StepMatch ambiguous = new(MatchStatus.Ambiguous, [new BoundStep(First, []), new BoundStep(Added, [])]);
            IReadOnlyList<LibraryStep> counted = StepLibrary.Counted(listed, StepLibrary.BoundBy([outline, plain, ambiguous]));
            Assert.Equal([3, 1, 0], counted.Select(s => s.Uses ?? -1));
        }),
        ("two readings of one definition share its key and another method or expression does not", () =>
        {
            Assert.Equal(StepLibrary.KeyOf(First), StepLibrary.KeyOf(First with { Scopes = [new StepScope("x", null, null)] }));
            Assert.True(First.SameAs(First with { }));
            Assert.NotEqual(StepLibrary.KeyOf(First), StepLibrary.KeyOf(First with { Expression = "the first number is {float}" }));
            Assert.NotEqual(StepLibrary.KeyOf(First), StepLibrary.KeyOf(First with { Method = "Other" }));
            Assert.NotEqual(StepLibrary.KeyOf(Binding(BindingKind.Given, null, "M")), StepLibrary.KeyOf(Binding(BindingKind.Given, "", "M")));
        }),
        ("hooks are listed once in the order they run", () =>
        {
            StepHook after = new("AfterScenario", "Done", "Calc.Steps.Hooks", [], 0);
            StepHook before = new("BeforeScenario", "Ready", "Calc.Steps.Hooks", [new StepScope("@calc", null, null)], 5);
            StepHook earlier = new("BeforeScenario", "Warm", "Calc.Steps.Hooks", [], 1);
            StepHook run = new("BeforeTestRun", "Start", "Calc.Steps.Hooks", [], 0);
            IReadOnlyList<StepHook> listed = StepLibrary.Hooks([Project("Specs", [], [after, before, earlier]), Project("Steps", [], [run, before])]);
            Assert.Equal(["Start", "Warm", "Ready", "Done"], listed.Select(h => h.Method));
            Assert.Equal(["@calc"], StepLibrary.TagsOf(before.Scopes));
            Assert.Equal("Before each scenario", StepLibrary.HookWords(before.Kind));
        }),
        ("the filter keeps what holds every word and is scoped to every tag", () => Cases.Each(
        [
            ("", "the first number is [int]|the two numbers are added|the display shows \"[string]\"", "Ready|Done"),
            ("NUMBER", "the first number is [int]|the two numbers are added", ""),
            ("ThenShown", "the display shows \"[string]\"", ""),
            ("@display first", "", ""),
            ("@calc", "", "Ready"),
        ], row => Filtered(row.Item1, row.Item2, row.Item3))));

    private static void Filtered(string typed, string steps, string hooks)
    {
        StepFilter filter = StepFilter.Parse(typed);
        LibraryStep[] listed = [.. StepLibrary.Definitions([Project("Specs", [First, Added, Shown])])];
        StepHook[] running = [new("BeforeScenario", "Ready", "Calc.Steps.Hooks", [new StepScope("calc", null, null)], 0), new("AfterScenario", "Done", "Calc.Steps.Hooks", [], 0)];
        Assert.Equal(steps, string.Join('|', listed.Where(filter.Fits).Select(s => s.Sample.Text)));
        Assert.Equal(hooks, string.Join('|', running.Where(filter.Fits).Select(h => h.Method)));
        Assert.Equal(typed.Length == 0, filter.IsEmpty);
    }
}
